using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// X6-02: багатоаркушевий прогін формул чекає блокування лише ПЕРШОГО аркуша;
/// наступні бере без черги (<see cref="ISheetEditGate.EnterEditNoWaitAsync"/>).
/// </summary>
/// <remarks>
/// ⛔ Доти кожен аркуш брався <see cref="ISheetEditGate.EnterEditAsync"/> (до 30 с), уже
/// тримаючи спільні блокування попередніх. Черга <c>sp_getapplock</c> — FIFO: прогін, що
/// чекав аркуш, який подавався, ставив за собою подання вже взятих аркушів, а за ними —
/// автозбереження їхніх редакторів (аж до <c>409 sheetBeingSubmitted</c>).
/// Справжню чергу SQL доводить <c>SheetEditGateNoWaitTests</c>; тут — що прогін її
/// не займає.
/// </remarks>
public sealed class RecalculationSheetLockConvoyTests
{
    private const long DocumentId = 700;
    private const int Version = 1;
    private const long FirstInstance = 501;
    private const long SecondInstance = 502;
    private const long FirstRow = 1001;
    private const long SecondRow = 2001;

    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();

    private int _firstSheet;
    private int _secondSheet;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Другий_аркуш_береться_без_черги_перший_з_очікуванням()
    {
        Arrange();

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.True(written > 0);
        await _gate.Received(1).EnterEditAsync(DocumentId, _firstSheet, Period, Arg.Any<CancellationToken>());
        await _gate.Received(1).EnterEditNoWaitAsync(DocumentId, _secondSheet, Period, Arg.Any<CancellationToken>());
        await _gate.DidNotReceive().EnterEditAsync(DocumentId, _secondSheet, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Зайнятий_другий_аркуш_відмовляє_нічого_не_записавши()
    {
        Arrange();
        _gate.EnterEditNoWaitAsync(DocumentId, _secondSheet, Period, Arg.Any<CancellationToken>())
             .ThrowsAsync(new ConcurrencyConflictException(
                 "ECR-DOC-4091",
                 "Аркуш зайнятий.",
                 new Dictionary<string, object?> { ["messageKey"] = "err.ECR-DOC-4091.sheetBeingSubmitted" }));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None));

        Assert.Equal("ECR-DOC-4091", error.ErrorCode);
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
    }

    private RecalculationService Service()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        // ⛔ Замикання транзакції викликається НАСПРАВДІ — інакше блокувань не брав би ніхто.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), units,
            Substitute.For<IRegistryStore>(),
            headers,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow, _gate);
    }

    /// <summary>
    /// Два аркуші, по таблиці на кожному з формулою <c>C3 = C1 + C2</c>; збережені
    /// підсумки застарілі в обох — повний прогін пише в обидва аркуші.
    /// </summary>
    private void Arrange()
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var first = Table(builder, "S1", FirstRow, out var firstTable);
        var second = Table(builder, "S2", SecondRow, out var secondTable);
        _firstSheet = firstTable.SheetDefId;
        _secondSheet = secondTable.SheetDefId;
        Assert.True(_firstSheet < _secondSheet);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        var instances = new List<TableInstanceRef>
        {
            new(FirstInstance, DocumentId, firstTable.Id, Version, Period.Value),
            new(SecondInstance, DocumentId, secondTable.Id, Version, Period.Value),
        };
        _rows.ResolveTableInstanceAsync(FirstInstance, Arg.Any<CancellationToken>()).Returns(instances[0]);
        _rows.ResolveTableInstanceAsync(SecondInstance, Arg.Any<CancellationToken>()).Returns(instances[1]);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(instances);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [FirstInstance] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = FirstRow },
                [SecondInstance] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = SecondRow },
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [FirstInstance] = first.Slice,
                [SecondInstance] = second.Slice,
            });
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<CellAddress, CellValueData>());

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>())
            .Returns([.. first.Dependencies, .. second.Dependencies]);
    }

    /// <summary>Аркуш із таблицею на один рядок; підсумок у зрізі — застарілий.</summary>
    private static (List<CellRecord> Slice, List<FormulaDependency> Dependencies) Table(
        TemplateBuilder builder, string sheetCode, long rowId, out TableDef table)
    {
        var sheet = builder.Sheet(sheetCode);
        table = builder.Table(sheet, $"T{sheetCode}");
        var c1 = builder.Column(table, $"C1{sheetCode}", isMonthColumn: true);
        var c2 = builder.Column(table, $"C2{sheetCode}", isMonthColumn: true);
        var c3 = builder.Column(table, $"C3{sheetCode}", CellDataType.Formula);
        builder.Row(table, "R1", 1);
        var formula = builder.Formula(table, $"[C1{sheetCode}] + [C2{sheetCode}]", column: c3);

        var slice = new List<CellRecord>
        {
            new(new CellAddress(Period, rowId, c1.Id), table.Id, new CellValueData { ValueNumeric = 100m }),
            new(new CellAddress(Period, rowId, c2.Id), table.Id, new CellValueData { ValueNumeric = 1m }),
            new(new CellAddress(Period, rowId, c3.Id), table.Id, new CellValueData { ValueNumeric = 11m, IsCalculated = true }),
        };

        return (slice,
        [
            FormulaDependency.ForFormula(formula.Id, 0, table.Id, "R1", c1.Id, null, null, 0),
            FormulaDependency.ForFormula(formula.Id, 0, table.Id, "R1", c2.Id, null, null, 1),
        ]);
    }
}
