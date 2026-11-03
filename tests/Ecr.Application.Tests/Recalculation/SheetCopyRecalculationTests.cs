// tests/Ecr.Application.Tests/Recalculation/SheetCopyRecalculationTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): перерахунок бачить документ «видом» — таблиці аркуша-копії беруться зі
/// своїх екземплярів, усі інші — з оригіналів; каскад від копії лишається всередині копії; копія читає ту саму копію
/// ІНШОГО періоду.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (локально, не комітиться): у <c>CopyScope.Apply</c> віддавати лише оригінали (відображати всі
/// таблиці на оригінал) — червоніють усі тести цього класу, що очікують запис у рядок копії.
/// </remarks>
public sealed class SheetCopyRecalculationTests
{
    private const long DocumentId = 700;
    private const int Version = 1;
    private const long CopyId = 77;
    private const int Year = 2026;

    // Екземпляри за періодом: оригінал T1, копія T1 (№77), оригінал T2.
    private static long Orig1(int seq) => (seq * 100L) + 1;

    private static long Copy1(int seq) => (seq * 100L) + 2;

    private static long Orig2(int seq) => (seq * 100L) + 3;

    private static long RowOf(long instance) => instance * 10;

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();

    private TableDef _t1 = null!;
    private TableDef _t2 = null!;
    private int _a;
    private int _tot;
    private int _y;
    private int _prev;
    private int _sheet1;

