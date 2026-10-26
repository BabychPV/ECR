using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17.1: читання логічного зрізу таблиць «місяці в колонках» (<see cref="MonthRoutedCellStore"/>,
/// <see cref="MonthColumnRouter"/>) і заповненість (<see cref="TableFillStore"/>) на реальному SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Перший принцип комміту: прапор <c>Collection:MonthColumnRouting</c> ВИМКНЕНО за замовчуванням, і тоді
/// декоратор неможливо відрізнити від <see cref="NormalizedCellStore"/> — ні результатом, ні кількістю звернень.
/// Решта тестів — ввімкнений прапор: значення місячного стовпця m живуть в екземплярі періоду m, введений
/// не-місячний — в екземплярі «якоря» (найменший період календаря), обчислені — в екземплярі перегляду.
/// </remarks>
[Collection("SqlServer")]
public sealed class MonthRoutedCellStoreTests(SqlServerFixture sql) : IDisposable
{
    private const int ViewMonth = 10;

    private const int Year = 2026;

    /// <summary>Стеля звернень на пакет: ідентичність, календар року, фізичний зріз, рядки перегляду, комірки власників.</summary>
    private const int MaxBatchCommands = 5;

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    private readonly TestDocumentBuilder _builder = new(sql.ConnectionString);

    public void Dispose() => _memory.Dispose();

    // ── (а) прапор вимкнено: ідентично фізичному зрізу ───────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Прапор_вимкнено_зріз_і_кількість_звернень_ідентичні_фізичному()
    {
        var a = await ArrangeAsync(instanceMonths: [1, 11]);
        await PutAsync(a, 11, 0, a.Doc.MonthColumnDefIds[10], 11m); // власник
        await PutAsync(a, ViewMonth, 0, a.Doc.MonthColumnDefIds[10], 999m); // копія в перегляді
        await PutAsync(a, 1, 0, a.Doc.ColumnDefIds[1], 5m); // якір
        await PutAsync(a, ViewMonth, 1, a.Doc.ColumnDefIds[1], 7m);

        await using var db = _builder.CreateContext();
        var plain = new NormalizedCellStore(db);
        var decorated = Decorate(db, plain, routing: false);

        var (plainCount, plainSlice) = await MeasureAsync(
            () => plain.ReadSlicesAsync([a.Doc.TableInstanceId], a.Doc.PeriodKey, CancellationToken.None));
        var (decoratedCount, decoratedSlice) = await MeasureAsync(
            () => decorated.ReadSlicesAsync([a.Doc.TableInstanceId], a.Doc.PeriodKey, CancellationToken.None));

        Assert.Equal(Describe(plainSlice[a.Doc.TableInstanceId]), Describe(decoratedSlice[a.Doc.TableInstanceId]));
        Assert.Equal(plainCount, decoratedCount);

        var plainOne = await plain.ReadSliceAsync(a.Doc.TableInstanceId, CancellationToken.None);
        var decoratedOne = await decorated.ReadSliceAsync(a.Doc.TableInstanceId, CancellationToken.None);
        Assert.Equal(Describe(plainOne), Describe(decoratedOne));

        // Копія в перегляді (999) — це і є «чинне» значення, поки прапор вимкнено.
        Assert.Equal(999m, ValueOf(decoratedOne, a.Doc.RowIds[0], a.Doc.MonthColumnDefIds[10]));
    }

    // ── (б) місячний стовпець: значення M11 з екземпляра 202611 видно у зрізі 202610 ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Значення_місячного_стовпця_береться_з_екземпляра_цього_місяця()
    {
        var a = await ArrangeAsync(instanceMonths: [1, 11]);
        var m11 = a.Doc.MonthColumnDefIds[10];
        var m10 = a.Doc.MonthColumnDefIds[9];

        await PutAsync(a, 11, 1, m11, 111m); // власник M11 — екземпляр 202611
        await PutAsync(a, ViewMonth, 1, m11, 999m); // застаріла копія в перегляді — затінена власником
        await PutAsync(a, ViewMonth, 1, m10, 10m); // власний стовпець перегляду — як є

        await using var db = _builder.CreateContext();
        var store = Decorate(db, new NormalizedCellStore(db), routing: true);

        var byPeriod = await store.ReadSliceAsync(a.Doc.TableInstanceId, a.Doc.PeriodKey, CancellationToken.None);
        var byId = await store.ReadSliceAsync(a.Doc.TableInstanceId, CancellationToken.None);
        var batch = await store.ReadSlicesAsync([a.Doc.TableInstanceId], CancellationToken.None);
        var batchByPeriod = await store.ReadSlicesAsync([a.Doc.TableInstanceId], a.Doc.PeriodKey, CancellationToken.None);

        foreach (var slice in new[] { byPeriod, byId, batch[a.Doc.TableInstanceId], batchByPeriod[a.Doc.TableInstanceId] })
        {
            Assert.Equal(111m, ValueOf(slice, a.Doc.RowIds[1], m11));
            Assert.Equal(10m, ValueOf(slice, a.Doc.RowIds[1], m10));
            Assert.Equal(2, slice.Count(c => c.Address.ColumnDefId == m11 || c.Address.ColumnDefId == m10));

            // Адреса — ЛОГІЧНІ: період і рядок екземпляра перегляду, а не власника.
            var logical = slice.Single(c => c.Address.ColumnDefId == m11);
            Assert.Equal(a.Doc.PeriodKey, logical.Address.PeriodKey);
            Assert.Equal(a.Doc.RowIds[1], logical.Address.TableRowId);
            Assert.Equal(a.Doc.TableDefId, logical.TableDefId);
        }

        // Той самий зріз, прочитаний з екземпляра власника, бачить те саме значення.
        var ownerSlice = await store.ReadSliceAsync(a.Others[11].TableInstanceId, a.Others[11].PeriodKey, CancellationToken.None);
        Assert.Equal(111m, ValueOf(ownerSlice, a.Others[11].RowIds[1], m11));
    }

    // ── (в) не-місячний введений стовпець — з якоря ─────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Не_місячний_введений_стовпець_береться_з_якоря_року()
    {
        var a = await ArrangeAsync(instanceMonths: [1, 11]);
        var plain = a.Doc.ColumnDefIds[1];

        await PutAsync(a, 1, 0, plain, 5m); // якір — найменший період календаря
        await PutAsync(a, 11, 0, plain, 6m); // чужа копія в іншому місяці — не власник
        await PutAsync(a, ViewMonth, 0, plain, 7m); // застаріла копія в перегляді — затінена якорем

        await using var db = _builder.CreateContext();
        var router = new MonthColumnRouter(db, Config(true));
        var store = Decorate(db, new NormalizedCellStore(db), routing: true);

        var slice = await store.ReadSliceAsync(a.Doc.TableInstanceId, a.Doc.PeriodKey, CancellationToken.None);
        Assert.Equal(5m, ValueOf(slice, a.Doc.RowIds[0], plain));
        Assert.Single(slice, c => c.Address.ColumnDefId == plain);

        // З боку самого якоря значення те саме.
        var anchorSlice = await store.ReadSliceAsync(a.Others[1].TableInstanceId, a.Others[1].PeriodKey, CancellationToken.None);
        Assert.Equal(5m, ValueOf(anchorSlice, a.Others[1].RowIds[0], plain));

        Assert.Equal(new PeriodKey((Year * 100) + 1), await router.AnchorOfAsync(a.Doc.ProjectId, Year, CancellationToken.None));
    }

    // ── (г) похідні лишаються своїми ─────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Обчислена_комірка_лишається_в_екземплярі_перегляду()
    {
        var a = await ArrangeAsync(instanceMonths: [1, 11]);
        var m11 = a.Doc.MonthColumnDefIds[10];

        await PutAsync(a, ViewMonth, 0, m11, 42m, calculated: true); // обчислена в перегляді — лишається
        await PutAsync(a, 11, 0, m11, 8m, calculated: true); // обчислена власника — чужа, не береться
        await PutAsync(a, 11, 1, m11, 9m); // введена власника — береться

        await using var db = _builder.CreateContext();
        var store = Decorate(db, new NormalizedCellStore(db), routing: true);

        var slice = await store.ReadSliceAsync(a.Doc.TableInstanceId, a.Doc.PeriodKey, CancellationToken.None);

        var calculated = slice.Single(c => c.Address.TableRowId == a.Doc.RowIds[0] && c.Address.ColumnDefId == m11);
        Assert.Equal(42m, calculated.Value.ValueNumeric);
        Assert.True(calculated.Value.IsCalculated);

        var entered = slice.Single(c => c.Address.TableRowId == a.Doc.RowIds[1] && c.Address.ColumnDefId == m11);
        Assert.Equal(9m, entered.Value.ValueNumeric);
        Assert.False(entered.Value.IsCalculated);
    }

    // ── (д) якорного екземпляра немає → порожньо, нічого не створюється ───

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Немає_екземпляра_якоря_зріз_порожній_і_екземпляр_не_створюється()
    {
        // Календар повний (рядки періодів 1..12), але екземпляр є лише в перегляді 202610.
        var a = await ArrangeAsync(instanceMonths: [], calendarOnlyMonths: Enumerable.Range(1, 12).Where(m => m != ViewMonth));
        var plain = a.Doc.ColumnDefIds[1];
        var m11 = a.Doc.MonthColumnDefIds[10];
        await PutAsync(a, ViewMonth, 0, plain, 7m); // копія в перегляді затінена порожнім якорем
        await PutAsync(a, ViewMonth, 0, m11, 3m); // так само M11: власника-екземпляра немає

        var instancesBefore = await CountInstancesAsync(a.Doc);

        await using var db = _builder.CreateContext();
        var store = Decorate(db, new NormalizedCellStore(db), routing: true);
        var slice = await store.ReadSliceAsync(a.Doc.TableInstanceId, a.Doc.PeriodKey, CancellationToken.None);

        Assert.Empty(slice);
        Assert.Equal(instancesBefore, await CountInstancesAsync(a.Doc));
    }

    // ── (е) Period-рядка нема → fallback на фізичне ──────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Немає_рядка_періоду_власника_читання_фізичне()
    {
        // У календарі лише період перегляду: ні M11, ні якір іншого місяця недоступні.
        var a = await ArrangeAsync(instanceMonths: [], calendarOnlyMonths: []);
        var plain = a.Doc.ColumnDefIds[1];
        var m11 = a.Doc.MonthColumnDefIds[10];
        await PutAsync(a, ViewMonth, 0, plain, 7m);
        await PutAsync(a, ViewMonth, 0, m11, 3m);

        await using var db = _builder.CreateContext();
        var store = Decorate(db, new NormalizedCellStore(db), routing: true);
        var slice = await store.ReadSliceAsync(a.Doc.TableInstanceId, a.Doc.PeriodKey, CancellationToken.None);

        Assert.Equal(7m, ValueOf(slice, a.Doc.RowIds[0], plain));
        Assert.Equal(3m, ValueOf(slice, a.Doc.RowIds[0], m11));
    }

    // ── таблиця поза MonthsInColumns не маршрутизується ──────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Таблиця_PerPeriodInstance_читається_фізично_навіть_з_увімкненим_прапором()
    {
        var doc = await _builder.BuildAsync(periodKey: (Year * 100) + ViewMonth, columnCount: 2, rowCount: 2);
        await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + 1));
        await using (var seed = _builder.CreateContext())
        {
            seed.CellValues.Add(new CellValue(
                new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]), doc.TableDefId, new CellValueData { ValueNumeric = 4m }));
            await seed.SaveChangesAsync();
        }

        await using var db = _builder.CreateContext();
        var store = Decorate(db, new NormalizedCellStore(db), routing: true);
        var slice = await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None);

        Assert.Equal(4m, ValueOf(slice, doc.RowIds[0], doc.ColumnDefIds[1]));
    }

    // ── (ж) без N+1 ──────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Зріз_пакета_коштує_однаково_на_3_і_на_12_таблиць_і_звичайні_таблиці_нічого_не_додають()
    {
        var three = await ArrangeBatchAsync(monthTables: 3, plainTables: 0);
        var twelve = await ArrangeBatchAsync(monthTables: 12, plainTables: 0);
        var threePlusPlain = await ArrangeBatchAsync(monthTables: 3, plainTables: 5);

        var (c3, s3) = await MeasureBatchAsync(three);
        var (c12, s12) = await MeasureBatchAsync(twelve);
        var (c3p, s3p) = await MeasureBatchAsync(threePlusPlain);

        // Пакет справді прочитав і підтягнув значення власників (інакше «мало звернень» доводило б порожнечу).
        Assert.Equal(3, s3.Count);
        Assert.Equal(12, s12.Count);
        foreach (var (batch, slices) in new[] { (three, s3), (twelve, s12), (threePlusPlain, s3p) })
        {
            foreach (var table in batch.MonthTables)
            {
                Assert.Equal(11m, ValueOf(slices[table.TableInstanceId], table.RowIds[0], table.MonthColumnDefIds[10]));
                Assert.Equal(1m, ValueOf(slices[table.TableInstanceId], table.RowIds[0], table.ColumnDefIds[1]));
            }
        }

        Assert.True(
            c3 == c12,
            $"Пакет із 3 таблиць: {c3} звернень, із 12: {c12}. Кількість звернень росте з кількістю таблиць (N+1).");
        Assert.True(
            c3 == c3p,
            $"3 місячні таблиці: {c3} звернень, разом із 5 звичайними: {c3p}. Таблиця без маршрутизованих стовпців платить.");
        Assert.True(c12 <= MaxBatchCommands, $"Пакет із 12 таблиць: {c12} звернень, стеля {MaxBatchCommands}.");
    }

    // ── (з) TableFillStore: відсоток заповнення не падає ─────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Заповненість_рахує_комірки_власників_і_не_падає_з_прапором()
    {
        var a = await ArrangeAsync(instanceMonths: [1, 11]);
        var m10 = a.Doc.MonthColumnDefIds[9];
        var m11 = a.Doc.MonthColumnDefIds[10];
        var plain = a.Doc.ColumnDefIds[1];

        // До перенесення даних: введене лежить у копіях перегляду.
        await PutAsync(a, ViewMonth, 0, m11, 1m);
        await PutAsync(a, ViewMonth, 1, m11, 1m);
        await PutAsync(a, ViewMonth, 0, plain, 1m);
        await PutAsync(a, ViewMonth, 0, m10, 1m); // власний стовпець перегляду

        await using (var db = _builder.CreateContext())
        {
            // Власники порожні: копії M11 і plain не рахуються, лишається власна M10.
            Assert.Equal(1, await FillAsync(db, a, routing: true));
        }

        // Після перенесення: значення в екземплярах власників (застарілі копії ще не прибрані).
        await PutAsync(a, 11, 0, m11, 11m);
        await PutAsync(a, 11, 1, m11, 11m);
        await PutAsync(a, 1, 0, plain, 5m);

        await using var db2 = _builder.CreateContext();
        Assert.Equal(4, await FillAsync(db2, a, routing: true)); // M11×2 + plain×1 + M10×1 — копії не подвоюють

        await using var db3 = _builder.CreateContext();
        Assert.Equal(4, await FillAsync(db3, a, routing: false)); // фізично: копії M11×2 + plain×1 + M10×1
    }

    private async Task<int> FillAsync(EcrDbContext db, Arranged a, bool routing)
    {
        var router = new MonthColumnRouter(db, Config(routing));
        var store = new TableFillStore(db, router, new MetadataCache(_memory, db));
        var counts = await store.GetFillCountsAsync(a.Doc.DocumentId, a.Doc.PeriodKey, [], CancellationToken.None);
        var table = Assert.Single(counts, c => c.TableDefId == a.Doc.TableDefId);
        return table.FilledCells;
    }

    // ── допоміжне ────────────────────────────────────────────────────────

    private static IConfiguration Config(bool routing)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = routing ? "true" : "false" })
            .Build();

    private MonthRoutedCellStore Decorate(EcrDbContext db, ICellStore inner, bool routing)
        => new(inner, new MonthColumnRouter(db, Config(routing)), new MetadataCache(_memory, db), db);

    private sealed record Arranged(TestDocument Doc, IReadOnlyDictionary<int, TestPeriodInstance> Others);

    /// <summary>Документ 202610 з таблицею «місяці в колонках» (2 звичайні колонки + M1..M12) і екземплярами вказаних місяців.</summary>
    private async Task<Arranged> ArrangeAsync(IEnumerable<int> instanceMonths, IEnumerable<int>? calendarOnlyMonths = null)
    {
        var doc = await _builder.BuildAsync(
            periodKey: (Year * 100) + ViewMonth,
            columnCount: 2,
            rowCount: 3,
            layoutKind: TableLayoutKind.MonthsInColumns,
            monthColumns: 12);

        var others = new Dictionary<int, TestPeriodInstance>();
        foreach (var month in instanceMonths)
        {
            others[month] = await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + month));
        }

        foreach (var month in calendarOnlyMonths ?? [])
        {
            await _builder.AddPeriodAsync(doc, new PeriodKey((Year * 100) + month));
        }

        return new Arranged(doc, others);
    }

    private async Task PutAsync(Arranged a, int month, int rowIndex, int columnDefId, decimal value, bool calculated = false)
    {
        var (period, rowId) = month == ViewMonth
            ? (a.Doc.PeriodKey, a.Doc.RowIds[rowIndex])
            : (a.Others[month].PeriodKey, a.Others[month].RowIds[rowIndex]);

        await using var db = _builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(period, rowId, columnDefId),
            a.Doc.TableDefId,
            new CellValueData { ValueNumeric = value, IsCalculated = calculated }));
        await db.SaveChangesAsync();
    }

    private async Task<int> CountInstancesAsync(TestDocument doc)
    {
        await using var db = _builder.CreateContext();
        return await db.TableInstances.CountAsync(t => t.DocumentId == doc.DocumentId);
    }

    private sealed record Batch(IReadOnlyList<TestDocument> MonthTables, IReadOnlyList<long> AllIds, TestDocument Doc);

    /// <summary>
    /// Документ із <paramref name="monthTables"/> таблицями «місяці в колонках» і <paramref name="plainTables"/>
    /// звичайними; у кожній місячній M11 у 202611 = 11, колонка 2 у якорі 202601 = 1.
    /// </summary>
    private async Task<Batch> ArrangeBatchAsync(int monthTables, int plainTables)
    {
        var first = await _builder.BuildAsync(
            periodKey: (Year * 100) + ViewMonth, columnCount: 2, rowCount: 3,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        var tables = new List<TestDocument> { first };
        for (var i = 1; i < monthTables; i++)
        {
            tables.Add(await _builder.AddTableAsync(first, TableLayoutKind.MonthsInColumns, monthColumns: 12));
        }

        var plain = new List<TestDocument>();
        for (var i = 0; i < plainTables; i++)
        {
            plain.Add(await _builder.AddTableAsync(first, TableLayoutKind.PerPeriodInstance, monthColumns: 0));
        }

        for (var month = 1; month <= 12; month++)
        {
            await _builder.AddPeriodAsync(first, new PeriodKey((Year * 100) + month));
        }

        foreach (var table in tables)
        {
            var anchor = await _builder.AddPeriodInstanceAsync(table, new PeriodKey((Year * 100) + 1));
            var november = await _builder.AddPeriodInstanceAsync(table, new PeriodKey((Year * 100) + 11));

            await using var db = _builder.CreateContext();
            db.CellValues.Add(new CellValue(
                new CellAddress(november.PeriodKey, november.RowIds[0], table.MonthColumnDefIds[10]),
                table.TableDefId, new CellValueData { ValueNumeric = 11m }));
            db.CellValues.Add(new CellValue(
                new CellAddress(anchor.PeriodKey, anchor.RowIds[0], table.ColumnDefIds[1]),
                table.TableDefId, new CellValueData { ValueNumeric = 1m }));
            await db.SaveChangesAsync();
        }

        return new Batch(tables, [.. tables.Concat(plain).Select(t => t.TableInstanceId)], first);
    }

    private async Task<(int Count, IReadOnlyDictionary<long, IReadOnlyList<CellRecord>> Slices)> MeasureBatchAsync(Batch batch)
    {
        // Розігрів: кеш метаданих (спільний _memory) не вважається вартістю пакета.
        await using var warm = _builder.CreateContext();
        await Decorate(warm, new NormalizedCellStore(warm), routing: true)
            .ReadSlicesAsync(batch.AllIds, batch.Doc.PeriodKey, CancellationToken.None);

        // Замір на СВІЖОМУ роутері: кеш scope (ідентичність, календар) порожній, тож рахується повна ціна пакета.
        await using var fresh = _builder.CreateContext();
        var measured = Decorate(fresh, new NormalizedCellStore(fresh), routing: true);
        return await MeasureAsync(
            () => measured.ReadSlicesAsync(batch.AllIds, batch.Doc.PeriodKey, CancellationToken.None));
    }

    private static async Task<(int Count, T Result)> MeasureAsync<T>(Func<Task<T>> action)
    {
        using var counter = new SqlClientCommandCounter(new CommandTally(), _ => Measuring.Value is { Value: true });
        counter.Tally.Reset();

        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        T result;
        try
        {
            result = await action();
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        return (counter.Tally.Snapshot().Total, result);
    }

    private static decimal? ValueOf(IReadOnlyList<CellRecord> slice, long rowId, int columnDefId)
        => slice.SingleOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == columnDefId)?.Value.ValueNumeric;

    /// <summary>Усі поля комірки рядком — порівняння побайтне.</summary>
    private static string Describe(IReadOnlyList<CellRecord> cells)
    {
        var text = new StringBuilder();
        foreach (var c in cells.OrderBy(c => c.Address.TableRowId).ThenBy(c => c.Address.ColumnDefId))
        {
            var v = c.Value;
            text.Append(CultureInfo.InvariantCulture,
                $"{c.Address.PeriodKey.Value}|{c.Address.TableRowId}|{c.Address.ColumnDefId}|{c.TableDefId}|"
                + $"{v.ValueString}|{v.ValueNumeric}|{v.ValueDate:O}|{v.ValueBool}|{v.ValueRegistryEntryId}|"
                + $"{v.ValueUnitId}|{v.IsCalculated}|{v.IsEmpty};");
        }

        return text.ToString();
    }
}
