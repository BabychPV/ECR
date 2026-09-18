// tests/Ecr.Application.Tests/Recalculation/RecalculationReadScopeTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// Що саме перерахунок ЧИТАЄ — <c>CAL-02</c> директиви №14 частина 3.
/// </summary>
/// <remarks>
/// ⛔ <c>LoadValuesAsync</c> читав усі екземпляри таблиць документа, усі
/// рядки, усі комірки — і робив це ДВІЧІ, бо попередній період вантажився
/// завжди, щойно <c>PeriodKey.Sequence &gt; 1</c>. У шаблоні на 91 таблицю
/// правка однієї комірки коштувала читання всього документа за два періоди на
/// КОЖНЕ автозбереження, хоча формула, яку перераховують, читає дві таблиці й
/// один період.
///
/// ⚠ Тут навмисно ДЕСЯТЬ таблиць, як у доказі директиви: на двох «дві замість
/// десяти» і «дві замість двох» не відрізняються, а твердження саме про те, що
/// обсяг читання не залежить від розміру документа.
/// </remarks>
public sealed class RecalculationReadScopeTests
{
    private const int TableCount = 10;
    private const long DocumentId = 700;
    private const int Version = 1;

    /// <summary>Ідентифікатор формули — сталий, щоб залежності складалися незалежно від знімка.</summary>
    private const int FormulaId = 555;

    /// <summary>Лютий: попередній період ІСНУЄ — інакше твердження про нього порожнє.</summary>
    private static readonly PeriodKey Period = new(202602);

    private static readonly PeriodKey Previous = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    private readonly List<TableDef> _tables = [];
    private readonly List<int> _inputColumns = [];

    /// <summary>Переліки екземплярів, з якими служба ходила по комірки, у порядку викликів.</summary>
    private readonly List<IReadOnlyList<long>> _sliceReads = [];

