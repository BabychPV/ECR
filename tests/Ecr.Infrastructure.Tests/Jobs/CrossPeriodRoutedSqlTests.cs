// tests/Ecr.Infrastructure.Tests/Jobs/CrossPeriodRoutedSqlTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-17.4: перерахунок читачів після зміни ВВЕДЕНОГО стовпця таблиці «місяці в колонках» з іншим власником —
/// наскрізно на справжньому SQL Server (роутер власності, декоратор сховища комірок, fan-out, задача формул).
/// </summary>
/// <remarks>
/// <para>
/// Світ: документ 202610 (перегляд) з таблицею «місяці в колонках» (C1 текст, IN — не-місячний введений, OUT —
/// обчислюваний, M1..M12) і екземплярами місяців 1 (якір), 9 і 11 (власник M11). Формула колонки OUT: <c>[M11] * 2</c>.
/// Значення M11 живе ЛИШЕ в екземплярі 202611; усі інші екземпляри читають його через роутер.
/// </para>
/// <para>
/// ⛔ Мутації (у detached-worktree, не комітити): (1) у <c>CrossPeriodReaders.ReaderSequences</c> прибрати блок
/// <c>ReadsRoutedInput</c> — читачі не ставляться, тести 9/24 червоні; (2) у <c>CrossPeriodReaders.Targets</c> прибрати
/// гілку <c>OwnerInput</c> — читач ставиться, але формули не цілі; (3) у <c>RecalculationService.Evaluate</c> прибрати
/// пропуск <c>foreignColumns</c> — перерахунок перезаписує введену комірку власника (тест «не пише»);
/// (4) у <c>RecalculationService.RunAsync</c> прибрати <c>BeginOwnerReadSession</c> — власники читаються на кожен період.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CrossPeriodRoutedSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private const int Year = 2026;
    private const int ViewMonth = 10;
    private const int OwnerMonth = 11;
    private const int FormulaId = 920_401;
    private const decimal StaleOutput = -1m;

    // ── сценарій 9: читачі M11 з нульовим зсувом ─────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Сценарій_9_правка_власника_M11_перераховує_формулу_що_читає_M11_в_усіх_інших_екземплярах()
    {
        var world = await ArrangeAsync();
        await SetOwnerValueAsync(world, 7m);

        var enqueued = new List<(string Target, string Payload)>();
        var progress = await RunJobAsync(world, enqueued, OwnerRequest(world), routing: true);

        // Власний період власника — через звичайний план (читає свою фізичну комірку).
        Assert.Equal(14m, await OutputAsync(world, Period(OwnerMonth)));

        // L2: читачі — усі наявні періоди року, окрім джерела, — дораховуються тим самим проходом, без нових задач.
        Assert.Empty(enqueued);
        Assert.Equal(
            [Period(1), Period(9), Period(ViewMonth), Period(OwnerMonth)],
            progress.Periods);

        // Читачі бачать M11 власника через роутер: 7 × 2 у КОЖНОМУ екземплярі.
        Assert.Equal(14m, await OutputAsync(world, Period(1)));
        Assert.Equal(14m, await OutputAsync(world, Period(9)));
        Assert.Equal(14m, await OutputAsync(world, Period(ViewMonth)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Сценарій_9_правка_з_перегляду_іншого_місяця_теж_перераховує_власника_і_решту()
    {
        var world = await ArrangeAsync();
        await SetOwnerValueAsync(world, 7m);

        // Каскад іде з перегляду 202610 (запис маршрутизовано в 202611, насіння — рядок перегляду).
        var enqueued = new List<(string Target, string Payload)>();
        var progress = await RunJobAsync(
            world, enqueued,
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, Period(ViewMonth),
                [new DirtyCell(world.Doc.RowIds[0], world.Doc.MonthColumnDefIds[OwnerMonth - 1])]),
            routing: true);

        // Сам перегляд перераховано локально (читає логічний зріз із значенням власника).
        Assert.Equal(14m, await OutputAsync(world, Period(ViewMonth)));

        // Читачі — усі, окрім періоду каскаду; власник серед них — у тому самому проході.
        Assert.Empty(enqueued);
        Assert.Equal(
            [Period(1), Period(9), Period(ViewMonth), Period(OwnerMonth)],
            progress.Periods);

        Assert.Equal(14m, await OutputAsync(world, Period(OwnerMonth)));
        Assert.Equal(14m, await OutputAsync(world, Period(1)));
        Assert.Equal(14m, await OutputAsync(world, Period(9)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Сценарій_9_абсолютне_посилання_Period_11_читає_екземпляр_11()
    {
        var world = await ArrangeAsync(absolute: true);
        await SetOwnerValueAsync(world, 7m);

        var enqueued = new List<(string Target, string Payload)>();
        await RunJobAsync(world, enqueued, OwnerRequest(world), routing: true);
        Assert.Empty(enqueued);

        // [Period:#11].[M11] * 2 — те саме число у кожному читачі, що б не лежало в його власній копії.
        Assert.Equal(14m, await OutputAsync(world, Period(ViewMonth)));
        Assert.Equal(14m, await OutputAsync(world, Period(9)));
        Assert.Equal(14m, await OutputAsync(world, Period(1)));
    }

    // ── сценарій 24: закритий період не перераховується ──────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Сценарій_24_закритий_період_читач_не_ставиться_і_не_перераховується()
    {
        var world = await ArrangeAsync();
        await SetOwnerValueAsync(world, 7m);
        await ClosePeriodAsync(world, 9);

        var enqueued = new List<(string Target, string Payload)>();
        var progress = await RunJobAsync(world, enqueued, OwnerRequest(world), routing: true);

        Assert.Empty(enqueued);
        Assert.Equal([Period(1), Period(ViewMonth), Period(OwnerMonth)], progress.Periods);
        Assert.Equal(14m, await OutputAsync(world, Period(ViewMonth)));
        Assert.Equal(StaleOutput, await OutputAsync(world, Period(9)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Сценарій_24_період_закрито_між_постановкою_і_виконанням_задача_нічого_не_пише()
    {
        var world = await ArrangeAsync();
        await SetOwnerValueAsync(world, 7m);
        await ClosePeriodAsync(world, 9);

        // Задача періоду-читача зі старим форматом (як у черзі до L2: період — у тілі, елемент — «джерело зміни»).
        var legacy = new FormulaRecalculationRequest(
            0, Period(9),
            [new DirtyCell(0, world.Doc.MonthColumnDefIds[OwnerMonth - 1], world.Doc.TableDefId, OwnerMonth, 1, OwnerInput: true)],
            world.Doc.DocumentId);
        var progress = await RunJobAsync(world, [], legacy, routing: true);

        Assert.Equal(StaleOutput, await OutputAsync(world, Period(9)));
        Assert.Equal(0, progress.Written);
    }

    // ── перерахунок не пише введені комірки власників ─────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Перерахунок_не_створює_копій_введених_комірок_власника_в_інших_екземплярах()
    {
        var world = await ArrangeAsync();
        await SetOwnerValueAsync(world, 7m);

        var enqueued = new List<(string Target, string Payload)>();
        await RunJobAsync(world, enqueued, OwnerRequest(world), routing: true);
        Assert.Empty(enqueued);

        // M11 фізично є лише в екземплярі власника: перерахунок читачів писав тільки обчислену OUT.
        Assert.Equal(
            1,
            await CountAsync(
                $"SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
                $"WHERE cv.ColumnDefId = {world.Doc.MonthColumnDefIds[OwnerMonth - 1]} " +
                $"AND r.TableInstanceId IN (SELECT Id FROM doc.TableInstance WHERE DocumentId = {world.Doc.DocumentId})"));
    }

    // ── прапор вимкнено: поведінка ідентична ──────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Прапор_вимкнено_правка_M11_не_ставить_читачів_і_читає_фізичні_копії()
    {
        var world = await ArrangeAsync();

        // Старе зберігання: у кожному екземплярі своя копія M11 (різні числа).
        await SetCellAsync(world, Period(OwnerMonth), world.Doc.MonthColumnDefIds[OwnerMonth - 1], 7m);
        await SetCellAsync(world, Period(ViewMonth), world.Doc.MonthColumnDefIds[OwnerMonth - 1], 100m);

        var enqueued = new List<(string Target, string Payload)>();
        await RunJobAsync(
            world, enqueued,
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, Period(ViewMonth),
                [new DirtyCell(world.Doc.RowIds[0], world.Doc.MonthColumnDefIds[OwnerMonth - 1])]),
            routing: false);

        // Перегляд рахує від СВОЄЇ копії (100 × 2), інших періодів не торкається.
        Assert.Equal(200m, await OutputAsync(world, Period(ViewMonth)));
        Assert.Empty(enqueued);
        Assert.Equal(StaleOutput, await OutputAsync(world, Period(9)));
    }

    // ── сценарій 25: власники читаються один раз на задачу ────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Сценарій_25_методологія_k_бачить_значення_власника_а_власники_читаються_не_частіше_ніж_раз_на_ключ()
    {
        var world = await ArrangeAsync(offsets: true);
        await SetOwnerValueAsync(world, 7m);

        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Config(true));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var service = Formulas(db, world, store, router);

        // Вихід = [M11] + [Period:-1].[M11] + [Period:+1].[M11]: три періоди читання (10, 9, 11), і в кожному
        // M11 — значення ВЛАСНИКА (7): 7 + 7 + 7. Без роутера періоди 10 і 9 бачили б порожню копію.
        await service.RecalculateAllAsync(world.Doc.DocumentId, new PeriodKey(Period(ViewMonth)), CancellationToken.None);

        Assert.Equal(21m, await OutputAsync(world, Period(ViewMonth)));

        // Стеля ТЗ — не більше 13 читань власників на пакет; сесія задачі дає менше: кожен ключ
        // (документ, період-власник, таблиця) читається один раз. Без сесії було б по запиту на кожен період читання.
        var withSession = store.OwnerQueryCount;
        Assert.InRange(withSession, 1, 13);

        await using var db2 = world.Builder.CreateContext();
        var plainStore = new MonthRoutedCellStore(
            new NormalizedCellStore(db2), new MonthColumnRouter(db2, Config(true)), world.Metadata, db2);
        foreach (var month in new[] { ViewMonth, 9, OwnerMonth })
        {
            var ids = await InstanceIdsAsync(world, Period(month));
            await plainStore.ReadSlicesAsync(ids, new PeriodKey(Period(month)), CancellationToken.None);
        }

        Assert.True(
            withSession < plainStore.OwnerQueryCount,
            $"Сесія задачі: {withSession} читань власників, без сесії: {plainStore.OwnerQueryCount}. Кеш не спрацював.");
    }

    // ── L6: правка в 12-періодному році (звіт S07: січень тече у дванадцять періодів) ─────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task L6_дванадцять_періодів_задача_дає_ті_самі_числа_що_й_повний_перерахунок_по_періодах()
    {
        var job = await ArrangeAsync(offsets: true, fullYear: true);
        var reference = await ArrangeAsync(offsets: true, fullYear: true);
        await SetOwnerValueAsync(job, 7m);
        await SetOwnerValueAsync(reference, 7m);

        var progress = await RunJobAsync(job, [], OwnerRequest(job), routing: true);
        Assert.Equal(Enumerable.Range(1, 12).Select(Period).ToList(), progress.Periods);

        // Еталон: ті самі дані, але кожен період — окремий повний прогін зі своєю (не спільною) сесією читання власників.
        await using (var db = reference.Builder.CreateContext())
        {
            var router = new MonthColumnRouter(db, Config(true));
            var plain = new MonthRoutedCellStore(new NormalizedCellStore(db), router, reference.Metadata, db);
            var service = Formulas(db, reference, plain, router);
            for (var month = 1; month <= 12; month++)
            {
                await service.RecalculateAllAsync(
                    reference.Doc.DocumentId, new PeriodKey(Period(month)), CancellationToken.None);
            }
        }

        for (var month = 1; month <= 12; month++)
        {
            var expected = await OutputAsync(reference, Period(month));
            Assert.NotEqual(StaleOutput, expected);
            Assert.Equal(expected, await OutputAsync(job, Period(month)));
        }

        // Не порожній збіг: у середині року формула бачить власника тричі (M11 + сусіди) — 7 + 7 + 7.
        Assert.Equal(21m, await OutputAsync(job, Period(5)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task L6_дванадцять_періодів_власники_читаються_кілька_разів_на_ЗАДАЧУ_а_не_по_запиту_на_читача()
    {
        var world = await ArrangeAsync(offsets: true, fullYear: true);
        await SetOwnerValueAsync(world, 7m);

        var (progress, store) = await RunJobWithStoreAsync(world, [], OwnerRequest(world), routing: true);
        Assert.Equal(12, progress.Periods.Count);

        // Стеля: перше читання (насіння) + доповнення для періоду-власника; не 12+ (по одному на кожного читача).
        // Кожне таке читання — скан введених комірок усіх таблиць за 12 періодів (звіт S07: ~124 тис. читань).
        Assert.InRange(store.OwnerQueryCount, 1, 3);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task L6_запит_власників_повертає_ті_самі_рядки_що_й_колишній_відбір_з_OR_EXISTS()
    {
        var world = await ArrangeAsync(fullYear: true);

        // Місячні колонки: власник кожної — свій місяць; у решті екземплярів лежать «чужі копії» (мають бути відкинуті).
        // Не-місячна введена колонка IN: у якорі (період 1) — потрібна, в інших періодах — ні.
        for (var owner = 1; owner <= 12; owner++)
        {
            for (var column = 1; column <= 12; column++)
            {
                await SetCellAsync(
                    world, Period(owner), world.Doc.MonthColumnDefIds[column - 1], (owner * 100m) + column);
            }

            await SetCellAsync(world, Period(owner), world.Doc.ColumnDefIds[1], 9000m + owner);
        }

        var periods = Enumerable.Range(1, 12).Select(Period).ToList();
        IReadOnlyCollection<int> tables = [world.Doc.TableDefId];
        IReadOnlyCollection<int> anchors = [Period(1)];

        await using var db = world.Builder.CreateContext();
        var current = await MonthRoutedCellStore.OwnerCellsQuery(db, world.Doc.DocumentId, periods, tables, anchors)
            .ToListAsync();

        // Колишній відбір (до L6), дослівно: anchor OR EXISTS(ColumnDef).
        var old = await (from instance in db.TableInstances.AsNoTracking()
                         where instance.DocumentId == world.Doc.DocumentId
                               && periods.Contains(instance.PeriodKeyValue)
                               && tables.Contains(instance.TableDefId)
                         join row in db.TableRows.AsNoTracking().Where(r => periods.Contains(r.PeriodKeyValue) && !r.IsDeleted)
                             on new { P = instance.PeriodKeyValue, I = instance.Id }
                             equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                         join cell in db.CellValues.AsNoTracking()
                                 .Where(c => periods.Contains(c.PeriodKeyValue) && !c.IsCalculated)
                             on new { P = row.PeriodKeyValue, R = row.Id }
                             equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
                         where anchors.Contains(cell.PeriodKeyValue)
                               || db.ColumnDefs.Any(column => column.Id == cell.ColumnDefId
                                                              && column.IsMonthColumn
                                                              && column.MonthNumber == cell.PeriodKeyValue % 100)
                         select new { cell.PeriodKeyValue, row.RowKeyValue, cell.ColumnDefId, cell.ValueNumeric })
            .ToListAsync();

        static string Key(int period, string row, int column, decimal? value)
            => string.Create(CultureInfo.InvariantCulture, $"{period}|{row}|{column}|{value}");

        var expected = old.Select(r => Key(r.PeriodKeyValue, r.RowKeyValue, r.ColumnDefId, r.ValueNumeric)).Order().ToList();
        var actual = current.Select(r => Key(r.OwnerPeriodKey, r.RowKey, r.ColumnDefId, r.ValueNumeric)).Order().ToList();

        // Не порожній збіг: якір дає 12 місячних + IN, решта 11 періодів — по одній місячній (власної колонки).
        Assert.Equal(12 + 1 + 11, expected.Count);
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Count, actual.Distinct().Count());
    }
    // ── допоміжне ────────────────────────────────────────────────────────

    private static int Period(int month) => (Year * 100) + month;

    private static IConfiguration Config(bool routing)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = routing ? "true" : "false" })
            .Build();

    /// <summary>Запит задачі на екземпляр власника: насіння — введена комірка M11.</summary>
    private static FormulaRecalculationRequest OwnerRequest(World world)
        => new(
            world.Others[OwnerMonth].TableInstanceId, Period(OwnerMonth),
            [new DirtyCell(world.Others[OwnerMonth].RowIds[0], world.Doc.MonthColumnDefIds[OwnerMonth - 1])]);

    private async Task<World> ArrangeAsync(bool absolute = false, bool offsets = false, bool fullYear = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(ViewMonth), columnCount: 3, rowCount: 1,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        // L6: fullYear — усі 12 місяців року (звіт S07: правка січня тече у дванадцять періодів).
        var otherMonths = fullYear
            ? Enumerable.Range(1, 12).Where(m => m != ViewMonth).ToArray()
            : new[] { 1, 9, OwnerMonth };
        var allMonths = fullYear ? Enumerable.Range(1, 12).ToArray() : new[] { 1, 9, ViewMonth, OwnerMonth };

        var others = new Dictionary<int, TestPeriodInstance>();
        foreach (var month in otherMonths)
        {
            others[month] = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Period(month)));
        }

        await using (var setup = builder.CreateContext())
        {
            foreach (var month in allMonths)
            {
                var period = await setup.Periods.SingleAsync(
                    p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Period(month));
                period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            }

            await setup.SaveChangesAsync();
        }

        var world = new World(builder, doc, others, Metadata(doc, absolute, offsets), absolute, offsets);

        // Застарілий вихід у кожному екземплярі; M11 власника порожнє (до першої правки).
        foreach (var month in allMonths)
        {
            await SetCellAsync(world, Period(month), doc.ColumnDefIds[2], StaleOutput, calculated: true);
        }

        return world;
    }

    private Task SetOwnerValueAsync(World world, decimal value)
        => SetCellAsync(world, Period(OwnerMonth), world.Doc.MonthColumnDefIds[OwnerMonth - 1], value);

    private async Task SetCellAsync(World world, int period, int columnDefId, decimal value, bool calculated = false)
    {
        var rowId = period == Period(ViewMonth) ? world.Doc.RowIds[0] : world.Others[period % 100].RowIds[0];
        await ExecuteAsync(
            $"DELETE FROM doc.CellValue WHERE PeriodKey = {period} AND TableRowId = {rowId} AND ColumnDefId = {columnDefId}; " +
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({period}, {rowId}, {columnDefId}, {world.Doc.TableDefId}, " +
            $"{value.ToString(CultureInfo.InvariantCulture)}, {(calculated ? 1 : 0)}, 0);");
    }

    private async Task ClosePeriodAsync(World world, int month)
    {
        await using var setup = world.Builder.CreateContext();
        var period = await setup.Periods.SingleAsync(
            p => p.ProjectId == world.Doc.ProjectId && p.PeriodKeyValue == Period(month));
        period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);
        await setup.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<long>> InstanceIdsAsync(World world, int period)
    {
        await using var db = world.Builder.CreateContext();
        return await db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == world.Doc.DocumentId && i.PeriodKeyValue == period)
            .Select(i => i.Id)
            .ToListAsync();
    }

    private async Task<CapturingJobProgress> RunJobAsync(
        World world, List<(string Target, string Payload)> enqueued, FormulaRecalculationRequest request, bool routing)
        => (await RunJobWithStoreAsync(world, enqueued, request, routing)).Progress;

    private async Task<(CapturingJobProgress Progress, MonthRoutedCellStore Store)> RunJobWithStoreAsync(
        World world, List<(string Target, string Payload)> enqueued, FormulaRecalculationRequest request, bool routing)
    {
        await using var db = world.Builder.CreateContext();

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                enqueued.Add((call.ArgAt<string>(0), JsonSerializer.Serialize(call.ArgAt<object?>(1))));
                return "job";
            });

        var router = new MonthColumnRouter(db, Config(routing));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var periods = Periods(world);
        var fanOut = new CrossPeriodFanOut(
            new RowStore(db, bulk, new TestClock(Now)), Versions(world), periods, jobs, db, router, world.Metadata);

        var job = new FormulaRecalculationJob(Formulas(db, world, store, router), db, fanOut, periods);
        var progress = new CapturingJobProgress();
        await job.ExecuteAsync(request, progress, CancellationToken.None);

        return (progress, store);
    }

    private static RecalculationService Formulas(
        EcrDbContext db, World world, ICellStore store, IMonthColumnRouter router)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            store, new RowStore(db, bulk, clock), Periods(world), world.Metadata,
            Versions(world), new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db), monthRouter: router);
    }

    private static IPeriodStore Periods(World world)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 1, 31)));

        // Стан береться з бази: закритий період має бути видно й задачі, і fan-out.
        periods.FindPeriodStateAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await using var db = world.Builder.CreateContext();
                var key = call.ArgAt<int>(1);
                return await db.Periods
                    .Where(p => p.ProjectId == world.Doc.ProjectId && p.PeriodKeyValue == key)
                    .Select(p => (PeriodState?)p.State)
                    .FirstOrDefaultAsync();
            });

        return periods;
    }

    private static ITemplateVersionStore Versions(World world)
    {
        var m11 = world.Doc.MonthColumnDefIds[OwnerMonth - 1];
        var versions = Substitute.For<ITemplateVersionStore>();

        // Залежність-комірка зі зсувом null на M11 (звичайне посилання [M11]); для режимів зі зсувами —
        // додаткові міжперіодні ребра на ту саму колонку.
        List<FormulaDependency> dependencies;
        if (world.Absolute)
        {
            dependencies =
            [
                FormulaDependency.ForFormula(
                    FormulaId, dependsOnKind: 5, world.Doc.TableDefId, rowKey: null, m11,
                    filterJson: null, periodOffset: OwnerMonth, sortOrder: 0),
            ];
        }
        else
        {
            dependencies =
            [
                FormulaDependency.ForFormula(
                    FormulaId, dependsOnKind: 0, world.Doc.TableDefId, rowKey: null, m11,
                    filterJson: null, periodOffset: null, sortOrder: 0),
            ];

            if (world.Offsets)
            {
                dependencies.Add(FormulaDependency.ForFormula(
                    FormulaId, dependsOnKind: 3, world.Doc.TableDefId, rowKey: null, m11,
                    filterJson: null, periodOffset: -1, sortOrder: 1));
                dependencies.Add(FormulaDependency.ForFormula(
                    FormulaId, dependsOnKind: 3, world.Doc.TableDefId, rowKey: null, m11,
                    filterJson: null, periodOffset: 1, sortOrder: 2));
            }
        }

        versions.ListFormulaDependenciesAsync(world.Doc.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(dependencies);

        return versions;
    }

    private static IMetadataCache Metadata(TestDocument doc, bool absolute, bool offsets)
    {
        var text = ColumnDefFor(doc, doc.ColumnDefIds[0], "C1", 1, CellDataType.String);
        var input = ColumnDefFor(doc, doc.ColumnDefIds[1], "IN", 2, CellDataType.Decimal);
        var output = ColumnDefFor(doc, doc.ColumnDefIds[2], "OUT", 3, CellDataType.Formula);

        var months = new List<ColumnDef>();
        for (var m = 1; m <= 12; m++)
        {
            var column = ColumnDefFor(doc, doc.MonthColumnDefIds[m - 1], $"M{m}", 3 + m, CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            months.Add(column);
        }

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);
        foreach (var month in months)
        {
            table.AddColumn(month);
        }

        var expression = absolute
            ? "[Period:#11].[M11] * 2"
            : offsets
                ? "[M11] + [Period:-1].[M11] + [Period:+1].[M11]"
                : "[M11] * 2";
        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(output.Id);
        table.AddFormula(formula);

        sheet.AddTable(table);

        var byId = new Dictionary<int, ColumnDef> { [text.Id] = text, [input.Id] = input, [output.Id] = output };
        foreach (var month in months)
        {
            byId[month.Id] = month;
        }

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: byId, RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int id, string code, int ordinal, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private async Task<decimal?> OutputAsync(World world, int period)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cv.ValueNumeric FROM doc.CellValue cv JOIN doc.TableRow r " +
            "ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            $"WHERE cv.PeriodKey = {period} AND cv.ColumnDefId = {world.Doc.ColumnDefIds[2]} " +
            $"AND r.TableInstanceId IN (SELECT Id FROM doc.TableInstance WHERE DocumentId = {world.Doc.DocumentId})";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task<int> CountAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        IReadOnlyDictionary<int, TestPeriodInstance> Others,
        IMetadataCache Metadata,
        bool Absolute,
        bool Offsets);

}
