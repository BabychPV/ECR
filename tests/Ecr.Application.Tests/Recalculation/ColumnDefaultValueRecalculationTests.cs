// tests/Ecr.Application.Tests/Recalculation/ColumnDefaultValueRecalculationTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ✎ C-5 (рішення PO 11.11, 02b §6.3): незаповнена комірка (рядок є, запису <c>doc.CellValue</c> немає) дає формулам
/// сервера <c>ColumnDef.DefaultValue</c> — той самий, що показує сітка. Явна порожнеча (<c>IsEmpty = 1</c>) — ні.
/// </summary>
/// <remarks>
/// Що було: <c>SliceEvaluationContext.Value</c> повертав <c>Null</c> для відсутньої комірки, тож сітка показувала
/// Factor = 1.5, а сервер рахував <c>10 * null</c>. Мутація: прибрати доповнення дефолтами в
/// <c>RecalculationService.LoadPeriodAsync</c> — червоніють «дефолт», «SUM» і «попередній період».
/// </remarks>
public sealed class ColumnDefaultValueRecalculationTests
{
    private const long DocumentId = 700;
    private const int Version = 1;
    private const int Year = 2026;
    private const long Row1 = 2001;
    private const long Row2 = 2002;

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    /// <summary>Комірки зрізу за періодом (номер місяця).</summary>
    private readonly Dictionary<int, List<CellRecord>> _slices = [];

    private TableDef _table = null!;
    private ColumnDef _qty = null!;
    private ColumnDef _factor = null!;
    private ColumnDef _out = null!;