    private int _outColumnId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-02")]
    public async Task Читаються_лише_екземпляри_таблиць_із_замикання_залежностей()
    {
        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ директиви. Документ із десяти таблиць, формула
        // в T1 читає лише T1 (свою) і T2 — отже пакетний запит комірок мусить
        // нести РІВНО два екземпляри.
        Arrange("[C1] + [T2].[R1].[C1]", crossPeriod: false);

        var written = await Service().RecalculateAsync(Instance(0), Dirty(), CancellationToken.None);

        Assert.Equal(1, written);

        var ids = Assert.Single(_sliceReads);
        Assert.Equal(new List<long> { Instance(0), Instance(1) }, ids.Order().ToList());

        // 1 (T1) + 2 (T2): число доводить, що прочитане саме те, а не «щось».
        Assert.Equal(3m, Assert.Single(Applied()).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-02")]
    public async Task Попередній_період_не_читається_коли_жодна_ціль_на_нього_не_посилається()
    {
        // ⛔ Друга половина того самого доказу: «рівно з ОДНИМ періодом».
        // Період — лютий, тобто попередній існує, і до `CAL-02` він читався
        // безумовно. Формула на нього не посилається, отже другий прохід
        // читання — чиста витрата.
        Arrange("[C1] + [T2].[R1].[C1]", crossPeriod: false);

        await Service().RecalculateAsync(Instance(0), Dirty(), CancellationToken.None);

        Assert.Single(_sliceReads);

        // ⚠ Заразом і рядки: другий прохід коштував не лише комірок. Два
        // виклики — це побудова плану в `RunAsync` і читання поточного періоду.
        await _rows.Received(2).GetRowIdsBatchAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        await _rows.DidNotReceive().GetTableInstancesAsync(
            DocumentId, Previous, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-02")]
    public async Task Крос_періодна_формула_таки_читає_попередній_період()
    {
        // ⛔ Контрольний тест межі, без якого попередній був би доказом
        // «ми перестали читати минулий місяць» — тобто доказом дефекту.
        // `[Period:-1]` у формулі означає реальні числа минулого періоду, і
        // прочитати їх як порожнечу означало б тихо занизити результат.
        Arrange("[C1] + [Period:-1].[C1]", crossPeriod: true);

        var written = await Service().RecalculateAsync(Instance(0), Dirty(), CancellationToken.None);

        Assert.Equal(1, written);

        // Два проходи — і обидва звужені до замикання (сама T1), а не до
        // всього документа.
        Assert.Equal(2, _sliceReads.Count);
        Assert.All(_sliceReads, ids => Assert.Equal(new List<long> { Instance(0) }, ids.ToList()));

        // 1 (лютий) + 40 (січень): число доводить, що прочитано саме
        // попередній період, а не другий раз поточний.
        Assert.Equal(41m, Assert.Single(Applied()).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-02")]
    public void Замикання_рахується_з_цілей_а_не_з_усіх_формул_версії()
    {
        // ⚠ Одиничне твердження про сам калькулятор скоупу: залежності ЧУЖОЇ
        // формули (яка в цей прогін не потрапила) не розширюють читання.
        // Без цього «замикання» тихо виродилося б у «всі таблиці версії».
        IReadOnlyList<FormulaDependency> dependencies =
        [
            FormulaDependency.ForFormula(10, 0, 100, "R1", 1, null, null, 0),
            FormulaDependency.ForFormula(10, 0, 200, "R1", 2, null, null, 1),
            FormulaDependency.ForFormula(20, 0, 900, "R1", 3, null, null, 0),
            FormulaDependency.ForFormula(20, 3, 901, "R1", 4, null, -1, 1),
        ];

        var scope = RecalculationReadScope.Compute(
            dependencies, [10], new Dictionary<int, int> { [10] = 100, [20] = 900 }, []);

        Assert.Equal(new List<int> { 100, 200 }, scope.TableDefIds!.Order().ToList());

        // ⛔ Крос-періодність ЧУЖОЇ формули теж не протікає: інакше другий
        // прохід читання повертався б через сусідню формулу тієї самої версії.
        Assert.False(scope.ReadsOtherPeriod);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-02")]
    public async Task Формула_без_збережених_залежностей_читає_ВЕСЬ_документ()
    {
        // ⛔ Названий випадок, а не перестраховка: формула, додана в шаблон
        // ПІСЛЯ введення даних, ще не має жодного ребра в графі
        // (`CascadeRecalculationTests.Формула_додана_після_введення_даних…`).
        // Граф на питання «що вона читає» не відповідає — і звузити читання за
        // його мовчанням означало б порахувати її з порожнечі: тихо
        // неправильне число замість помилки.
        //
        // ⚠ Прогін тут ПОВНИЙ: інкрементний до такої формули не дотягується
        // жодним насінням саме тому, що ребер немає.
        Arrange("[C1] + [T2].[R1].[C1]", crossPeriod: false, withDependencies: false);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(3m, Assert.Single(Applied()).Value.ValueNumeric);

        // Читання — по всіх десяти таблицях, як і до `CAL-02`.
        var ids = _sliceReads[0];
        Assert.Equal(TableCount, ids.Count);
    }

    private static long Instance(int index) => 1000 + index;

    private static long RowId(int index) => 2000 + index;

    /// <summary>Змінена комірка входу в першій таблиці.</summary>
    private DirtySet Dirty()
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Period, RowId(0), _inputColumns[0]));

        return dirty;
    }

    /// <summary>Комірки, які служба віддала на запис.</summary>
    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));

        return ((CellChangeSet)call.GetArguments()[0]!).Upserts;
    }

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            _audit,
            new TestClock(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow);
    }

    /// <summary>
    /// Документ із <see cref="TableCount"/> таблиць на одному аркуші; формула
    /// живе в першій і пише в її колонку <c>Out</c>.
    /// </summary>
    /// <param name="expression">Вираз формули.</param>
    /// <param name="crossPeriod">
    /// Чи несе граф залежностей крос-періодне ребро — саме воно й вирішує, чи
    /// вантажити попередній період.
    /// </param>
    /// <param name="withDependencies">
    /// <c>false</c> — граф порожній: саме так виглядає формула, додана в
    /// шаблон після того, як дані вже введені.
    /// </param>
    private void Arrange(string expression, bool crossPeriod, bool withDependencies = true)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");

