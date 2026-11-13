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
/// ✎ C-1 (аудит 2026-11-10, рішення PO 11.11, D-475): формула, що дає помилку, НЕ перезаписує останнє правильне
/// значення, залежні від неї формули не перераховуються, стан помилки пишеться в <c>calc.FormulaError</c>, а журнал
/// отримує одну подію при появі й одну при усуненні.
/// </summary>
/// <remarks>
/// Шаблон: <c>C = [A]/[B]</c>, <c>D = [C]*2 + [E]</c>. Було A=10, B=2, E=1 → C=5, D=11; правка B=0, E=2.
/// До C-1: C пропускалась, а D рахувалась від старого C=5 → 12 (тихо хибне число).
/// </remarks>
public sealed class FormulaErrorRecalculationTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const long RowId = 1001;
    private const string RowKeyValue = "R";
    private const int Version = 1;
    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IFormulaErrorStore _errors = Substitute.For<IFormulaErrorStore>();

    private int _b;
    private int _e;
    private int _c;
    private int _d;
    private int _formulaC;
    private int _formulaD;
    private int _tableId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Помилка_формули_лишає_значення_і_не_перераховує_залежні()
    {
        Arrange(b: 0m, e: 2m, hasErrors: false);

        var written = await Service(withStore: false).RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        // ⛔ Без C-1: D = 5*2 + 2 = 12 записано від старого C (помилка C не потрапляла в стан прогону).
        Assert.Equal(0, written);
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Помилка_формули_пише_стан_і_одну_подію_журналу()
    {
        Arrange(b: 0m, e: 2m, hasErrors: false);

        await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        var apply = Assert.Single(_errors.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync));
        var upserts = ((IReadOnlyCollection<FormulaErrorRow>)apply.GetArguments()[0]!).ToList();
        var c = Assert.Single(upserts, u => u.ColumnDefId == _c);
        Assert.Equal(FormulaError.ErrorKind, c.Kind);
        Assert.Equal("#DIV/0", c.ErrorCode);
        Assert.Contains("\"value\":\"0\"", c.InputsJson, StringComparison.Ordinal);
        var d = Assert.Single(upserts, u => u.ColumnDefId == _d);
        Assert.Equal(FormulaError.BlockedKind, d.Kind);
        Assert.Equal(_c, d.SourceColumnDefId);
        Assert.Equal(RowKeyValue, d.SourceRowKey);

        var events = Events();
        var ev = Assert.Single(events);
        Assert.Equal(CellChangeOrigins.CalcError, ev.Origin);
        Assert.Equal(_c, ev.Address.ColumnDefId);
        Assert.NotNull(ev.OldValue);
        Assert.Equal(ev.OldValue, ev.NewValue);

        // Другий прогін на тих самих даних: стан уже збережений — ні запису, ні події (рішення PO п. 4).
        _errors.ClearReceivedCalls();
        _audit.ClearReceivedCalls();
        Arrange(b: 0m, e: 2m, hasErrors: true, persisted: upserts);

        await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        Assert.DoesNotContain(_errors.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync));
        Assert.Empty(Events());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Успішний_перерахунок_знімає_помилку_і_пише_подію_усунення()
    {
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Arrange(b: 0m, e: 2m, hasErrors: false);
        var persisted = new List<FormulaErrorRow>
        {
            new(DocumentId, Period.Value, TableInstance, _tableId, null, RowId, RowKeyValue, _c, _formulaC,
                FormulaError.ErrorKind, "#DIV/0", null, null, null, null, null, since),
            new(DocumentId, Period.Value, TableInstance, _tableId, null, RowId, RowKeyValue, _d, _formulaD,
                FormulaError.BlockedKind, "#DIV/0", null, Period.Value, _tableId, RowKeyValue, _c, since),
        };
        Arrange(b: 2m, e: 2m, hasErrors: true, persisted: persisted);

        var written = await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        // C = 10/2 = 5 (як збережено — не пишеться), D = 5*2 + 2 = 12 (нове число).
        Assert.Equal(1, written);
        var apply = Assert.Single(_errors.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync));
        Assert.Empty((IReadOnlyCollection<FormulaErrorRow>)apply.GetArguments()[0]!);
        var deletes = ((IReadOnlyCollection<CellAddress>)apply.GetArguments()[1]!).Select(a => a.ColumnDefId).Order().ToList();
        Assert.Equal(new[] { _c, _d }.Order().ToList(), deletes);

        var ev = Assert.Single(Events());
        Assert.Equal(CellChangeOrigins.CalcErrorResolved, ev.Origin);
        Assert.Equal(_c, ev.Address.ColumnDefId);
        Assert.Equal("#DIV/0", ev.Reason);
    }

    private List<CellChangeRecord> Events()
        => [.. _audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteCellChangesAsync))
            .SelectMany(c => (IEnumerable<CellChangeRecord>)c.GetArguments()[0]!)
            .Where(r => r.Origin is CellChangeOrigins.CalcError or CellChangeOrigins.CalcErrorResolved)];

    /// <summary>Правка B і E (насіння каскаду).</summary>
    private DirtySet Dirty()
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Period, RowId, _b));
        dirty.Add(new CellAddress(Period, RowId, _e));
        return dirty;
    }

    private RecalculationService Service(bool withStore = true, IMonthColumnRouter? router = null)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), units,
            Substitute.For<IRegistryStore>(), headers, _audit,
            new TestClock(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            _uow, Substitute.For<ISheetEditGate>(),
            formulaErrors: withStore ? _errors : null,
            monthRouter: router);
    }

    /// <summary>
    /// ✎ LAND-YEAR B24 × C-1b(2): прогін ЗАКРИТОГО якоря річної розкладки (<c>annualPeriods</c>) пише лише не-місячні
    /// колонки маршрутизованої таблиці. Рядкова формула (цілі — усі колонки рядка) лишається ціллю, але місячну колонку
    /// не відвідує — збережена помилка формули на ній належить закритому місяцю і НЕ знімається як «застаріла»
    /// (ні рядок <c>calc.FormulaError</c>, ні подія «усунено» в журналі).
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація (локально, не комітити): прибрати фільтр Deletes/Events за <c>ownedElsewhere</c> у
    /// <c>RecalculationService.RunAsync</c> (блок «LAND-YEAR B24 × C-1b(2)») — помилка M знімається, тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-474")]
    public async Task B24_закритий_якір_не_знімає_збережену_помилку_на_місячній_колонці()
    {
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");
        var table = builder.Table(sheet, "T", TableRowMode.Fixed, TableLayoutKind.MonthsInColumns);
        var total = builder.Column(table, "TOT", CellDataType.Formula);
        var month = builder.Column(table, "M", CellDataType.Formula, isMonthColumn: true);
        var row = builder.Row(table, RowKeyValue, 1);
        var formula = builder.Formula(table, "1 + 1", FormulaScope.Row, row: row);
        formula.SetEvaluationOrder(0);
        var snapshot = builder.Build();
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);

        var instance = new TableInstanceRef(TableInstance, DocumentId, table.Id, Version, Period.Value, HasFormulaErrors: true);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(instance);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([instance]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstance] = new Dictionary<string, long> { [RowKeyValue] = RowId },
            });
        IReadOnlyList<CellRecord> slice =
        [
            new(new CellAddress(Period, RowId, total.Id), table.Id, new CellValueData { ValueNumeric = 0m, IsCalculated = true }),
            new(new CellAddress(Period, RowId, month.Id), table.Id, new CellValueData { ValueNumeric = 5m, IsCalculated = true }),
        ];
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [TableInstance] = slice });
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns([]);

        // Збережена помилка рядкової формули на МІСЯЧНІЙ колонці закритого січня.
        _errors.ReadInstancesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new List<FormulaErrorRow>
            {
                new(DocumentId, Period.Value, TableInstance, table.Id, null, RowId, RowKeyValue, month.Id, formula.Id,
                    FormulaError.ErrorKind, "#DIV/0", null, null, null, null, null, since),
            });

        // Маршрутизована таблиця річної розкладки; у якоря чужих колонок немає (усе місячне тут — січень).
        var router = Substitute.For<IMonthColumnRouter>();
        router.Enabled.Returns(true);
        router.GetDocumentAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(new MonthRoutingDocument(DocumentId, 1, Version));
        router.AppliesToTable(Arg.Any<TableDef>()).Returns(true);
        router.ForeignColumnsAsync(
                Arg.Any<MonthRoutingDocument>(), Arg.Any<TableDef>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<ForeignColumn>)[]);

        var annualPeriods = Enumerable.Range(1, 12).Select(m => (Period.Year * 100) + m).ToList();
        var written = await Service(router: router)
            .RecalculateAllAsync(DocumentId, Period, CancellationToken.None, annualPeriods: annualPeriods);

        // Прогін відбувся: не-місячна TOT = 2 записана, місячна M — ні.
        Assert.Equal(1, written);
        await _cells.Received(1).ApplyBatchAsync(
            Arg.Is<IReadOnlyCollection<CellChangeSet>>(sets => sets.Any(s =>
                s.Upserts.Any(u => u.Address.ColumnDefId == total.Id && u.Value.ValueNumeric == 2m)
                && s.Upserts.All(u => u.Address.ColumnDefId != month.Id))),
            Arg.Any<CancellationToken>());

        // Помилка на M не знята: жодного видалення її адреси і жодної події «усунено».
        foreach (var apply in _errors.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IFormulaErrorStore.ApplyAsync)))
        {
            Assert.DoesNotContain((IReadOnlyCollection<CellAddress>)apply.GetArguments()[1]!, a => a.ColumnDefId == month.Id);
        }

        Assert.DoesNotContain(Events(), e => e.Address.ColumnDefId == month.Id);
    }

    /// <summary>Таблиця A, B, E (входи), C = [A]/[B], D = [C]*2 + [E]; збережено C=5, D=11 (обчислені).</summary>
    private void Arrange(decimal b, decimal e, bool hasErrors, IReadOnlyList<FormulaErrorRow>? persisted = null)
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

        (_tableId, _b, _e, _c, _d, _formulaC, _formulaD) =
            (table.Id, bColumn.Id, eColumn.Id, c.Id, d.Id, formulaC.Id, formulaD.Id);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);
        var instance = new TableInstanceRef(TableInstance, DocumentId, table.Id, Version, Period.Value, HasFormulaErrors: hasErrors);
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
            Cell(eColumn.Id, e, calculated: false),
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
            .Returns(persisted ?? []);

        CellRecord Cell(int column, decimal value, bool calculated)
            => new(new CellAddress(Period, RowId, column), table.Id,
                new CellValueData { ValueNumeric = value, IsCalculated = calculated });
    }
}
