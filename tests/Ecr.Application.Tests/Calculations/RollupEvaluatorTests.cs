using Ecr.Application.Calculations;
using Ecr.Application.Templates;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>Чиста функція Rollup (D-230, схеми — ПРИПУЩЕННЯ). Кожен тест фіксує одну мутацію.</summary>
public sealed class RollupEvaluatorTests
{
    private static RelationRow Row(string key, string unit, decimal? fact)
        => new(key, new Dictionary<string, decimal?> { ["Fact"] = fact },
            new Dictionary<string, string?> { ["Unit"] = unit });

    private static RelationRow Target(string key, string unit)
        => new(key, new Dictionary<string, decimal?>(), new Dictionary<string, string?> { ["Unit"] = unit });

    private static readonly RelationMatchSpec ByUnit = new([new RelationKey("Unit", "Unit")]);

    private static RollupSpec Spec(RollupAggregate a) => new("Fact", "Total", a);

    private static decimal? One(RollupAggregate a, IReadOnlyList<RelationRow> src, byte? scale = null)
        => Assert.Single(RollupEvaluator.Evaluate(ByUnit, Spec(a), src, [Target("T1", "A")], scale).Writes).Value;

    [Fact]
    public void Sum_а_не_Avg()
    {
        var src = new[] { Row("1", "A", 1m), Row("2", "A", 2m), Row("3", "A", 6m) };
        Assert.Equal(9m, One(RollupAggregate.Sum, src));
    }

    [Fact]
    public void Avg_а_не_Sum()
    {
        var src = new[] { Row("1", "A", 1m), Row("2", "A", 2m), Row("3", "A", 6m) };
        Assert.Equal(3m, One(RollupAggregate.Avg, src));
    }

    [Fact]
    public void Min_Max_і_Count()
    {
        var src = new[] { Row("1", "A", 4m), Row("2", "A", -2m), Row("3", "A", null) };
        Assert.Equal(-2m, One(RollupAggregate.Min, src));
        Assert.Equal(4m, One(RollupAggregate.Max, src));
        Assert.Equal(2m, One(RollupAggregate.Count, src));
    }

    [Fact]
    public void Null_значення_не_входять_у_середнє()
    {
        var src = new[] { Row("1", "A", 2m), Row("2", "A", null), Row("3", "A", 4m) };
        Assert.Equal(3m, One(RollupAggregate.Avg, src));
    }

    [Fact]
    public void Порожнє_джерело_дає_null_для_sum_avg_min_max_і_нуль_для_count()
    {
        foreach (var a in new[] { RollupAggregate.Sum, RollupAggregate.Avg, RollupAggregate.Min, RollupAggregate.Max })
        {
            Assert.Null(One(a, []));
        }

        Assert.Equal(0m, One(RollupAggregate.Count, []));
        Assert.Null(One(RollupAggregate.Sum, [Row("1", "A", null)]));
    }

    [Fact]
    public void Округлення_до_Scale_приймача_AwayFromZero()
    {
        Assert.Equal(0.13m, One(RollupAggregate.Sum, [Row("1", "A", 0.125m)], scale: 2));
        Assert.Equal(-0.13m, One(RollupAggregate.Sum, [Row("1", "A", -0.125m)], scale: 2));
        Assert.Equal(0.125m, One(RollupAggregate.Sum, [Row("1", "A", 0.125m)], scale: null));
        Assert.Equal(1m, One(RollupAggregate.Sum, [Row("1", "A", 0.5m)], scale: 0));
    }

    [Fact]
    public void Count_не_округлюється()
        => Assert.Equal(2m, One(RollupAggregate.Count, [Row("1", "A", 1m), Row("2", "A", 1m)], scale: 0));

    [Fact]
    public void Дублі_ключів_джерела_усі_входять_в_агрегат()
    {
        var src = new[] { Row("1", "A", 1m), Row("2", "A", 1m), Row("3", "B", 100m) };
        Assert.Equal(2m, One(RollupAggregate.Sum, src));
    }

    [Fact]
    public void Кожен_рядок_приймача_бере_лише_свою_групу_а_без_пари_дає_порожнє()
    {
        var src = new[] { Row("1", "A", 1m), Row("2", "B", 10m) };
        var r = RollupEvaluator.Evaluate(ByUnit, Spec(RollupAggregate.Sum), src,
            [Target("TA", "A"), Target("TB", "B"), Target("TZ", "Z")], null);

        Assert.Equal([1m, 10m, null], r.Writes.Select(w => w.Value).ToArray());
        Assert.Equal(["TA", "TB", "TZ"], r.Writes.Select(w => w.TargetRowKey).ToArray());
        Assert.All(r.Writes, w => Assert.Equal("Total", w.TargetColumn));
    }

