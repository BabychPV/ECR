using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ✎ D-575 (рішення людини 23.11, варіант B): формули НЕ ВІДКРИТОГО періоду (Scheduled активного проєкту) не
/// обчислюються — комірки лишаються як є, без помилки, доки місяць не відкриється. Перевіряється кожен вхід
/// <see cref="RecalculationService"/>, через який ідуть шляхи формул: правка (задача формул), повний прогін (рік, нічний
/// розклад, «Перерахувати»/F9, перерахунок після міграції — усі через <c>RecalculationJob</c>), читач іншого періоду
/// (каскад, ланцюжок періодів) і подання.
/// </summary>
/// <remarks>
/// Шаблон: <c>C = [A]/[B]</c>, <c>D = [C]*2 + [E]</c>; B = 0 — помилка #DIV/0 (як #N/A газу без даних HSE400, D-506).
/// ⛔ Мутація (локально, не комітити): прибрати гілку <c>evaluationState is { AwaitingOpen: true }</c> у
/// <c>RecalculationService.RunAsync</c> — тести «не відкрито» червоніють (помилка пишеться, D перераховується).
/// </remarks>
public sealed class AwaitingOpenPeriodRecalculationTests
{
    private const long TableInstance = 510;
    private const long DocumentId = 710;
    private const long RowId = 1101;
    private const string RowKeyValue = "R";
    private const int Version = 1;
    private static readonly PeriodKey Period = new(202612);
    private static readonly DateTime Since = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IFormulaErrorStore _errors = Substitute.For<IFormulaErrorStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();

