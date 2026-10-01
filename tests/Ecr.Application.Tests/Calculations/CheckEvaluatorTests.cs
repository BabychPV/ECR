using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>Чиста функція Check і знахідки панелі валідації (D-230, схеми — ПРИПУЩЕННЯ).</summary>
public sealed class CheckEvaluatorTests
{
    private static CheckSpec Spec(string tol, CheckToleranceKind kind = CheckToleranceKind.Abs, CheckSeverity sev = CheckSeverity.Warn)
        => new("Fact", "Total", decimal.Parse(tol, System.Globalization.CultureInfo.InvariantCulture), kind, sev);

    [Theory]
    [InlineData("10", "10", "0", true)]
    [InlineData("10", "10.5", "0.5", true)]   // рівність допуску — Pass (≤, не <)
    [InlineData("10", "10.51", "0.5", false)]
    [InlineData("10.5", "10", "0.5", true)]   // знак відхилення не важливий
    [InlineData("9.49", "10", "0.5", false)]
    [InlineData("10", "10.01", "0", false)]
    public void Abs_допуск_включно_і_за_модулем(string left, string right, string tol, bool pass)
    {
        var r = CheckEvaluator.Evaluate(Spec(tol), decimal.Parse(left, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(right, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(r.Compared);
        Assert.Equal(pass, r.Passed);
    }

    [Fact]
    public void Rel_допуск_рахується_від_модуля_right_а_не_left()
    {
        // 1% від |right|=200 -> 2; відхилення 2 -> Pass; від left=202 було б 2.02 — інше місце межі.
        var spec = Spec("0.01", CheckToleranceKind.Rel);
        Assert.True(CheckEvaluator.Evaluate(spec, 202m, 200m).Passed);
        Assert.False(CheckEvaluator.Evaluate(spec, 202.01m, 200m).Passed);
        Assert.Equal(2m, CheckEvaluator.Evaluate(spec, 202m, 200m).Allowed);
        Assert.True(CheckEvaluator.Evaluate(spec, -202m, -200m).Passed);
    }

    [Fact]
    public void Abs_і_Rel_дають_різний_результат_на_тих_самих_числах()
    {
        Assert.False(CheckEvaluator.Evaluate(Spec("0.01", CheckToleranceKind.Abs), 202m, 200m).Passed);
        Assert.True(CheckEvaluator.Evaluate(Spec("0.01", CheckToleranceKind.Rel), 202m, 200m).Passed);
    }

    [Fact]
    public void Rel_при_right_нуль_вимагає_точної_рівності()
    {
        Assert.True(CheckEvaluator.Evaluate(Spec("0.5", CheckToleranceKind.Rel), 0m, 0m).Passed);
        Assert.False(CheckEvaluator.Evaluate(Spec("0.5", CheckToleranceKind.Rel), 0.01m, 0m).Passed);
    }

    [Fact]
    public void Порожнє_значення_не_порівнюється()
    {
        Assert.False(CheckEvaluator.Evaluate(Spec("0"), null, 1m).Compared);
        Assert.False(CheckEvaluator.Evaluate(Spec("0"), 1m, null).Compared);
        Assert.True(CheckEvaluator.Evaluate(Spec("0"), null, null).Passed);
    }

    private static RelationRow Row(string key, string unit, decimal? fact = null, decimal? total = null)
        => new(key,
            new Dictionary<string, decimal?> { ["Fact"] = fact, ["Total"] = total },
            new Dictionary<string, string?> { ["Unit"] = unit });

    private static readonly RelationMatchSpec ByUnit = new([new RelationKey("Unit", "Unit")]);

    [Fact]
    public void Failures_зіставляє_за_ключем_і_віддає_лише_порушення()
    {
        var source = new[] { Row("s1", "A", fact: 10m), Row("s2", "B", fact: 5m), Row("s3", "C", fact: 7m) };
        var target = new[] { Row("tA", "A", total: 10m), Row("tB", "B", total: 9m) };

        var f = CheckEvaluator.Failures(ByUnit, Spec("0"), source, target);

        var one = Assert.Single(f);
        Assert.Equal("tB", one.TargetRowKey);
        Assert.Equal("s2", one.SourceRowKey);
        Assert.Equal(4m, one.Result.Deviation);
    }

    [Fact]
    public void Failures_порожній_keys_лише_для_однорядкового_приймача()
    {
        var source = new[] { Row("s1", "A", fact: 1m), Row("s2", "A", fact: 2m) };
        var none = new RelationMatchSpec([]);

        Assert.Equal(2, CheckEvaluator.Failures(none, Spec("0"), source, [Row("t", "x", total: 100m)]).Count);
        Assert.Empty(CheckEvaluator.Failures(none, Spec("0"), source, [Row("t1", "x", total: 100m), Row("t2", "x", total: 100m)]));
    }

    [Theory]
    [InlineData(CheckSeverity.Block, ValidationSeverity.Error)]
    [InlineData(CheckSeverity.Warn, ValidationSeverity.Warning)]
    [InlineData(CheckSeverity.Info, ValidationSeverity.Info)]
    public void Серйозність_відображається_у_рівень_панелі_і_не_блокує_збереження(CheckSeverity s, ValidationSeverity expected)
    {
        var spec = Spec("0", sev: s);
        var f = CheckEvaluator.Failures(ByUnit, spec, [Row("s", "A", fact: 1m)], [Row("t", "A", total: 2m)]);

        var m = Assert.Single(CheckEvaluator.ToMessages("REL1", 42, spec, f, "en"));

        Assert.Equal(expected, m.Severity);
        Assert.Equal("REL-REL1", m.RuleCode);
        Assert.Equal(42, m.TableDefId);
        Assert.Equal("t", m.RowKey);
        Assert.Equal("Total", m.ColumnCode);
        Assert.False(m.BlocksSave);
    }

    [Theory]
    [InlineData("en", "deviation 1")]
    [InlineData("ru", "отклонение 1")]
    [InlineData("kz", "ауытқу 1")]
    public void Текст_знахідки_за_мовою_запиту(string lang, string fragment)
    {
        var spec = Spec("0");
        var f = CheckEvaluator.Failures(ByUnit, spec, [Row("s", "A", fact: 1m)], [Row("t", "A", total: 2m)]);
        Assert.Contains(fragment, Assert.Single(CheckEvaluator.ToMessages("R", 1, spec, f, lang)).Message);
    }

    // ── RelationCheckRunner: від комірок до знахідок ────────────────────────────

    private sealed class World
    {
        public required ITemplateVersionStore Store { get; init; }
        public required ICellStore Cells { get; init; }
        public required IRowStore Rows { get; init; }
        public required IMetadataCache Metadata { get; init; }
        public required IReadOnlyList<TableInstanceRef> Instances { get; init; }
    }

    private static readonly PeriodKey Period = PeriodKey.Parse(202601);

    private static World Build(string? mapJson, bool active = true, TableRelationKind kind = TableRelationKind.Check,
        decimal sourceFact = 10m, decimal targetTotal = 10.4m)
    {
        var b = new TemplateBuilder { TemplateVersionId = 7 };
        var sheet = b.Sheet("S");
        var src = b.Table(sheet, "SRC");
        var tgt = b.Table(sheet, "TGT");
        var srcFact = b.Column(src, "Fact");
        var srcUnit = b.Column(src, "Unit", CellDataType.String);
        var tgtTotal = b.Column(tgt, "Total");
        var tgtUnit = b.Column(tgt, "Unit", CellDataType.String);

        var relation = new TableRelationDef(
            Ecr.Domain.ValueObjects.EcrCode.Create("REL1"), src.Id, tgt.Id, kind,
            """{"keys":[{"source":"Unit","target":"Unit"}]}""");
        relation.Update(src.Id, tgt.Id, kind, relation.MatchJson, mapJson, 0, active);

        var store = Substitute.For<ITemplateVersionStore>();
        store.ListTableRelationsAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<TableRelationDef> { relation });

        var srcInstance = new TableInstanceRef(100, 1, src.Id, 7, Period.Value);
        var tgtInstance = new TableInstanceRef(200, 1, tgt.Id, 7, Period.Value);
        var ids = new List<long> { 100, 200 };

        var rows = Substitute.For<IRowStore>();
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [100] = new Dictionary<string, long> { ["R1"] = 1001 },
                [200] = new Dictionary<string, long> { ["R1"] = 2001 },
            });

        CellRecord Num(long row, int col, int table, decimal v)
            => new(new CellAddress(Period, row, col), table, new CellValueData { ValueNumeric = v });
        CellRecord Str(long row, int col, int table, string v)
            => new(new CellAddress(Period, row, col), table, new CellValueData { ValueString = v });

        var cells = Substitute.For<ICellStore>();
        cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [100] = new[] { Num(1001, srcFact.Id, src.Id, sourceFact), Str(1001, srcUnit.Id, src.Id, "U") },
                [200] = new[] { Num(2001, tgtTotal.Id, tgt.Id, targetTotal), Str(2001, tgtUnit.Id, tgt.Id, "U") },
            });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(7, Arg.Any<CancellationToken>()).Returns(b.Build());

        return new World { Store = store, Cells = cells, Rows = rows, Metadata = metadata, Instances = [srcInstance, tgtInstance] };
    }

    private static Task<IReadOnlyList<ValidationMessage>> Run(World w, string lang = "en")
        => RelationCheckRunner.RunAsync(w.Store, w.Cells, w.Rows, w.Metadata, w.Instances, Period, lang, CancellationToken.None);

    private const string Block = """{"left":"Fact","right":"Total","tolerance":"0.1","toleranceKind":"abs","severity":"Block"}""";

    [Fact]
    public async Task Runner_віддає_Error_для_Block_коли_відхилення_більше_допуску()
    {
        var m = Assert.Single(await Run(Build(Block)));
        Assert.Equal(ValidationSeverity.Error, m.Severity);
        Assert.Equal("R1", m.RowKey);
        Assert.Equal("Total", m.ColumnCode);
    }

    [Fact]
    public async Task Runner_нічого_не_віддає_в_межах_допуску()
        => Assert.Empty(await Run(Build(Block, targetTotal: 10.1m)));

    [Theory]
    [InlineData(false, TableRelationKind.Check)]
    [InlineData(true, TableRelationKind.Rollup)]
    [InlineData(true, TableRelationKind.Mirror)]
    public async Task Runner_без_активного_Check_не_читає_ні_комірок_ні_рядків(bool active, TableRelationKind kind)
    {
        var w = Build(Block, active, kind);

        Assert.Empty(await Run(w));

        await w.Cells.DidNotReceiveWithAnyArgs().ReadSlicesAsync(default!, default(PeriodKey), default);
        await w.Rows.DidNotReceiveWithAnyArgs().GetRowIdsBatchAsync(default!, default, default);
    }

    [Fact]
    public async Task Runner_пропускає_зв_язок_зі_збереженою_раніше_хибною_схемою()
        => Assert.Empty(await Run(Build("""{"left":"Fact","right":"Total","severity":"Fatal"}""")));
}