    [Fact]
    public void Дублі_ключів_приймача_кожен_отримує_агрегат_групи()
    {
        var r = RollupEvaluator.Evaluate(ByUnit, Spec(RollupAggregate.Sum), [Row("1", "A", 5m)],
            [Target("T1", "A"), Target("T2", "A")], null);
        Assert.Equal([5m, 5m], r.Writes.Select(w => w.Value).ToArray());
    }

    [Fact]
    public void Порожній_ключ_не_дорівнює_порожньому_ключу()
    {
        var r = RollupEvaluator.Evaluate(ByUnit, Spec(RollupAggregate.Sum), [Row("1", "", 5m)], [Target("T1", "")], null);
        Assert.Null(Assert.Single(r.Writes).Value);
    }

    [Fact]
    public void Складений_ключ_вимагає_збігу_всіх_пар_і_не_плутає_межі()
    {
        var match = new RelationMatchSpec([new RelationKey("Unit", "Unit"), new RelationKey("Fact", "Fact")]);
        var src = new[]
        {
            new RelationRow("1", new Dictionary<string, decimal?> { ["Fact"] = 2m }, new Dictionary<string, string?> { ["Unit"] = "A" }),
            new RelationRow("2", new Dictionary<string, decimal?> { ["Fact"] = 3m }, new Dictionary<string, string?> { ["Unit"] = "A" }),
        };
        var tgt = new RelationRow("T", new Dictionary<string, decimal?> { ["Fact"] = 2m }, new Dictionary<string, string?> { ["Unit"] = "A" });

        var r = RollupEvaluator.Evaluate(match, Spec(RollupAggregate.Count), src, [tgt], null);
        Assert.Equal(1m, Assert.Single(r.Writes).Value);
    }

    [Fact]
    public void Порожній_keys_однорядковий_приймач_бере_усі_джерела()
    {
        var r = RollupEvaluator.Evaluate(new RelationMatchSpec([]), Spec(RollupAggregate.Sum),
            [Row("1", "A", 1m), Row("2", "B", 2m)], [Target("ONLY", "x")], null);
        Assert.Equal(3m, Assert.Single(r.Writes).Value);
        Assert.Null(r.SkippedReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Порожній_keys_і_приймач_не_з_одного_рядка_нічого_не_вгадує(int targets)
    {
        var tgt = Enumerable.Range(0, targets).Select(i => Target($"T{i}", "x")).ToList();
        var r = RollupEvaluator.Evaluate(new RelationMatchSpec([]), Spec(RollupAggregate.Sum), [Row("1", "A", 1m)], tgt, null);
        Assert.Empty(r.Writes);
        Assert.Equal("ambiguousTarget", r.SkippedReason);
    }

    [Fact]
    public void Count_рахує_й_непорожній_текст()
    {
        var spec = new RollupSpec("Unit", "Total", RollupAggregate.Count);
        var r = RollupEvaluator.Evaluate(new RelationMatchSpec([]), spec, [Row("1", "A", null), Row("2", "", null)], [Target("T", "x")], null);
        Assert.Equal(1m, Assert.Single(r.Writes).Value);
    }

    [Fact]
    public void Recalculator_обходить_зв_язки_за_кодом_і_пропускає_відсутні_таблиці()
    {
        var rel = new RollupRelation("B", 1, 2, ByUnit, Spec(RollupAggregate.Sum), null);
        var missing = new RollupRelation("A", 1, 99, ByUnit, Spec(RollupAggregate.Sum), null);
        var rows = new Dictionary<int, IReadOnlyList<RelationRow>>
        {
            [1] = [Row("1", "A", 4m)],
            [2] = [Target("T", "A")],
        };

        var w = new RelationRecalculator().ComputeRollups([rel, missing], rows);

        var one = Assert.Single(w);
        Assert.Equal("B", one.RelationCode);
        Assert.Equal(2, one.TargetTableDefId);
        Assert.Equal(4m, one.Write.Value);
        Assert.Empty(new RelationRecalculator().ComputeRollups([], rows));
    }
}