    private static PeriodKey Key(int sequence) => PeriodKey.Create(Year, sequence);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Незаповнена_комірка_дає_формулі_DefaultValue_колонки()
    {
        Arrange("[Qty] * [Factor]", "1.5");
        Put(2, Row1, _qty, 10m);

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(15m, Single(Row1).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Дефолт_із_десятковою_комою_теж_читається()
    {
        Arrange("[Qty] * [Factor]", "1,5");
        Put(2, Row1, _qty, 10m);

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(15m, Single(Row1).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Явна_порожнеча_дефолту_не_отримує()
    {
        Arrange("[Qty] * [Factor]", "1.5");
        Put(2, Row1, _qty, 10m);
        _slices[2].Add(new(new CellAddress(Key(2), Row1, _factor.Id), _table.Id, new CellValueData { IsEmpty = true }));

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.DoesNotContain(Applied(), c => c.Address.TableRowId == Row1 && c.Value.ValueNumeric == 15m);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Збережене_значення_перекриває_дефолт()
    {
        Arrange("[Qty] * [Factor]", "1.5");
        Put(2, Row1, _qty, 10m);
        Put(2, Row1, _factor, 2m);

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(20m, Single(Row1).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task SUM_над_колонкою_рахує_дефолт_незаповнених()
    {
        Arrange("SUM([T].[R1:R2].[Factor])", "1.5");

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.All(Applied(), c => Assert.Equal(3m, c.Value.ValueNumeric));
        Assert.NotEmpty(Applied());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Попередній_період_дає_дефолт()
    {
        // У січні рядок є, комірки Factor немає → 1.5; лютий читає її через зсув.
        Arrange("[Qty] * [Period:-1].[Factor]", "1.5", crossPeriod: true);
        Put(2, Row1, _qty, 10m);

        await Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None);

        Assert.Equal(15m, Single(Row1).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Непридатний_дефолт_як_відсутній()
    {
        Arrange("[Qty] + 1", "abc");
        Put(2, Row1, _qty, 10m);

        var exception = await Record.ExceptionAsync(
            () => Service().RecalculateAllAsync(DocumentId, Key(2), CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(11m, Single(Row1).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "C-5")]
    public async Task Дефолт_Lookup_код_запису_стає_ідентифікатором()
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var table = builder.Table(builder.Sheet("S"), "T");
        var lookup = builder.Column(table, "Engine", CellDataType.Lookup);
        lookup.SetLookup(900);
        lookup.SetPresentation("NOTPASSEDOVERHAUL", null, null);
        var plain = builder.Column(table, "Qty");
        plain.SetPresentation("2.5", null, null);
        var computed = builder.Column(table, "Out", CellDataType.Formula);
        computed.SetPresentation("7", null, null);
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        var registries = Substitute.For<IRegistryStore>();
        registries.FindEntriesByCodesAsync(900, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)
            [
                RegistryEntry.ForSnapshot(
                    77, 900, EcrCode.Create("NOTPASSEDOVERHAUL"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "n" }), 1),
            ]);

        var set = await new ColumnDefaultValues(_metadata, registries).ForVersionAsync(Version, CancellationToken.None);

        var defaults = set.Of(table.Id).ToDictionary(d => d.ColumnDefId, d => d.Value);
        Assert.Equal(77L, defaults[lookup.Id].ValueRegistryEntryId);
        Assert.Equal(2.5m, defaults[plain.Id].ValueNumeric);
        Assert.False(defaults.ContainsKey(computed.Id));   // обчислювана колонка дефолту не має
        Assert.False(set.HasUnresolvedLookup);
    }

    private CellRecord Single(long rowId) => Assert.Single(Applied(), c => c.Address.TableRowId == rowId);

    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .SingleOrDefault(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));

        return call is null
            ? []
            : [.. ((IReadOnlyCollection<CellChangeSet>)call.GetArguments()[0]!).SelectMany(set => set.Upserts)];
    }

    private void Put(int sequence, long rowId, ColumnDef column, decimal value)
        => _slices[sequence].Add(
            new(new CellAddress(Key(sequence), rowId, column.Id), _table.Id, new CellValueData { ValueNumeric = value }));

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

    private void Arrange(string expression, string factorDefault, bool crossPeriod = false)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        _table = builder.Table(builder.Sheet("S"), "T");
        _qty = builder.Column(_table, "Qty");
        _factor = builder.Column(_table, "Factor");
        _factor.SetPresentation(factorDefault, null, null);
        _out = builder.Column(_table, "Out", CellDataType.Formula);
        builder.Row(_table, "R1", 1);
        builder.Row(_table, "R2", 2);
        var formula = builder.Formula(_table, expression, column: _out);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        _slices[1] = [];
        _slices[2] = [];

        _rows.ResolveTableInstanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => new TableInstanceRef(
                call.ArgAt<long>(0), DocumentId, _table.Id, Version, Key((int)(call.ArgAt<long>(0) - 1000)).Value));
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<PeriodKey>(1);
                return (IReadOnlyList<TableInstanceRef>)
                    [new TableInstanceRef(1000 + key.Sequence, DocumentId, _table.Id, Version, key.Value)];
            });
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)
                call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                    id => id,
                    id => (IReadOnlyDictionary<string, long>)new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        ["R1"] = Row1,
                        ["R2"] = Row2,
                    }));
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var period = call.ArgAt<PeriodKey>(1);
                var slice = _slices.TryGetValue(period.Sequence, out var found) ? found : [];
                return (IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>)
                    call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                        id => id, id => (IReadOnlyList<CellRecord>)[.. slice.Select(c => c with { Address = c.Address with { PeriodKey = period } })]);
            });

        // Граф: колонкові читання Qty і Factor (зсув — для міжперіодного посилання). Діапазон `[R1:R2]` — без
        // збереженого графа (чернетка): формула читає документ, діапазон розкривається за поточним порядком.
        IReadOnlyList<FormulaDependency> graph = expression.Contains("[R1:R2]", StringComparison.Ordinal)
            ? []
            :
            [
                FormulaDependency.ForFormula(formula.Id, 0, _table.Id, null, _qty.Id, null, 0, 0),
                FormulaDependency.ForFormula(
                    formula.Id, crossPeriod ? (byte)3 : (byte)0, _table.Id, null, _factor.Id, null,
                    crossPeriod ? (short)-1 : (short)0, 1),
            ];
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(graph);
    }
}
