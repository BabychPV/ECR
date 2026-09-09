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
/// Каскадний перерахунок формул шаблону — від правки комірки до нового числа.
/// </summary>
/// <remarks>
/// ⛔ `A7-63` у своїй найтихішій формі: <c>RecalculateAsync</c> був
/// заглушкою (<c>return Task.CompletedTask</c>), граф залежностей не
/// зберігався, а перекласти його на план перерахунку не було чим. Правка
/// комірки не міняла жодного похідного числа, і жодна помилка про це не
/// повідомляла — саме так виглядає неправильна звітність, яку помічають через
/// місяць на звірці.
/// </remarks>
public sealed class CascadeRecalculationTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Version = 1;
    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    // ⚠ Порожній довідник за замовчуванням: тести цього класу — про КАСКАД
    // (яка формула перерахувалась чому), а не про конверсію одиниць.
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    private TableDef _table = null!;
    private int _janId;
    private int _febId;
    private int _totalId;
    private int _formulaId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правка_комірки_перераховує_похідну()
    {
        // ⛔ Головне твердження: до виправлення тут не записувалося НІЧОГО.
        Arrange(jan: 10m, feb: 5m);

        var written = await Service().RecalculateAsync(TableInstance, Dirty(_janId), CancellationToken.None);

        Assert.Equal(1, written);

        var upsert = Assert.Single(Applied());
        Assert.Equal(_totalId, upsert.Address.ColumnDefId);
        Assert.Equal(15m, upsert.Value.ValueNumeric);

        // ⚠ Значення позначене обчисленим: інакше воно виглядало б як
        // введене людиною, і наступна правка «поверх» не мала б жодної
        // ознаки конфлікту (`R-A2`).
        Assert.True(upsert.Value.IsCalculated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правка_комірки_поза_графом_не_перераховує_нічого()
    {
        // ⚠ Інкрементність — це не оптимізація, а вимога: повний перерахунок
        // на кожну правку не вкладається в бюджет (`ФВ-9.4`). Тому правка
        // колонки, від якої не залежить жодна формула, має коштувати нуль.
        Arrange(jan: 10m, feb: 5m);

        var written = await Service()
            .RecalculateAsync(TableInstance, Dirty(_totalId), CancellationToken.None);

        Assert.Equal(0, written);
        await _cells.DidNotReceiveWithAnyArgs().ApplyAsync(null!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Порожній_набір_змін_не_читає_навіть_структури()
    {
        // ⛔ Порожнє насіння — це «нема чого рахувати», а не «перерахувати
        // все». Зворотне прочитання перетворило б кожен виклик на прогін по
        // всьому документу.
        Arrange(jan: 10m, feb: 5m);

        var written = await Service()
            .RecalculateAsync(TableInstance, new DirtySet(), CancellationToken.None);

        Assert.Equal(0, written);
        await _metadata.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Формула_додана_після_введення_даних_не_потрапляє_в_жоден_dirty_set()
    {
        // ⛔ Найтихіший випадок з усіх: формулу додали в шаблон ПІСЛЯ того, як
        // дані вже введені. Її залежностей у графі ще немає, і жодна правка
        // комірки її не зачепить — тобто інкрементний шлях не перерахує її
        // НІКОЛИ, хоч би скільки разів правили дані.
        Arrange(jan: 10m, feb: 5m, withDependencies: false);

        var written = await Service()
            .RecalculateAsync(TableInstance, Dirty(_janId), CancellationToken.None);

        Assert.Equal(0, written);
        await _cells.DidNotReceiveWithAnyArgs().ApplyAsync(null!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Повний_перерахунок_рахує_формулу_якої_немає_в_жодному_dirty_set()
    {
        // ⛔ Той самий шаблон, що й у тесті вище: формула, до якої
        // інкрементний шлях не дотягується жодним насінням. Повний
        // перерахунок мусить порахувати її саме тому, що бере формули з
        // ПЛАНУ, а не з насіння.
        Arrange(jan: 10m, feb: 5m, withDependencies: false);

        var written = await Service()
            .RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(1, written);

        var upsert = Assert.Single(Applied());
        Assert.Equal(_totalId, upsert.Address.ColumnDefId);
        Assert.Equal(15m, upsert.Value.ValueNumeric);
        Assert.True(upsert.Value.IsCalculated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Повний_перерахунок_документа_без_екземплярів_нічого_не_читає()
    {
        // ⚠ Документа за цей період немає — рахувати нема де. Це не помилка:
        // прогін запускають і на документ, у якому за період ще нічого не
        // заведено.
        Arrange(jan: 10m, feb: 5m);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(0, written);
        await _metadata.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Кілька_таблиць_документа_читаються_одним_пакетним_запитом()
    {
        // ⛔ Q-166 (аудит фази 2, продуктивність): цей прогін виконується на
        // КОЖНЕ редагування комірки (через `FormulaRecalculationJob`), а не
        // лише на повний перерахунок — запит на кожну таблицю окремо тут
        // коштує найдорожче серед усіх знахідок цього виміру. Друга таблиця
        // навмисно НЕ з `_table.Id`: інакше вона перезаписала б перший запис
        // у `rowIdsByTable` (індекс за `TableDefId`, не за екземпляром) і
        // зробила б це тим самим тестом, що й для однієї таблиці.
        Arrange(jan: 10m, feb: 5m);

        const long secondInstance = 501;
        const int secondTableDefId = 999;
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([
                new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value),
                new TableInstanceRef(secondInstance, DocumentId, secondTableDefId, Version, Period.Value),
            ]);

        await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        // Два незалежні проходи існували вже ДО фіксу (побудова плану в
        // RunAsync і завантаження значень у LoadValuesAsync) — Q-166 не про
        // їх злиття, а про те, що кожен із них ходив у базу окремо НА КОЖНУ
        // таблицю. Тепер кожен прохід — рівно один пакетний виклик.
        await _rows.Received(2).GetRowIdsBatchAsync(
            Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _cells.Received(1).ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());

        await _rows.DidNotReceive().GetRowIdsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _cells.DidNotReceive().ReadSliceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Змінена комірка першого рядка в заданій колонці.</summary>
    private static DirtySet Dirty(int columnDefId)
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Period, 1001, columnDefId));

        return dirty;
    }

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new(_cells, _rows, Substitute.For<IPeriodStore>(), _metadata, _versions, new RealFormulaEngine(), _units, _uow);
    }

    /// <summary>Комірки, які служба віддала на запис.</summary>
    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));

        return ((CellChangeSet)call.GetArguments()[0]!).Upserts;
    }

    /// <summary>
    /// Таблиця з двома місяцями і підсумком; підсумок — формула колонки.
    /// </summary>
    /// <param name="jan">Значення січня.</param>
    /// <param name="feb">Значення лютого.</param>
    /// <param name="withDependencies">
    /// <c>false</c> — граф залежностей формули порожній: саме так виглядає
    /// формула, додана в шаблон після того, як дані вже введені.
    /// </param>
    private void Arrange(decimal jan, decimal feb, bool withDependencies = true)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");

        var janColumn = builder.Column(_table, "Jan", isMonthColumn: true);
        var febColumn = builder.Column(_table, "Feb", isMonthColumn: true);
        var totalColumn = builder.Column(_table, "Total");

        builder.Row(_table, "7001001", 1);

        _janId = janColumn.Id;
        _febId = febColumn.Id;
        _totalId = totalColumn.Id;

        var formula = new FormulaDef(_table.Id, FormulaScope.Column, "[Jan] + [Feb]", ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, 100);
        typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!.SetValue(formula, _totalId);
        _table.AddFormula(formula);
        _formulaId = formula.Id;

        var snapshot = builder.Build();
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value));

        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["7001001"] = 1001 });

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value)]);

        // ⛔ Q-166: службу перемкнуто на пакетні методи (`GetRowIdsBatchAsync`,
        // `ReadSlicesAsync`) — фікстура задає ті самі дані, що й одиничні
        // виклики вище, лише в пакетній формі. Одиничні мокуються теж:
        // `ResolveTableInstanceAsync`-шлях (не всі тести проходять через
        // `LoadPeriodAsync`) досі може їх торкатися.
        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstance] = new Dictionary<string, long> { ["7001001"] = 1001 },
            });

        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(
                new CellAddress(Period, 1001, _janId), _table.Id,
                new CellValueData { ValueNumeric = jan }),
            new CellRecord(
                new CellAddress(Period, 1001, _febId), _table.Id,
                new CellValueData { ValueNumeric = feb }),
        ]);

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [TableInstance] =
                [
                    new CellRecord(
                        new CellAddress(Period, 1001, _janId), _table.Id,
                        new CellValueData { ValueNumeric = jan }),
                    new CellRecord(
                        new CellAddress(Period, 1001, _febId), _table.Id,
                        new CellValueData { ValueNumeric = feb }),
                ],
            });

        // ⚠ Граф — саме той, який зберігає публікація: формула підсумку
        // залежить від двох місячних колонок того самого рядка.
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(
            withDependencies
                ?
                [
                    FormulaDependency.ForFormula(_formulaId, 0, _table.Id, "7001001", _janId, null, null, 0),
                    FormulaDependency.ForFormula(_formulaId, 0, _table.Id, "7001001", _febId, null, null, 1),
                ]
                : []);
    }
}
