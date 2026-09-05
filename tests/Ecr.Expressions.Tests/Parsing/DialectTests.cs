// tests/Ecr.Expressions.Tests/Parsing/DialectTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Два діалекти, один парсер (ФВ-9.5). Різниця — у **дозволених** посиланнях
/// і функціях, а не в граматиці.
/// </summary>
/// <remarks>
/// Третього діалекту немає: рядковий фільтр гранта виведено з обсягу (D-92)
/// саме тому, що вимагав би окремої граматики і компіляції в SQL-предикат.
/// </remarks>
public sealed class DialectTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("@Arg")]
    [InlineData("CST.DENSITY")]
    [InlineData("!OtherFormula")]
    [Trait("Requirement", "ФВ-9.5")]
    public void Конструкції_методологій_заборонені_в_діалекті_шаблонів(string token)
    {
        var template = Expr.Parse(token, ExpressionDialect.Template);
        var methodology = Expr.Parse(token, ExpressionDialect.Methodology);

        Assert.False(template.IsSuccess);
        Assert.Contains(template.Diagnostics, d => d.Code == ExpressionErrors.Syntax);

        // Та сама конструкція в «своєму» діалекті розбирається без зауважень —
        // отже, заборона саме діалектна, а не синтаксична.
        Assert.True(methodology.IsSuccess,
            string.Join("; ", methodology.Diagnostics.Select(d => d.Message)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_комірки_заборонені_в_діалекті_методологій()
    {
        // Методологія працює з підготовленими аргументами, а не лізе в документ
        // сама. Це межа, яка робить її переносною між шаблонами.
        var result = Expr.Parse("[Water_07].[Main].[7001001].[Jan]", ExpressionDialect.Methodology);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("методолог", StringComparison.Ordinal));

        Assert.True(Expr.Parse("[Water_07].[Main].[7001001].[Jan]", ExpressionDialect.Template).IsSuccess);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функція_поза_набором_діалекту_дає_ECR_TMPL_0422()
    {
        // SQRT існує, але лише в методологіях: набір Template закритий
        // (02b §7). ⚠ Раніше тут стояв CONVERT — він перейшов у діалект
        // шаблонів за Q-066: у чинному шаблоні 216 формул конвертують одиниці
        // діленням на 1000, і без CONVERT їм нема куди мігрувати.
        var result = Expr.Parse("SQRT(4)", ExpressionDialect.Template);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == "ECR-TMPL-0422");
        Assert.True(Expr.Parse("SQRT(4)", ExpressionDialect.Methodology).IsSuccess);

        // А CONVERT тепер доступний в обох діалектах.
        Assert.True(Expr.Parse("CONVERT(1, 't', 'kg')", ExpressionDialect.Template).IsSuccess);

        var unknown = Expr.Parse("VLOOKUP(1, 2, 3)", ExpressionDialect.Methodology);
        Assert.False(unknown.IsSuccess);
        Assert.Contains(unknown.Diagnostics, d => d.Code == "ECR-TMPL-0422");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функції_поточного_часу_заборонені_в_обох_діалектах()
    {
        // ⚠ Час береться ЛИШЕ з календарного контексту (02b §10). Інакше
        // результат перерахунку залежав би від дня, коли його запустили, і
        // звірка з еталоном стала б неможливою в принципі.
        foreach (var dialect in new[] { ExpressionDialect.Template, ExpressionDialect.Methodology })
        {
            foreach (var name in new[] { "NOW()", "TODAY()", "RAND()" })
            {
                var result = Expr.Parse(name, dialect);

                Assert.False(result.IsSuccess, $"{name} мав бути відхилений у діалекті {dialect}");
                Assert.Contains(result.Diagnostics, d => d.Code == ExpressionErrors.Syntax);
            }
        }
    }
}