        for (var i = 0; i < TableCount; i++)
        {
            var table = builder.Table(sheet, $"T{i + 1}");
            _inputColumns.Add(builder.Column(table, "C1", isMonthColumn: true).Id);
            builder.Row(table, "R1", 1);
            _tables.Add(table);
        }

        _outColumnId = builder.Column(_tables[0], "Out", CellDataType.Formula).Id;

        var formula = new FormulaDef(
            _tables[0].Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(_outColumnId);
        _tables[0].AddFormula(formula);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        var instances = new List<TableInstanceRef>();
        for (var i = 0; i < TableCount; i++)
        {
            instances.Add(new TableInstanceRef(Instance(i), DocumentId, _tables[i].Id, Version, Period.Value));
        }

        _rows.ResolveTableInstanceAsync(Instance(0), Arg.Any<CancellationToken>()).Returns(instances[0]);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = (PeriodKey)callInfo[1]!;

                return (IReadOnlyList<TableInstanceRef>)
                    [.. instances.Select(t => t with { PeriodKey = key.Value })];
            });

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var asked = (IReadOnlyList<long>)callInfo[0]!;

                return (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)asked.ToDictionary(
                    id => id,
                    id => (IReadOnlyDictionary<string, long>)new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        ["R1"] = RowId((int)(id - 1000)),
                    });
            });

        // ⚠ Зріз віддається рівно на ЗАПИТАНІ екземпляри, а не на всі: інакше
        // тест мовчки отримував би значення таблиць, яких не просив, і
        // твердження «прочитано рівно два» не мало б наслідків для числа.
        //
        // ⚠ Період розрізняється за ПОРЯДКОМ виклику: `ReadSlicesAsync` періоду
        // не приймає (екземпляр таблиці належить одному періоду за побудовою),
        // а `LoadValuesAsync` іде спершу в поточний, потім у попередній.
        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var asked = (IReadOnlyList<long>)callInfo[0]!;
                var period = _sliceReads.Count == 0 ? Period : Previous;
                _sliceReads.Add([.. asked]);

                return (IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>)asked.ToDictionary(
                    id => id,
                    id => (IReadOnlyList<CellRecord>)Slice(id, period));
            });

        List<FormulaDependency> dependencies =
        [
            FormulaDependency.ForFormula(FormulaId, 0, _tables[0].Id, "R1", _inputColumns[0], null, null, 0),
        ];

        dependencies.Add(
            crossPeriod

                // ⚠ Саме так це кодує публікація: `DependsOnKind = 3`
                // (CrossPeriod) і ненульовий `PeriodOffset`
                // (`DependencyExtractor.cs:124-125`).
                ? FormulaDependency.ForFormula(
                    FormulaId, 3, _tables[0].Id, "R1", _inputColumns[0], null, -1, 1)
                : FormulaDependency.ForFormula(
                    FormulaId, 0, _tables[1].Id, "R1", _inputColumns[1], null, null, 1));

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>())
            .Returns(withDependencies ? dependencies : []);
    }

    /// <summary>Комірки одного екземпляра: вхід і (для T1) застарілий підсумок.</summary>
    private List<CellRecord> Slice(long instanceId, PeriodKey period)
    {
        var index = (int)(instanceId - 1000);
        var rowId = RowId(index);

        // Поточний період: 1, 2, 3…; попередній: 40, 41, 42… — числа навмисно
        // різні, щоб «прочитали не той період» було видно як інше значення.
        var input = period == Period ? index + 1 : 40 + index;

        List<CellRecord> cells =
        [
            new(new CellAddress(period, rowId, _inputColumns[index]), _tables[index].Id,
                new CellValueData { ValueNumeric = input }),
        ];

        if (index == 0)
        {
            // Застарілий підсумок: `DAT-02` не пише незміненого, тож без
            // розбіжності не було б ані запису, ані доказу.
            cells.Add(new CellRecord(
                new CellAddress(period, rowId, _outColumnId), _tables[0].Id,
                new CellValueData { ValueNumeric = -1m, IsCalculated = true }));
        }

        return cells;
    }
}
