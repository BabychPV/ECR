// tests/Ecr.Expressions.Tests/Functions/AggregateFunctionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Одинадцять функцій діалекту шаблонів на **порожній множині** і з `null`.
/// </summary>
/// <remarks>
/// В агрегатах `null` **поглинається**, у бінарних операціях —
/// **поширюється** (`02b-expressions.md` §6). Це різні правила навмисно:
/// сума трьох місяців, де
/// один порожній, має дорівнювати сумі двох; а `A + B` з невідомим `B`
/// невідоме, і мовчазний нуль тут був би неправильним числом.
/// </remarks>
public sealed class AggregateFunctionTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("COUNT")]
    public void Агрегат_на_порожній_множині_має_визначений_результат(string function)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_поглинається_в_агрегатах()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_поширюється_в_бінарних_операціях()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_дає_діагностику_а_не_виняток()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVG_ігнорує_null_а_не_рахує_його_нулем()
        => Assert.Fail("not implemented");
}
