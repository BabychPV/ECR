// tests/Ecr.Application.Tests/Recalculation/CrossPeriodPredicateTests.cs
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
/// ✎ C-11 (рішення PO 11.11, варіант «б»): <c>[Period:-n].[T].[WHERE …].[Col]</c> фільтрує рядки ЗСУНУТОГО періоду —
/// умова й значення беруться з тих самих рядків того періоду.
/// </summary>
/// <remarks>
/// Що було: рядки й умова — з ПОТОЧНОГО періоду, значення — з іншого періоду за ключем поточного рядка. Динамічні
/// рядки (у кожному періоді свої ключі) давали 0, фіксовані — умову з не того місяця. Мутація: у
/// <c>SliceEvaluationContext.EvaluatePredicate</c> повернути <c>_rowsByTable</c> для будь-якого зсуву — червоніють
/// обидва перші тести.
/// </remarks>
public sealed class CrossPeriodPredicateTests
{
    private const long DocumentId = 700;
    private const int Version = 1;
    private const int Year = 2026;

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    /// <summary>Рядки Main за номером періоду: ключ → (id, Kind, Qty).</summary>
    private readonly Dictionary<int, List<(string Key, long Id, string Kind, decimal Qty)>> _main = [];

    private TableDef _mainTable = null!;
    private TableDef _sumTable = null!;
    private ColumnDef _kind = null!;
    private ColumnDef _qty = null!;
    private ColumnDef _total = null!;

    private static PeriodKey Key(int sequence) => PeriodKey.Create(Year, sequence);

    private static long SumRow(int sequence) => 9000 + sequence;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-11")]
    public async Task Динамічні_рядки_попереднього_періоду_фільтруються_своєю_умовою()
    {
        Arrange("SUM([Period:-1].[Main].[WHERE [Kind] = 'A'].[Qty])", -1);
        _main[1] = [("j1", 101, "A", 3m), ("j2", 102, "A", 4m), ("j3", 103, "B", 10m)];
        _main[2] = [("g1", 201, "B", 100m), ("g2", 202, "A", 1000m)];

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(7m, Total());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-11")]
    public async Task Фіксовані_рядки_беруть_умову_з_того_самого_періоду()
    {
        Arrange("SUM([Period:-1].[Main].[WHERE [Kind] = 'A'].[Qty])", -1);
        _main[1] = [("R1", 101, "A", 3m), ("R2", 102, "B", 10m)];
        _main[2] = [("R1", 201, "B", 50m), ("R2", 202, "A", 60m)];

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(3m, Total());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-11")]
    public async Task Поточний_період_без_зсуву_як_і_раніше()
    {
        Arrange("SUM([Main].[WHERE [Kind] = 'A'].[Qty])", 0);
        _main[1] = [("j1", 101, "A", 3m)];
        _main[2] = [("g1", 201, "B", 100m), ("g2", 202, "A", 1000m)];

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(1000m, Total());
    }

    private decimal? Total()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
        var upserts = ((IReadOnlyCollection<CellChangeSet>)call.GetArguments()[0]!).SelectMany(set => set.Upserts);

        return Assert.Single(upserts, c => c.Address.ColumnDefId == _total.Id).Value.ValueNumeric;
    }

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 2, 1), new DateOnly(Year, 2, 28)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            Substitute.For<IRegistryStore>(), headers, Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(Year, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow, Substitute.For<ISheetEditGate>());
    }

    private long MainInstance(int sequence) => 1000 + sequence;

    private long SumInstance(int sequence) => 2000 + sequence;

    private void Arrange(string expression, int offset)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");
        _mainTable = builder.Table(sheet, "Main", TableRowMode.Dynamic);
        _kind = builder.Column(_mainTable, "Kind", CellDataType.String);
        _qty = builder.Column(_mainTable, "Qty");
        _sumTable = builder.Table(sheet, "Sum");
        _total = builder.Column(_sumTable, "Total", CellDataType.Formula);
        builder.Row(_sumTable, "S1", 1);
        var formula = builder.Formula(_sumTable, expression, column: _total);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        _rows.ResolveTableInstanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<long>(0);
                return id >= 2000
                    ? new TableInstanceRef(id, DocumentId, _sumTable.Id, Version, Key((int)(id - 2000)).Value)
                    : new TableInstanceRef(id, DocumentId, _mainTable.Id, Version, Key((int)(id - 1000)).Value);
            });
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<PeriodKey>(1);
                return (IReadOnlyList<TableInstanceRef>)
                [
                    new TableInstanceRef(MainInstance(key.Sequence), DocumentId, _mainTable.Id, Version, key.Value),
                    new TableInstanceRef(SumInstance(key.Sequence), DocumentId, _sumTable.Id, Version, key.Value),
                ];
            });
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var sequence = call.ArgAt<PeriodKey>(1).Sequence;
                return (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)
                    call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                        id => id,
                        id => (IReadOnlyDictionary<string, long>)(id == SumInstance(sequence)
                            ? new Dictionary<string, long>(StringComparer.Ordinal) { ["S1"] = SumRow(sequence) }
                            : (_main.TryGetValue(sequence, out var rows) ? rows : [])
                                .ToDictionary(r => r.Key, r => r.Id, StringComparer.Ordinal)));
            });
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var period = call.ArgAt<PeriodKey>(1);
                return (IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>)
                    call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                        id => id,
                        id => (IReadOnlyList<CellRecord>)(id == MainInstance(period.Sequence)
                            ? [.. (_main.TryGetValue(period.Sequence, out var rows) ? rows : []).SelectMany(r => new CellRecord[]
                            {
                                new(new CellAddress(period, r.Id, _kind.Id), _mainTable.Id, new CellValueData { ValueString = r.Kind }),
                                new(new CellAddress(period, r.Id, _qty.Id), _mainTable.Id, new CellValueData { ValueNumeric = r.Qty }),
                            })]
                            : []));
            });

        // Предикатне ребро: колонка значення + FilterJson, рядка немає (DependencyExtractor).
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>())
            .Returns(
            [
                FormulaDependency.ForFormula(
                    formula.Id, offset == 0 ? (byte)0 : (byte)3, _mainTable.Id, null, _qty.Id,
                    "{\"where\":\"[Kind] = 'A'\"}", (short)offset, 0),
            ]);
    }
}