    private int _b;
    private int _e;
    private int _c;
    private int _sheetId;
    private int _tableId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Правка_в_не_відкритому_періоді_формули_не_рахує_і_помилки_не_пише()
    {
        Arrange(PeriodState.Scheduled, awaitingOpen: true, b: 0m);

        var written = await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        AssertNotEvaluated(written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Повний_прогін_року_нічний_F9_міграція_не_відкритий_період_пропускає()
    {
        Arrange(PeriodState.Scheduled, awaitingOpen: true, b: 0m);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None, writeSubmitted: true);

        AssertNotEvaluated(written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Читач_іншого_періоду_каскад_ланцюжок_не_відкритий_період_пропускає()
    {
        Arrange(PeriodState.Scheduled, awaitingOpen: true, b: 0m);

        var written = await Service().RecalculateFromOtherPeriodsAsync(
            DocumentId, Period, [new CrossPeriodSource(11, _tableId, _b, OwnerInput: true)], CancellationToken.None);

        AssertNotEvaluated(written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Подання_не_відкритого_періоду_не_бачить_помилок_формул()
    {
        Arrange(PeriodState.Scheduled, awaitingOpen: true, b: 0m);

        var errors = await Service().RecalculateSheetForSubmitAsync(DocumentId, _sheetId, Period, CancellationToken.None);

        Assert.Empty(errors);
        AssertNotEvaluated(written: 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Застарілі_помилки_не_відкритого_періоду_знімаються_значення_лишаються()
    {
        Arrange(PeriodState.Scheduled, awaitingOpen: true, b: 0m, persisted: true);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
        var apply = Assert.Single(_errors.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync));
        Assert.Empty((IReadOnlyCollection<FormulaErrorRow>)apply.GetArguments()[0]!);
        var deleted = Assert.Single((IReadOnlyCollection<CellAddress>)apply.GetArguments()[1]!);
        Assert.Equal(new CellAddress(Period, RowId, _c), deleted);
        Assert.Empty(Events());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Відкритий_період_рахує_і_пише_помилку_як_доти()
    {
        // Контроль: той самий світ, період відкрито — D-506 діє (помилка C пишеться).
        Arrange(PeriodState.Open, awaitingOpen: false, b: 0m);

        await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        var apply = Assert.Single(_errors.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync));
        var upserts = (IReadOnlyCollection<FormulaErrorRow>)apply.GetArguments()[0]!;
        Assert.Contains(upserts, u => u.ColumnDefId == _c && u.ErrorCode == "#DIV/0");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-575")]
    public async Task Відкритий_період_без_помилки_пише_значення()
    {
        Arrange(PeriodState.Open, awaitingOpen: false, b: 2m);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        // C = 10/2 = 5 (як збережено), D = 5*2 + 3 = 13 (збережено 11) — один запис.
        Assert.Equal(1, written);
    }

    private void AssertNotEvaluated(int written)
    {
        Assert.Equal(0, written);
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ReadSlicesAsync));
        Assert.DoesNotContain(_errors.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync));
        Assert.Empty(Events());
    }

    private List<CellChangeRecord> Events()
        => [.. _audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteCellChangesAsync))
            .SelectMany(c => (IEnumerable<CellChangeRecord>)c.GetArguments()[0]!)];

    private DirtySet Dirty()
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Period, RowId, _b));
        dirty.Add(new CellAddress(Period, RowId, _e));
        return dirty;
    }

    private RecalculationService Service()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        _periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, _periods, _metadata, _versions, new RealFormulaEngine(), units,
            Substitute.For<IRegistryStore>(), headers, _audit,
            new TestClock(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc)),
            _uow, Substitute.For<ISheetEditGate>(),
            formulaErrors: _errors);
    }

    /// <summary>Таблиця A, B, E (входи), C = [A]/[B], D = [C]*2 + [E]; збережено C=5, D=11 (обчислені), E = 3.</summary>
    private void Arrange(PeriodState state, bool awaitingOpen, decimal b, bool persisted = false)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");
        var table = builder.Table(sheet, "T", TableRowMode.Fixed, TableLayoutKind.PerPeriodInstance);
        var a = builder.Column(table, "A");
        var bColumn = builder.Column(table, "B");
        var eColumn = builder.Column(table, "E");
        var c = builder.Column(table, "C", CellDataType.Formula);
        var d = builder.Column(table, "D", CellDataType.Formula);
        builder.Row(table, RowKeyValue, 1);
        var formulaC = builder.Formula(table, "[A] / [B]", FormulaScope.Column, c);
        formulaC.SetEvaluationOrder(0);
        var formulaD = builder.Formula(table, "[C] * 2 + [E]", FormulaScope.Column, d);
        formulaD.SetEvaluationOrder(1);
        var snapshot = builder.Build();

        (_tableId, _sheetId, _b, _e, _c) = (table.Id, sheet.Id, bColumn.Id, eColumn.Id, c.Id);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);
        var instance = new TableInstanceRef(TableInstance, DocumentId, table.Id, Version, Period.Value, HasFormulaErrors: persisted);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(instance);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([instance]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstance] = new Dictionary<string, long> { [RowKeyValue] = RowId },
            });

        IReadOnlyList<CellRecord> slice =
        [
            Cell(a.Id, 10m, calculated: false),
            Cell(bColumn.Id, b, calculated: false),
            Cell(eColumn.Id, 3m, calculated: false),
            Cell(c.Id, 5m, calculated: true),
            Cell(d.Id, 11m, calculated: true),
        ];
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [TableInstance] = slice });

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(
        [
            FormulaDependency.ForFormula(formulaC.Id, 0, table.Id, RowKeyValue, a.Id, null, null, 0),
            FormulaDependency.ForFormula(formulaC.Id, 0, table.Id, RowKeyValue, bColumn.Id, null, null, 1),
            FormulaDependency.ForFormula(formulaD.Id, 0, table.Id, RowKeyValue, c.Id, null, null, 2),
            FormulaDependency.ForFormula(formulaD.Id, 0, table.Id, RowKeyValue, eColumn.Id, null, null, 3),
        ]);

        _errors.ReadInstancesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(persisted
                ? new List<FormulaErrorRow>
                {
                    new(DocumentId, Period.Value, TableInstance, table.Id, null, RowId, RowKeyValue, c.Id, formulaC.Id,
                        FormulaError.ErrorKind, "#N/A", null, null, null, null, null, Since),
                }
                : []);

        _periods.FindEvaluationStateAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodEvaluationState(state, awaitingOpen));
        _periods.FindPeriodStateAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>()).Returns(state);

        CellRecord Cell(int column, decimal value, bool calculated)
            => new(new CellAddress(Period, RowId, column), table.Id,
                new CellValueData { ValueNumeric = value, IsCalculated = calculated });
    }
}
