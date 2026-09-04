// tests/Ecr.Expressions.Tests/Parsing/DialectTests.cs
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
    public void Конструкції_методологій_заборонені_в_діалекті_шаблонів(string token)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_комірки_заборонені_в_діалекті_методологій()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функція_поза_набором_діалекту_дає_ECR_TMPL_0422()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функції_поточного_часу_заборонені_в_обох_діалектах()
        => Assert.Fail("not implemented");
}
