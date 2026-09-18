using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// Досяжність непропорційного обчислення — наскрізно, від формули шаблону до
/// запису в комірки, і межа, яка його зупиняє
/// (<see cref="Evaluator.MaxEvaluationSteps"/>).
/// </summary>
/// <remarks>
/// ⛔ **Замір ДО межі, щоб було з чим порівнювати.** Стенд нижче
/// (`RecalculateAllAsync` над однією таблицею за один період) виміряно на цій
/// самій машині ДО того, як бюджет з'явився:
/// <list type="table">
/// <item><term>`[Amount]`, 471 рядок</term><description>12.2 мс, 1.2 МБ</description></item>
/// <item><term>1 агрегат-діапазон, 471 рядок</term><description>228 мс, 23 МБ</description></item>
/// <item><term>30 агрегатів-діапазонів, 1107 символів</term><description>2 508 мс, 667 МБ</description></item>
/// <item><term>1 предикатний агрегат, 471 рядок</term><description>410 мс, 180 МБ</description></item>
/// <item><term>8 предикатних агрегатів, 421 символ</term><description>3 847 мс, 1 432 МБ</description></item>
/// <item><term>30 предикатних агрегатів, 1587 символів</term><description><b>16 820 мс, 5 368 МБ</b></description></item>
/// <item><term>ті самі 30 на 30 рядках</term><description>48 мс, 24 МБ</description></item>
/// </list>
/// Зростання КВАДРАТИЧНЕ: у 15.7 раза більше рядків — у 348 разів більше часу.
/// Жодне з наявних обмежень цього не зупиняло: whitelist функцій (`SUM` у
/// ньому), межа глибини (`Parser.MaxRecursionDepth = 192`, тут глибина 1),
/// межа довжини виразу (2000 символів, тут 1587), `PredicateValidator`
/// (предикат законний). Розмір таблиці не вигаданий: 471 — найважча таблиця
/// чинного `.xlsm` (`Ecr.DataGen.DistributionProfile.MaxRowsPerTable`).
///
/// ⚠ ТРЕЙТА `ФВ-13.6` тут немає навмисно. Та вимога описує захист **рівня 2**
/// (виконання користувацького C#-коду, `reference/backend/B18` §14.7), рівень 2
/// у перший реліз не входить (`ФВ-9.3`, `D-105`) і формально звільнений від
/// трасування (`contracts/trace-exempt.md`); трейт зробив би її одночасно
/// звільненою і покритою, що червонить
/// `RequirementCensusTests.Жодна_вимога_не_звільнена_і_покрита_водночас`.
/// </remarks>
public sealed class ExpressionBudgetReachabilityTests
{
    private const long DocumentId = 700;
    private const long ItemsInstance = 510;
    private const long MainInstance = 511;
    private const int Version = 1;

    /// <summary>Найважча таблиця чинного шаблону (<c>DistributionProfile</c>).</summary>
    private const int HeaviestRealTable = 471;

    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Тридцять_предикатних_агрегатів_не_дають_жодного_числа_замість_шістнадцяти_секунд()
    {
        // ⛔ ЧЕРВОНИЙ ДОКАЗ. Прибери бюджет із `Evaluator` — і цей тест стане
        // зеленим навпаки: `written` дорівнюватиме 471, бо всі 471 обчислення
        // дійдуть до кінця, витративши на це 16.8 секунди і 5.4 ГБ. Саме це
        // число (471 проти 0) і є вся різниця, яку робить межа.
        Arrange(Repeat("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])", 30));

        var sw = Stopwatch.StartNew();
        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);
        sw.Stop();

        // Жодна комірка не записана: `#BUDGET` — це помилка-значення, а
        // помилку `RecalculationService` у комірку не кладе. Це видима відмова,
        // а не підроблене число.
        Assert.Equal(0, written);
        await _cells.DidNotReceiveWithAnyArgs().ApplyAsync(null!, default);

