using Ecr.Application.Calculations;
using Ecr.Application.Templates;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-03: переповнення decimal у Rollup/Check — значення, а не виняток
/// (виняток валив увесь перерахунок через одну пару рядків).
/// </summary>
public sealed class RelationOverflowTests
{
    private const decimal HalfMax = 50_000_000_000_000_000_000_000_000_000m;

    [Theory]
    [InlineData(RollupAggregate.Sum)]
    [InlineData(RollupAggregate.Avg)]
    public void Rollup_переповнення_суми_дає_порожнє_значення(RollupAggregate aggregate)
        => Assert.Null(RollupEvaluator.Aggregate(aggregate, [HalfMax, HalfMax], targetScale: 2));

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
