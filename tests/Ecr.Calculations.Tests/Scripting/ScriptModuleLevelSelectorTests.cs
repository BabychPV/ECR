using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>MS Етап 7: скрипт рівня Module без <c>rows</c> — селектор віддає усі рядки таблиці.</summary>
public sealed class ScriptModuleLevelSelectorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Модульний_рівень_бере_усі_рядки_таблиці_а_звичайний_скрипт_без_збігу_нічого()
    {
        var stand = new ScriptStand("rows from OTHER;\noutput Y: kg = 1;", NumericMode.Strict);
        stand.AddTable("T1").Column("A")
            .Row("R1", ("A", 1m))
            .Row("F_X", ("A", 2m));
        var context = await stand.Module.PrepareAsync(
            stand.Methodology, ScriptStand.DocumentId, new(ScriptStand.PeriodValue), CancellationToken.None);
        var runtime = (ScriptRuntime)context.ScriptRuntime!;

        var plain = await stand.Selector.MatchAsync(
            context.Script!, stand.Main.InstanceId, runtime.EntryIdText, CancellationToken.None);
        var module = await stand.Selector.MatchAsync(
            context.Script!, stand.Main.InstanceId, runtime.EntryIdText, CancellationToken.None, allRows: true);

        Assert.Empty(plain.Matches);
        Assert.Equal(["R1", "F_X"], module.Matches.Select(m => m.RowKey).ToArray());
        Assert.All(module.Matches, m => Assert.Equal("T1", m.RuleCode));
        Assert.Equal(["R1", "F_X"], module.AllRowKeys);
        Assert.NotNull(module.Read);
        Assert.Empty(module.Excluded);
    }
}
