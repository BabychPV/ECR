// tests/Ecr.Application.Tests/Calculations/ModuleProfileEvaluatedTablesTests.cs
using Ecr.Application.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// AN-69: профіль несе екземпляри таблиць, чиї рядки зіставлено з правилами (для заміни <c>calc.CalculationIssue</c>),
/// і НЕ пише їх, як і методологію рядка, у <c>ModulesProfileJson</c>.
/// </summary>
public sealed class ModuleProfileEvaluatedTablesTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Зведення_профілів_об_єднує_перераховані_таблиці_без_дублів()
    {
        var a = new ModuleProfile();
        a.RecordEvaluatedTables([1, 2]);
        var b = new ModuleProfile();
        b.RecordEvaluatedTables([2, 3]);

        a.Merge(b);

        Assert.Equal([1L, 2L, 3L], a.EvaluatedTables.Order().ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Перераховані_таблиці_і_методологія_рядка_не_потрапляють_у_JSON_профілю()
    {
        var profile = new ModuleProfile();
        profile.RecordEvaluatedTables([42]);
        profile.RecordUnmatched([new UnmatchedRow(42, 2, "R2", 900, "SECRET_METHODOLOGY")]);

        var json = profile.ToJson();

        Assert.DoesNotContain("SECRET_METHODOLOGY", json, StringComparison.Ordinal);
        Assert.Equal("SECRET_METHODOLOGY", Assert.Single(profile.UnmatchedRows).MethodologyCode);
    }
}
