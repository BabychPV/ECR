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
        // `Sqrt` існує, але лише в методологіях: набір Template закритий
        // (02b §7). ⚠ Раніше тут стояв CONVERT — він перейшов у діалект
        // шаблонів за Q-066: у чинному шаблоні 216 формул конвертують одиниці
        // діленням на 1000, і без CONVERT їм нема куди мігрувати.
        //
        // ⛔ І раніше тут стояло `SQRT(4)` — у ВЕРХНЬОМУ регістрі, з
        // твердженням, що діалект методологій його приймає. Це була неправда
        // про чинну систему (`Q-082`): у NCalc 1.3.8 `EvaluateOptions.IgnoreCase`
        // не виставлений, тож `SQRT` там — невідома функція, а корінь пишеться
        // `Sqrt`. Тест зеленів, бо звірявся з нашим вигаданим набором.
        var result = Expr.Parse("Sqrt(4)", ExpressionDialect.Template);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == "ECR-TMPL-0422");
        Assert.True(Expr.Parse("Sqrt(4)", ExpressionDialect.Methodology).IsSuccess);

        // ⛔ А ВЕРХНІЙ регістр не приймає ЖОДЕН із діалектів: у шаблоні такої
        // функції немає взагалі, у методології — немає саме в такому написанні.
        Assert.False(Expr.Parse("SQRT(4)", ExpressionDialect.Template).IsSuccess);
        Assert.False(Expr.Parse("SQRT(4)", ExpressionDialect.Methodology).IsSuccess);

        // А CONVERT тепер доступний в обох діалектах.
        Assert.True(Expr.Parse("CONVERT(1, 't', 'kg')", ExpressionDialect.Template).IsSuccess);

        var unknown = Expr.Parse("VLOOKUP(1, 2, 3)", ExpressionDialect.Methodology);
        Assert.False(unknown.IsSuccess);
        Assert.Contains(unknown.Diagnostics, d => d.Code == "ECR-TMPL-0422");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.5")]
    public void Вигаданий_набір_у_діалекті_методологій_відхиляється()
    {
        // ⛔ Шість імен, які наш власний `FunctionRegistry` приймав до кроку
        // `I.14`. Формула з ними проходила публікацію і рахувалася — при тому
        // що чинний рушій (NCalc 1.3.8) жодного з них не знає. Це не «зайва
        // суворість тепер»: це числа, звірені ні з чим, тоді (`Q-082`).
        foreach (var expression in new[]
                 {
                     "POWER(2, 3)",
                     "SWITCH(1, 1, 2, 3)",
                     "COALESCE(1, 2)",
                     "MOD(5, 3)",
                     "TRUNC(1.7)",
                     "IFERROR(1, 2)",
                 })
        {
            var result = Expr.Parse(expression, ExpressionDialect.Methodology);

            Assert.False(result.IsSuccess, $"{expression} мав бути відхилений");
            Assert.Contains(result.Diagnostics, d => d.Code == ExpressionErrors.Syntax);
        }
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("POW(2, 3)", "Pow")]
    [InlineData("ROUND(1.5, 0)", "Round")]
    [InlineData("POWER(2, 3)", "Pow(a, b)")]
    [InlineData("MOD(5, 3)", "оператор %")]
    [InlineData("SWITCH(1, 1, 2, 3)", "вкладені if")]
    public void Відмова_називає_чим_саме_заміняти(string expression, string expected)
    {
        // ⚠ «Невідома функція» відправила б методолога шукати те, чого нема.
        // Кожен із цих промахів має рівно одну правильну поправку, і вона
        // мусить бути в тексті: `POW` — описка регістру, `POWER` і `SWITCH` —
        // наш власний вигаданий набір, який ці формули приймав.
        var result = Expr.Parse(expression, ExpressionDialect.Methodology);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains(expected, StringComparison.Ordinal));
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
