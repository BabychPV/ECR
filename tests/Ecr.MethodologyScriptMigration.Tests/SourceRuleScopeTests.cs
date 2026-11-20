using Ecr.Application.Calculations.Scripting.Migration;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// F13 (D-556, коло 4): правило, прив'язане до таблиці, не відбирає рядки іншої таблиці речення; правило без таблиці діє
/// для кожної. Мутація: прибрати фільтр за <c>TableCodes</c> у <c>SourceRule.For</c> - червоний.
/// </summary>
public sealed class SourceRuleScopeTests
{
    private static SourceRule Rule(string column, params string[] tables)
        => new([new SourceMatchPair(column, SourceMatchValue.OfText("x"))], tables.Length == 0 ? null : tables);

    [Fact]
    public void Правило_діє_лише_для_своєї_таблиці_а_непривязане_для_всіх()
    {
        var r1 = Rule("A", "T1");
        var r2 = Rule("B", "T2");
        var r3 = Rule("C");
        var r4 = new SourceRule([], []);
        IReadOnlyList<SourceRule> rules = [r1, r2, r3, r4];

        Assert.Equal([r1, r3, r4], SourceRule.For("T1", rules));
        Assert.Equal([r2, r3, r4], SourceRule.For("T2", rules));
        Assert.Equal([r3, r4], SourceRule.For("T3", rules));
        Assert.Equal([r1, r3, r4], SourceRule.For("T1", [r1, r3, r4]));
    }
}
