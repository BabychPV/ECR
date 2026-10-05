using Ecr.Application.Calculations;
using Ecr.Application.Templates;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-03: переповнення decimal у Rollup/Check — значення, а не виняток
/// (виняток валив увесь перерахунок через одну пару рядків).
/// </summary>
public sealed class RelationOverflowTests
{
    private const decimal HalfMax = 50_000_000_000_000_000_000_000_000_000m;

    [Fact]
    public void Rollup_переповнення_суми_дає_порожнє_значення_з_ознакою()
    {
        Assert.Null(RollupEvaluator.Aggregate(RollupAggregate.Sum, [HalfMax, HalfMax], targetScale: 2, out var overflow));
        Assert.True(overflow);
    }

    [Fact]
    public void Rollup_середнє_що_вміщується_не_губиться_на_переповненій_сумі()
    {
        Assert.Equal(HalfMax, RollupEvaluator.Aggregate(RollupAggregate.Avg, [HalfMax, HalfMax], targetScale: null, out var overflow));
        Assert.False(overflow);
    }

    [Fact]
    public void Переповнення_Rollup_пише_попередження_з_кодом_зв_язку()
    {
        var logger = new ListLogger();
        var row = new RelationRow("S1", new Dictionary<string, decimal?> { ["Fact"] = HalfMax }, new Dictionary<string, string?>());
        var target = new RelationRow("T1", new Dictionary<string, decimal?>(), new Dictionary<string, string?>());
        var relation = new RollupRelation(
            "REL_SUM", 1, 2, new RelationMatchSpec([]), new RollupSpec("Fact", "Total", RollupAggregate.Sum), null);

        var writes = new RelationRecalculator(logger: logger).ComputeRollups(
            [relation],
            new Dictionary<int, IReadOnlyList<RelationRow>> { [1] = [row, row], [2] = [target] });

        var write = Assert.Single(writes).Write;
        Assert.Null(write.Value);
        Assert.True(write.Overflow);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("REL_SUM", entry.Message, StringComparison.Ordinal);
    }

    private sealed class ListLogger : ILogger<RelationRecalculator>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData(new[] { 1, 1, -1 }, 1)]
    [InlineData(new[] { 1, 1, -1, -1, 1 }, 1)]
    [InlineData(new[] { -1, -1, 1 }, -1)]
    public void Rollup_сума_різних_знаків_не_переповнюється_на_проміжному_кроці(int[] signs, int expectedSign)
    {
        // Рев'ю AN-38, P3-10: [max, max, −max] — справжня сума max, а не «переповнення».
        var values = signs.Select(s => (decimal?)(s * decimal.MaxValue)).ToList();

        Assert.Equal(expectedSign * decimal.MaxValue, RollupEvaluator.Aggregate(RollupAggregate.Sum, values, targetScale: null, out var overflow));
        Assert.False(overflow);
    }

    [Fact]
    public void Rollup_справжнє_переповнення_суми_різних_знаків_лишається_переповненням()
    {
        Assert.Null(RollupEvaluator.Aggregate(
            RollupAggregate.Sum, [decimal.MaxValue, decimal.MaxValue, -1m], targetScale: null, out var overflow));
        Assert.True(overflow);
    }

    [Fact]
    public void Rollup_велика_сума_без_переповнення_рахується()
        => Assert.Equal(HalfMax, RollupEvaluator.Aggregate(RollupAggregate.Sum, [HalfMax, 0m], targetScale: null));

    [Fact]
    public void Check_переповнення_відхилення_це_провал_а_не_виняток()
    {
        var spec = new CheckSpec("L", "R", 0.1m, CheckToleranceKind.Abs, CheckSeverity.Warn);

        var r = CheckEvaluator.Evaluate(spec, HalfMax, -HalfMax);

        Assert.True(r.Compared);
        Assert.False(r.Passed);
        Assert.Equal(decimal.MaxValue, r.Deviation);
    }

    [Fact]
    public void Check_переповнення_відносного_допуску_насичується_і_пара_проходить()
    {
        var spec = new CheckSpec("L", "R", 10m, CheckToleranceKind.Rel, CheckSeverity.Warn);

        var r = CheckEvaluator.Evaluate(spec, HalfMax, HalfMax);

        Assert.True(r.Passed);
        Assert.Equal(decimal.MaxValue, r.Allowed);
    }
}