        // ⚠ Порогу часу тут НЕМАЄ і бути не може: замір залежить від машини, а
        // тест, який червоніє від сусіднього процесу на агенті, перестає бути
        // доказом. Доказ — у `written`. Час лишається в повідомленні, щоб при
        // падінні було видно, скільки коштував прогін.
        Assert.True(
            written == 0,
            $"Прогін тривав {sw.Elapsed.TotalMilliseconds:F0} мс.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Один_предикатний_агрегат_над_найважчою_таблицею_рахується_повністю()
    {
        // ⛔ Друга половина доказу. Межа, надто тісна для чинних методологій,
        // зробила б систему непрацездатною, а публікація без зеленого
        // приймального тесту заборонена (`ФВ-9.12`). Цей вираз — зразок
        // предиката з директиви №11 (T12) і з `CascadeRecalculationTests`, і
        // над НАЙВАЖЧОЮ реальною таблицею він мусить порахуватися до кінця.
        Arrange("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])");

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(HeaviestRealTable, written);

        var upserts = Applied();
        Assert.Equal(HeaviestRealTable, upserts.Count);

        // Кожен рядок отримав суму всієї таблиці — 471 рядок по одиниці.
        Assert.All(upserts, u => Assert.Equal((decimal)HeaviestRealTable, u.Value.ValueNumeric));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Агрегат_над_діапазоном_усієї_найважчої_таблиці_рахується_повністю()
    {
        // ⚠ Друга чинна форма — `SUM` над діапазоном рядків. У корпусі чинного
        // шаблону 245 викликів `SUM` на ~10 000 формул
        // (`docs/reference/as-is/01-as-is-overview.md` §3.1).
        Arrange($"SUM([Main].[r0000:r{HeaviestRealTable - 1:D4}].[Amount])");

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(HeaviestRealTable, written);
        Assert.All(Applied(), u => Assert.Equal((decimal)HeaviestRealTable, u.Value.ValueNumeric));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Код_вичерпаного_бюджету_не_збігається_з_жодною_іншою_помилкою_значення()
    {
        // ⚠ `#BUDGET` мусить бути відрізнимим: правильна дія на нього —
        // звузити діапазон або розбити формулу, а не «виправити типи»
        // (`#VALUE`) чи «повернути видалений рядок» (`#REF`). Один код на два
        // стани не дав би відповісти, що робити.
        string[] others =
        [
            ExpressionErrors.DivideByZero, ExpressionErrors.BadReference,
            ExpressionErrors.BadValue, ExpressionErrors.BadUnit,
            ExpressionErrors.RuntimeCycle, ExpressionErrors.ArgumentNotFound,
        ];

        Assert.DoesNotContain(ExpressionErrors.BudgetExceeded, others);
        Assert.StartsWith("#", ExpressionErrors.BudgetExceeded, StringComparison.Ordinal);
    }

    private static string Repeat(string part, int times)
        => string.Join(" + ", Enumerable.Repeat(part, times));

    /// <summary>Комірки, які служба віддала на запис; порожньо — записів не було.</summary>
    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .SingleOrDefault(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));

        return call is null ? [] : ((CellChangeSet)call.GetArguments()[0]!).Upserts;
    }

    /// <summary>
    /// Динамічна <c>Items</c> і фіксована <c>Main</c>, обидві на
    /// <see cref="HeaviestRealTable"/> рядків; формула — КОЛОНКОВА на
    /// <c>Main</c>, тобто обчислюється в кожному рядку.
    /// </summary>
    /// <remarks>
    /// ⚠ Дві таблиці, а не одна, і це не ускладнення: предикат читає рядки
    /// РАНТАЙМУ (динамічна таблиця), а діапазон — рядки ШАБЛОНУ
    /// (<c>RangeExpander</c> розкриває <c>table.Rows</c>), і в динамічної
    /// таблиці їх немає за побудовою (<c>TableDef.AddRow</c> відмовляє). Одна
    /// таблиця не змогла б показати обидві форми.
    /// </remarks>
    private void Arrange(string expression)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Waste");

        var items = builder.Table(sheet, "Items", TableRowMode.Dynamic);
        var wasteType = builder.Column(items, "WasteType", CellDataType.Lookup);
        var itemAmount = builder.Column(items, "Amount");

        var main = builder.Table(sheet, "Main");
        var mainAmount = builder.Column(main, "Amount");
        var total = builder.Column(main, "Total");

        var rowKeys = new List<string>(HeaviestRealTable);
        for (var i = 0; i < HeaviestRealTable; i++)
        {
            var key = $"r{i:D4}";
            rowKeys.Add(key);
            builder.Row(main, key, i + 1);
        }

        builder.Formula(main, expression, FormulaScope.Column, column: total);

        var snapshot = builder.Build();
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([
                new TableInstanceRef(ItemsInstance, DocumentId, items.Id, Version, Period.Value),
                new TableInstanceRef(MainInstance, DocumentId, main.Id, Version, Period.Value),
            ]);

        var itemRowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var mainRowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var itemCells = new List<CellRecord>();
        var mainCells = new List<CellRecord>();

        for (var i = 0; i < HeaviestRealTable; i++)
        {
            long itemRowId = 2000 + i;
            long mainRowId = 20000 + i;
            itemRowIds[rowKeys[i]] = itemRowId;
            mainRowIds[rowKeys[i]] = mainRowId;

            itemCells.Add(new CellRecord(
                new CellAddress(Period, itemRowId, wasteType.Id), items.Id,
                new CellValueData { ValueString = "W-01" }));
            itemCells.Add(new CellRecord(
                new CellAddress(Period, itemRowId, itemAmount.Id), items.Id,
                new CellValueData { ValueNumeric = 1m }));
            mainCells.Add(new CellRecord(
                new CellAddress(Period, mainRowId, mainAmount.Id), main.Id,
                new CellValueData { ValueNumeric = 1m }));
        }

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [ItemsInstance] = itemRowIds,
                [MainInstance] = mainRowIds,
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [ItemsInstance] = itemCells,
                [MainInstance] = mainCells,
            });

        // Повний прогін бере формули з ПЛАНУ, а не з насіння, тож граф
        // залежностей тут не потрібен.
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns([]);
    }

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // ⛔ `DAT-02` п. 4 (`S-04`): запис і аудит перерахунку йдуть через
        // `IUnitOfWork.ExecuteInTransactionAsync`. Без цього налаштування
        // NSubstitute повертає typed-default і НІКОЛИ не викликає замикання —
        // `written` лишався б правильним (він рахується ДО запису), а
        // `Applied()` порожнім, тобто половина тверджень цього класу мовчки
        // перестала б щось доводити.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            _audit, new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), _uow);
    }
}