    private static PeriodKey Key(int sequence) => PeriodKey.Create(Year, sequence);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Повний_перерахунок_рахує_копію_зі_своїх_комірок_а_інший_аркуш_з_оригіналу()
    {
        Arrange(includePrev: false);

        var written = await Service().RecalculateAllAsync(DocumentId, Key(5), CancellationToken.None);

        var applied = Applied();
        Assert.Equal(3, written);
        Assert.Equal(50m, ValueAt(applied, Orig1(5), _tot));    // оригінал: A=5 × 10
        Assert.Equal(500m, ValueAt(applied, Copy1(5), _tot));   // копія №77: A=50 × 10 — її власні комірки
        Assert.Equal(5m, ValueAt(applied, Orig2(5), _y));       // інший аркуш читає ОРИГІНАЛ (5), а не копію (50)
        Assert.Equal(3, applied.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Правка_в_копії_перераховує_лише_копію_і_не_чіпає_оригінал_та_інший_аркуш()
    {
        Arrange(includePrev: false);

        var written = await Service().RecalculateAsync(
            Copy1(5), Dirty(5, Copy1(5)), CancellationToken.None);

        var applied = Applied();
        Assert.Equal(1, written);
        Assert.Equal(500m, ValueAt(applied, Copy1(5), _tot));
        Assert.DoesNotContain(applied, c => c.Address.TableRowId == RowOf(Orig1(5)));
        Assert.DoesNotContain(applied, c => c.Address.TableRowId == RowOf(Orig2(5)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Правка_в_оригіналі_не_чіпає_копію_а_інший_аркуш_бачить_оригінал()
    {
        Arrange(includePrev: false);

        var written = await Service().RecalculateAsync(
            Orig1(5), Dirty(5, Orig1(5)), CancellationToken.None);

        var applied = Applied();
        Assert.Equal(2, written);
        Assert.Equal(50m, ValueAt(applied, Orig1(5), _tot));
        Assert.Equal(5m, ValueAt(applied, Orig2(5), _y));
        Assert.DoesNotContain(applied, c => c.Address.TableRowId == RowOf(Copy1(5)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Міжперіодне_читання_копії_бере_ту_саму_копію_іншого_періоду()
    {
        Arrange(includePrev: true);

        var written = await Service().RecalculateAllAsync(DocumentId, Key(5), CancellationToken.None);

        var applied = Applied();
        Assert.Equal(5, written);
        Assert.Equal(4m, ValueAt(applied, Orig1(5), _prev));    // [Period:-1].[A] оригіналу: період 4, A=4
        Assert.Equal(40m, ValueAt(applied, Copy1(5), _prev));   // копії: період 4, A=40 (не оригінал)
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Зміна_іншого_періоду_перераховує_читача_і_оригінала_і_копії_кожного_з_власним_джерелом()
    {
        Arrange(includePrev: true);

        var written = await Service().RecalculateFromOtherPeriodsAsync(
            DocumentId, Key(5), [new CrossPeriodSource(4, _t1.Id, _a)], CancellationToken.None);

        var applied = Applied();
        Assert.Equal(2, written);
        Assert.Equal(4m, ValueAt(applied, Orig1(5), _prev));
        Assert.Equal(40m, ValueAt(applied, Copy1(5), _prev));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Перерахунок_під_поданням_копії_пише_лише_копію_і_не_бере_блокування_ще_раз()
    {
        Arrange(includePrev: false);

        var written = await Service().RecalculateSheetUnderSubmitLockAsync(
            DocumentId, _sheet1, Key(5), CancellationToken.None, sheetCopyId: CopyId);

        var applied = Applied();
        Assert.Equal(1, written);
        Assert.Equal(500m, ValueAt(applied, Copy1(5), _tot));

        // Копію тримає викликач (подання) — ні оригінал, ні та сама копія повторно не блокуються.
        await _gate.DidNotReceiveWithAnyArgs().EnterEditAsync(default, default, default, default);
        await _gate.DidNotReceiveWithAnyArgs().EnterCopyEditAsync(default, default, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Decision", "RS-1103")]
    public async Task Запис_у_копію_бере_спільне_блокування_саме_копії_а_подана_копія_не_пишеться()
    {
        Arrange(includePrev: false);
        _gate.EnterCopyEditAsync(DocumentId, _sheet1, Arg.Any<PeriodKey>(), CopyId, Arg.Any<CancellationToken>())
            .Returns(DocumentStatus.Submitted);

        var written = await Service().RecalculateAsync(Copy1(5), Dirty(5, Copy1(5)), CancellationToken.None);

        Assert.Equal(0, written);   // копія подана → прогін її не пише (оригінал Draft цього не змінює)
        await _gate.Received().EnterCopyEditAsync(
            DocumentId, _sheet1, Arg.Any<PeriodKey>(), CopyId, Arg.Any<CancellationToken>());
    }

    private static decimal? ValueAt(IReadOnlyList<CellRecord> applied, long instance, int column)
        => applied.Single(c => c.Address.TableRowId == RowOf(instance) && c.Address.ColumnDefId == column)
            .Value.ValueNumeric;

    private DirtySet Dirty(int sequence, long instance)
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Key(sequence), RowOf(instance), _a));

        return dirty;
    }

    private IReadOnlyList<CellRecord> Applied()
        => [.. _cells.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync))
            .SelectMany(c => (IReadOnlyCollection<CellChangeSet>)c.GetArguments()[0]!)
            .SelectMany(set => set.Upserts)];

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 1, 31)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            Substitute.For<IRegistryStore>(), headers, Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(Year, 2, 1, 0, 0, 0, DateTimeKind.Utc)), _uow, _gate);
    }

    /// <summary>
    /// Аркуш S1 (таблиця T1: A — вхід, Tot = A × 10, Prev = [Period:-1].[A]) має копію №77; аркуш S2 (T2: Y = T1.R1.A).
    /// Вхід A: оригінал — період × 1 (4 → 4, 5 → 5), копія — період × 10 (4 → 40, 5 → 50).
    /// </summary>
    private void Arrange(bool includePrev)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var s1 = builder.Sheet("S1");
        _sheet1 = s1.Id;
        _t1 = builder.Table(s1, "T1");
        _a = builder.Column(_t1, "A").Id;
        _tot = builder.Column(_t1, "Tot").Id;
        _prev = builder.Column(_t1, "Prev").Id;
        builder.Row(_t1, "R1", 1);
        builder.Formula(_t1, "[A] * 10", column: _t1.Columns.Single(c => c.Id == _tot));

        var s2 = builder.Sheet("S2");
        _t2 = builder.Table(s2, "T2");
        _y = builder.Column(_t2, "Y").Id;
        builder.Row(_t2, "R1", 1);
        builder.Formula(_t2, "[S1].[T1].[R1].[A]", column: _t2.Columns.Single(c => c.Id == _y));

        if (includePrev)
        {
            builder.Formula(_t1, "[Period:-1].[A]", column: _t1.Columns.Single(c => c.Id == _prev));
        }

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        var totFormula = _t1.Formulas.Single(f => f.ColumnDefId == _tot);
        var yFormula = _t2.Formulas.Single();
        List<FormulaDependency> dependencies =
        [
            FormulaDependency.ForFormula(totFormula.Id, 0, _t1.Id, "R1", _a, null, null, 0),
            FormulaDependency.ForFormula(yFormula.Id, 0, _t1.Id, "R1", _a, null, null, 0),
        ];
        if (includePrev)
        {
            var prevFormula = _t1.Formulas.Single(f => f.ColumnDefId == _prev);
            dependencies.Add(FormulaDependency.ForFormula(prevFormula.Id, 3, _t1.Id, "R1", _a, null, -1, 0));
        }

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(dependencies);

        _rows.ResolveTableInstanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<long>(0);
                var seq = (int)(id / 100);
                var kind = id % 100;

                return new TableInstanceRef(
                    id, DocumentId, kind == 3 ? _t2.Id : _t1.Id, Version, Key(seq).Value,
                    kind == 2 ? CopyId : null);
            });

        // Оригінали першими, копія після — як віддає RowStore.
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var seq = call.ArgAt<PeriodKey>(1).Sequence;

                return (IReadOnlyList<TableInstanceRef>)
                [
                    new TableInstanceRef(Orig1(seq), DocumentId, _t1.Id, Version, Key(seq).Value),
                    new TableInstanceRef(Orig2(seq), DocumentId, _t2.Id, Version, Key(seq).Value),
                    new TableInstanceRef(Copy1(seq), DocumentId, _t1.Id, Version, Key(seq).Value, CopyId),
                ];
            });

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)
                call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                    id => id, id => (IReadOnlyDictionary<string, long>)new Dictionary<string, long> { ["R1"] = RowOf(id) }));

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var period = call.ArgAt<PeriodKey>(1);
                var seq = period.Sequence;
                var result = new Dictionary<long, IReadOnlyList<CellRecord>>();
                foreach (var id in call.ArgAt<IReadOnlyList<long>>(0))
                {
                    var kind = id % 100;
                    result[id] = kind switch
                    {
                        1 => [Cell(period, id, _t1.Id, _a, seq)],
                        2 => [Cell(period, id, _t1.Id, _a, seq * 10)],
                        _ => [],
                    };
                }

                return result;
            });
    }

    private static CellRecord Cell(PeriodKey period, long instance, int table, int column, decimal value)
        => new(new CellAddress(period, RowOf(instance), column), table, new CellValueData { ValueNumeric = value });
}
