// tests/Ecr.Infrastructure.Tests/Jobs/CrossPeriodRoutedSqlTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
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
    // ── B124: видалення рядка — склад року змінився в усіх екземплярах ──────

    /// <summary>
    /// ⛔ B124: видалення рядка таблиці «місяці в колонках» дзеркально прибирає його з УСІХ екземплярів року (D-457 п.3),
    /// разом зі значеннями власників (M11 рядка 2 у 202611). Агрегат <c>SUM(M11)</c> в інших періодах мусить
    /// перерахуватися тим самим проходом, як після правки M11 (п.6), а не лишатися з видаленим рядком до наступної правки.
    /// Мутація: у <c>RecalculationService.RunAsync</c> не класти колонки насіння видаленого рядка в <c>inputColumns</c> —
    /// читачів немає, періоди 1/9/11 лишаються -1 (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    public async Task B124_видалення_рядка_з_перегляду_перераховує_агрегат_власника_в_усіх_інших_екземплярах()
    {
        var world = await ArrangeAsync(sum: true);
        var m11 = world.Doc.MonthColumnDefIds[OwnerMonth - 1];
        await SetOwnerValueAsync(world, 7m);
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({Period(OwnerMonth)}, {world.Others[OwnerMonth].RowIds[1]}, {m11}, {world.Doc.TableDefId}, 5, 0, 0);");

        // Те, що робить DeleteRowHandler у дзеркальному режимі: рядок 2 м'яко видалено в кожному екземплярі року.
        await using (var db = world.Builder.CreateContext())
        {
            var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
            Assert.True(await rows.SoftDeleteRowAsync(world.Doc.RowIds[1], new PeriodKey(Period(ViewMonth)), CancellationToken.None));
            foreach (var (month, instance) in world.Others)
            {
                Assert.True(await rows.SoftDeleteRowAsync(instance.RowIds[1], new PeriodKey(Period(month)), CancellationToken.None));
            }
        }

        // Насіння — усі колонки видаленого рядка перегляду (як `DeleteRowHandler.EnqueueRecalculationAsync`).
        var seeds = world.Doc.ColumnDefIds.Concat(world.Doc.MonthColumnDefIds)
            .Select(column => new DirtyCell(world.Doc.RowIds[1], column))
            .ToList();
        var enqueued = new List<(string Target, string Payload)>();
        var progress = await RunJobAsync(
            world, enqueued, new FormulaRecalculationRequest(world.Doc.TableInstanceId, Period(ViewMonth), seeds), routing: true);

        Assert.Empty(enqueued);
        Assert.Equal([Period(1), Period(9), Period(ViewMonth), Period(OwnerMonth)], progress.Periods);
        foreach (var month in new[] { 1, 9, ViewMonth, OwnerMonth })
        {
            Assert.Equal(7m, await OutputAsync(world, Period(month)));
        }
    }

    /// <summary>
    /// B124 (регресія): дзеркальне створення рядка (<c>CreateRowHandler.CreateMirroredUnderLockAsync</c>) перерахунку не
    /// ставить — і не мусить: порожній рядок не змінює агрегатів (SUM/COUNT/AVERAGE пропускають порожнє). Збережені
    /// значення кожного місяця після створення дорівнюють свіжому повному перерахунку. Мутація: дати новому рядку
    /// значення M11 у власника — свіжий перерахунок розходиться зі збереженим (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    public async Task B124_дзеркальне_створення_порожнього_рядка_не_робить_агрегати_інших_місяців_застарілими()
    {
        var world = await ArrangeAsync(sum: true);
        await SetOwnerValueAsync(world, 7m);
        await RunJobAsync(world, [], OwnerRequest(world), routing: true);

        var months = new[] { 1, 9, ViewMonth, OwnerMonth };
        var stored = new Dictionary<int, decimal?>();
        foreach (var month in months)
        {
            stored[month] = await OutputAsync(world, Period(month));
            Assert.Equal(7m, stored[month]);
        }

        // Те, що робить CreateRowHandler у дзеркальному режимі: рядок у кожному наявному екземплярі року, без задачі.
        await AddMirrorRowAsync(world, "B124NEW");

        await using (var db = world.Builder.CreateContext())
        {
            var router = new MonthColumnRouter(db, Config(true));
            var service = Formulas(db, world, new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db), router);
            foreach (var month in months)
            {
                await service.RecalculateAllAsync(world.Doc.DocumentId, new PeriodKey(Period(month)), CancellationToken.None);
            }
        }

        foreach (var month in months)
        {
            Assert.Equal(stored[month], await OutputAsync(world, Period(month)));
        }
    }

    /// <summary>
    /// ⛔ B124 (D-491): колонка M11 з дефолтом 3 — новий рядок ЗМІНЮЄ агрегат (7 + 3 + 3 → 13). Дзеркальне створення з
    /// перегляду жовтня (справжній <c>CreateRowHandler</c>) ставить задачу формул з насінням «новий рядок × M11», і її
    /// прогін дає 13 у КОЖНОМУ місяці року. Мутація: прибрати <c>NewRowDefaultColumns</c> з насіння — задачі немає,
    /// усі місяці лишаються 10 (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    public async Task B124_дзеркальне_створення_рядка_з_дефолтом_перераховує_агрегат_у_всіх_місяцях()
    {
        var world = await ArrangeAsync(sum: true, m11Default: "3");
        await SetOwnerValueAsync(world, 7m);
        await RunJobAsync(world, [], OwnerRequest(world), routing: true);

        var months = new[] { 1, 9, ViewMonth, OwnerMonth };
        foreach (var month in months)
        {
            // Рядок 1 — 7, рядок 2 порожній → дефолт 3.
            Assert.Equal(10m, await OutputAsync(world, Period(month)));
        }

        var (jobs, payload) = CapturingJobs();
        await CreateRowAsync(world, jobs, [.. months.Select(Period)]);
        Assert.NotNull(payload());

        // F13 M4: перегляд (жовтень) — не перший за періодом екземпляр; насіння — рядок САМЕ екземпляра запиту.
        var own = await RowIdAsync(Period(ViewMonth), world.Doc.TableInstanceId, "B124NEW");
        Assert.All(PayloadRowIds(payload()), id => Assert.Equal(own, id));

        await RunJobWithStoreAsync(world, [], payload(), routing: true);
        foreach (var month in months)
        {
            Assert.Equal(13m, await OutputAsync(world, Period(month)));
        }
    }

    /// <summary>
    /// B124 (межа правила): видалення рядка в НЕмаршрутизованій таблиці (не «місяці в колонках») — не Q-17: значень
    /// власників немає, рядок видаляється лише в перегляді, читачів в інших періодах немає. Мутація M3: у
    /// <c>CrossPeriodFanOut.RoutedInputsAsync</c> прибрати фільтр <c>AppliesToTable</c> — періоди 1/9/11 перераховуються
    /// (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    public async Task B124_видалення_рядка_немаршрутизованої_таблиці_не_будить_читачів_інших_періодів()
    {
        var world = await ArrangeAsync(sum: true, layout: TableLayoutKind.PerPeriodInstance);

        await using (var db = world.Builder.CreateContext())
        {
            var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
            Assert.True(await rows.SoftDeleteRowAsync(world.Doc.RowIds[1], new PeriodKey(Period(ViewMonth)), CancellationToken.None));
        }

        var seeds = world.Doc.ColumnDefIds.Concat(world.Doc.MonthColumnDefIds)
            .Select(column => new DirtyCell(world.Doc.RowIds[1], column))
            .ToList();
        var enqueued = new List<(string Target, string Payload)>();
        var progress = await RunJobAsync(
            world, enqueued, new FormulaRecalculationRequest(world.Doc.TableInstanceId, Period(ViewMonth), seeds), routing: true);

        Assert.Empty(enqueued);
        Assert.Equal([Period(ViewMonth)], progress.Periods);
        foreach (var month in new[] { 1, 9, OwnerMonth })
        {
            Assert.Equal(StaleOutput, await OutputAsync(world, Period(month)));
        }
    }

    /// <summary>
    /// B124 (межа правила): насіння видаленого рядка в ОБЧИСЛЮВАНІЙ колонці маршрутизованої таблиці — не введене
    /// значення власника (живе в екземплярі перегляду, D-457 п.2), читачів Q-17 в інших періодах не будить. Мутація M3:
    /// у <c>CrossPeriodFanOut.RoutedInputsAsync</c> прибрати фільтр <c>IsComputed</c> — періоди 1/9/11 перераховуються
    /// (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    public async Task B124_видалений_рядок_в_обчислюваній_колонці_не_будить_читачів_Q17()
    {
        var world = await ArrangeAsync(sum: true, m11Computed: true);

        await using (var db = world.Builder.CreateContext())
        {
            var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
            Assert.True(await rows.SoftDeleteRowAsync(world.Doc.RowIds[1], new PeriodKey(Period(ViewMonth)), CancellationToken.None));
            foreach (var (month, instance) in world.Others)
            {
                Assert.True(await rows.SoftDeleteRowAsync(instance.RowIds[1], new PeriodKey(Period(month)), CancellationToken.None));
            }
        }

        // Лише обчислювана M11 (її читає формула OUT): введених колонок у насінні немає.
        var enqueued = new List<(string Target, string Payload)>();
        var progress = await RunJobAsync(
            world, enqueued,
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, Period(ViewMonth),
                [new DirtyCell(world.Doc.RowIds[1], world.Doc.MonthColumnDefIds[OwnerMonth - 1])]),
            routing: true);

        Assert.Empty(enqueued);
        Assert.DoesNotContain(Period(1), progress.Periods);
        foreach (var month in new[] { 1, 9, OwnerMonth })
        {
            Assert.Equal(StaleOutput, await OutputAsync(world, Period(month)));
        }
    }

    /// <summary>
    /// B124 (F13 M2): таблиця БЕЗ дефолту, з ПОРОЖНІМ (пробіли) і з НЕПРИДАТНИМ (текст у числовій колонці, як у
    /// <c>ColumnDefaultValues</c>) — створення рядка через справжній <c>CreateRowHandler</c> задачі формул не ставить
    /// (порожній рядок агрегатів не змінює). Мутація: прибрати <c>IsNullOrWhiteSpace</c>/<c>Parse</c> у
    /// <c>NewRowDefaultColumns</c> — постановка є (червоне).
    /// </summary>
    /// <param name="m11Default">Дефолт колонки M11.</param>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("abc")]
    public async Task B124_створення_рядка_без_придатного_дефолту_задачі_не_ставить(string? m11Default)
    {
        var world = await ArrangeAsync(sum: true, m11Default: m11Default);
        var (jobs, payload) = CapturingJobs();

        await CreateRowAsync(world, jobs, [Period(1), Period(9), Period(ViewMonth), Period(OwnerMonth)]);

        Assert.Null(payload());
        Assert.DoesNotContain(jobs.ReceivedCalls(), c => c.GetMethodInfo().Name.StartsWith("Enqueue", StringComparison.Ordinal));
    }

    /// <summary>
    /// B124 (F13 M3): звичайне (не Q-17) створення рядка в таблиці з дефолтом — агрегат перегляду перераховується
    /// (7 + 3 → 7 + 3 + 3). Мутація: насіння дефолтів лише в дзеркальному шляху — задачі немає (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-491")]
    [Trait("Finding", "B124")]
    public async Task B124_звичайне_створення_рядка_з_дефолтом_перераховує_агрегат()
    {
        var world = await ArrangeAsync(sum: true, m11Default: "3", layout: TableLayoutKind.PerPeriodInstance);
        await SetCellAsync(world, Period(ViewMonth), world.Doc.MonthColumnDefIds[OwnerMonth - 1], 7m);
        await RunJobAsync(
            world, [],
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, Period(ViewMonth),
                [new DirtyCell(world.Doc.RowIds[0], world.Doc.MonthColumnDefIds[OwnerMonth - 1])]),
            routing: true);
        Assert.Equal(10m, await OutputAsync(world, Period(ViewMonth)));

        var (jobs, payload) = CapturingJobs();
        await CreateRowAsync(world, jobs, [Period(ViewMonth)]);
        Assert.NotNull(payload());
        var own = await RowIdAsync(Period(ViewMonth), world.Doc.TableInstanceId, "B124NEW");
        Assert.All(PayloadRowIds(payload()), id => Assert.Equal(own, id));

        await RunJobWithStoreAsync(world, [], payload(), routing: true);
        Assert.Equal(13m, await OutputAsync(world, Period(ViewMonth)));
    }

    /// <summary>
    /// B124 (F13, крайній випадок): екземпляр запиту ВЖЕ мав ключ (розбіжність складу), рядок створено лише в інших
    /// місяцях — насіння бере наявний рядок екземпляра запиту, і агрегати всіх місяців перераховуються. Мутація:
    /// повертати 0 для пропущеного екземпляра — задачі немає (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B124")]
    public async Task B124_дзеркальне_створення_коли_екземпляр_запиту_вже_мав_ключ_сіє_його_рядок()
    {
        var world = await ArrangeAsync(sum: true, m11Default: "3");
        await SetOwnerValueAsync(world, 7m);
        await RunJobAsync(world, [], OwnerRequest(world), routing: true);

        var existing = await AddRowAsync(Period(ViewMonth), world.Doc.TableInstanceId, "B124NEW");

        var (jobs, payload) = CapturingJobs();
        await CreateRowAsync(world, jobs, [Period(1), Period(9), Period(ViewMonth), Period(OwnerMonth)]);
        Assert.NotNull(payload());
        Assert.All(PayloadRowIds(payload()), id => Assert.Equal(existing, id));

        await RunJobWithStoreAsync(world, [], payload(), routing: true);
        foreach (var month in new[] { 1, 9, ViewMonth, OwnerMonth })
        {
            Assert.Equal(13m, await OutputAsync(world, Period(month)));
        }
    }

    /// <summary>
    /// ⛔ B131 (D-491), наскрізно: PATCH створює рядок і пише лише текст C1; колонка M11 з дефолтом 3 не записана, але
    /// агрегат <c>SUM(M11)</c> мусить урахувати дефолт нового рядка (7 + 3 → 7 + 3 + 3). Справжній
    /// <c>PatchCellsHandler</c>, payload його задачі — у справжню <c>FormulaRecalculationJob</c>. Мутація: у
    /// <c>BuildRecalculationSeeds</c> прибрати насіння дефолтів нових рядків — насіння лише C1, агрегат лишається 10
    /// (червоне).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-491")]
    [Trait("Finding", "B131")]
    public async Task B131_PATCH_новий_рядок_без_запису_колонки_з_дефолтом_перераховує_агрегат()
    {
        var world = await ArrangeAsync(sum: true, m11Default: "3", layout: TableLayoutKind.PerPeriodInstance);
        var m11 = world.Doc.MonthColumnDefIds[OwnerMonth - 1];
        await SetCellAsync(world, Period(ViewMonth), m11, 7m);
        await RunJobAsync(
            world, [],
            new FormulaRecalculationRequest(world.Doc.TableInstanceId, Period(ViewMonth), [new DirtyCell(world.Doc.RowIds[0], m11)]),
            routing: true);
        Assert.Equal(10m, await OutputAsync(world, Period(ViewMonth)));

        var (jobs, payload) = CapturingJobs();
        await PatchNewRowAsync(world, jobs, "B131NEW", routing: false, new PatchCell("C1", "x"));
        Assert.NotNull(payload());

        await RunJobWithStoreAsync(world, [], payload(), routing: true);
        Assert.Equal(13m, await OutputAsync(world, Period(ViewMonth)));
    }

    /// <summary>PATCH нового рядка в екземпляр перегляду через справжній <c>PatchCellsHandler</c> (права — дозвіл).</summary>
    private static async Task PatchNewRowAsync(
        World world, IBackgroundJobScheduler jobs, string rowKey, bool routing, params PatchCell[] cells)
    {
        const int userId = 1;
        var snapshot = await world.Metadata.GetAsync(world.Doc.TemplateVersionId, CancellationToken.None);
        var profile = new AccessBuilder { UserId = userId }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Build();
        var allColumns = snapshot.ColumnsById.Keys.ToList();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), world.Doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), world.Doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(_ => DocumentReadScope.For(profile, world.Doc.ProjectId, snapshot));
        access.CanEditCellsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                .ToDictionary(a => a, _ => EditDecision.Allow()));
        access.CanCreateRowsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                .ToDictionary(
                    k => k,
                    _ => new NewRowAccess(EditDecision.Allow(), allColumns.ToDictionary(c => c, _ => EditDecision.Allow())),
                    StringComparer.Ordinal));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);

        await using var db = world.Builder.CreateContext();
        var clock = new TestClock(Now);
        // B132: з роутером Q-17 — комірки чужих власників ідуть в їхні екземпляри (тіньові рядки).
        var router = new MonthColumnRouter(db, Config(routing));
        var handler = new PatchCellsHandler(
            routing ? new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db) : new NormalizedCellStore(db),
            new RowStore(db, new BulkCellLoader(db.Database.GetConnectionString()!, 1000), clock),
            new DocumentStore(db),
            new PeriodStore(db),
            world.Metadata,
            access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db),
            new RegistryStore(db),
            new DocumentHeaderStore(db),
            new AuditWriter(db),
            new AuditReader(db),
            jobs,
            new UnitOfWork(db, clock),
            user,
            clock,
            new SheetEditGate(db),
            new UnitCatalog(db),
            monthRouter: routing ? router : null);

        await handler.HandleAsync(
            new PatchCellsRequest(
                world.Doc.TableInstanceId, Period(ViewMonth), "UserEdit", [new PatchRow(rowKey, null, cells)]),
            CancellationToken.None);
    }

    /// <summary>
    /// ⛔ B132 (B131, наскрізно): PATCH з перегляду жовтня створює рядок і пише M11 (власник — 202611); у листопаді рядка
    /// немає — сховище створює ТІНЬОВИЙ рядок власника. Агрегат листопада <c>SUM(M9)</c> читає M9 маршрутизовано (власник —
    /// 202609, там рядка немає), тож для тіньового рядка діє дефолт 3: 3 + 3 → 3 + 3 + 3. Справжній
    /// <c>PatchCellsHandler</c> з роутером, payload задачі ЕКЗЕМПЛЯРА ВЛАСНИКА — у справжню <c>FormulaRecalculationJob</c>.
    /// Мутація: у <c>RouteForeignOwnersAsync</c> не сіяти дефолти тіньових рядків (<c>shadowSeeds</c>) — насіння лише
    /// (тіньовий, M11), формула M9 не ціль, листопад лишається 6 (червоне). Друга мутація — звузити насіння тіньового
    /// рядка до колонок, власник яких — листопад (лише M11): так само червоне; тому звуження за власником не зроблено.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B132")]
    public async Task B132_тіньовий_рядок_власника_з_дефолтом_перераховує_агрегат_періоду_власника()
    {
        var world = await ArrangeAsync(sum: true, m11Default: "3", sumMonth: 9);

        await using (var db = world.Builder.CreateContext())
        {
            var router = new MonthColumnRouter(db, Config(true));
            var service = Formulas(db, world, new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db), router);
            await service.RecalculateAllAsync(world.Doc.DocumentId, new PeriodKey(Period(OwnerMonth)), CancellationToken.None);
        }

        // Два рядки без M9 — дефолт у кожному.
        Assert.Equal([6m], await OutputsAsync(world, Period(OwnerMonth)));

        var payloads = new List<object?>();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                payloads.Add(call.ArgAt<object?>(1));
                return "job";
            });

        await PatchNewRowAsync(
            world, jobs, "B132NEW", routing: true, new PatchCell($"M{OwnerMonth}", 1m));

        var owner = Assert.Single(
            payloads,
            p => (long)p!.GetType().GetProperty("TableInstanceId")!.GetValue(p)! == world.Others[OwnerMonth].TableInstanceId);
        await RunJobWithStoreAsync(world, [], owner, routing: true);

        // Тіньовий рядок увійшов у склад листопада: усі три живі рядки — 9.
        Assert.Equal([9m], await OutputsAsync(world, Period(OwnerMonth)));
    }

    /// <summary>Різні значення OUT живих рядків документа за період.</summary>
    private async Task<List<decimal?>> OutputsAsync(World world, int period)
    {
        var values = new List<decimal?>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT cv.ValueNumeric FROM doc.TableRow r LEFT JOIN doc.CellValue cv " +
            $"ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.ColumnDefId = {world.Doc.ColumnDefIds[2]} " +
            $"WHERE r.PeriodKey = {period} AND r.IsDeleted = 0 " +
            $"AND r.TableInstanceId IN (SELECT Id FROM doc.TableInstance WHERE DocumentId = {world.Doc.DocumentId})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetDecimal(0));
        }

        return values;
    }

    // ── допоміжне ────────────────────────────────────────────────────────

    /// <summary>Черга, що запам'ятовує payload останньої постановки задачі формул.</summary>
    private static (IBackgroundJobScheduler Jobs, Func<object?> Payload) CapturingJobs()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);
        object? payload = null;
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                payload = call.ArgAt<object?>(1);
                return "job";
            });
        return (jobs, () => payload);
    }

    /// <summary>Id рядків насіння payload постановки (<c>Cells[].RowId</c>).</summary>
    private static List<long> PayloadRowIds(object? payload)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var ids = json.RootElement.GetProperty("Cells").EnumerateArray()
            .Select(c => c.GetProperty("RowId").GetInt64())
            .ToList();
        Assert.NotEmpty(ids);
        return ids;
    }

    /// <summary>Живий рядок екземпляра за ключем.</summary>
    private async Task<long> RowIdAsync(int period, long instanceId, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT Id FROM doc.TableRow WHERE PeriodKey = {period} AND TableInstanceId = {instanceId} " +
            $"AND RowKey = N'{rowKey}' AND IsDeleted = 0";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Рядок лише в одному екземплярі (розбіжність складу).</summary>
    private async Task<long> AddRowAsync(int period, long instanceId, string rowKey)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var id = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        db.TableRows.Add(new Ecr.Domain.Entities.Documents.TableRow(
            new PeriodKey(period), id, instanceId, RowKey.Create(rowKey), 98, Now));
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>POST row у екземпляр перегляду через справжній <c>CreateRowHandler</c> з увімкненим прапором Q-17.</summary>
    private static async Task CreateRowAsync(World world, IBackgroundJobScheduler jobs, IReadOnlyList<int> annualPeriods)
    {
        const int userId = 1;
        var snapshot = await world.Metadata.GetAsync(world.Doc.TemplateVersionId, CancellationToken.None);
        var profile = new AccessBuilder { UserId = userId }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), world.Doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), world.Doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(_ => DocumentReadScope.For(profile, world.Doc.ProjectId, snapshot));
        access.CanCreateRowsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                .ToDictionary(
                    k => k,
                    _ => new NewRowAccess(
                        EditDecision.Allow() with { AnnualPeriods = annualPeriods }, new Dictionary<int, EditDecision>()),
                    StringComparer.Ordinal));

        await using var db = world.Builder.CreateContext();
        var clock = new TestClock(Now);
        var handler = new CreateRowHandler(
            new RowStore(db, new BulkCellLoader(db.Database.GetConnectionString()!, 1000), clock), new DocumentStore(db),
            world.Metadata, access, new UnitOfWork(db), clock, new SheetEditGate(db),
            monthRouter: new MonthColumnRouter(db, Config(true)), jobs: jobs);
        await handler.CreateAsync(
            world.Doc.DocumentId, world.Doc.TableInstanceId, RowKey.Create("B124NEW"), profile, CancellationToken.None);
    }

    /// <summary>Новий рядок з ключем <paramref name="rowKey"/> у кожному екземплярі документа (дзеркало CreateRow).</summary>
    private async Task AddMirrorRowAsync(World world, string rowKey)
    {
        var instances = new List<(int Period, long Instance)> { (Period(ViewMonth), world.Doc.TableInstanceId) };
        instances.AddRange(world.Others.Select(o => (Period(o.Key), o.Value.TableInstanceId)));

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var firstId = await loader.ReserveIdsAsync("doc.TableRowSeq", instances.Count, CancellationToken.None);
        await using var db = world.Builder.CreateContext();
        for (var i = 0; i < instances.Count; i++)
        {
            db.TableRows.Add(new Ecr.Domain.Entities.Documents.TableRow(
                new PeriodKey(instances[i].Period), firstId + i, instances[i].Instance, RowKey.Create(rowKey), 99, Now));
        }

        await db.SaveChangesAsync();
    }

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

    private async Task<World> ArrangeAsync(
        bool absolute = false, bool offsets = false, bool fullYear = false, bool sum = false, string? m11Default = null,
        TableLayoutKind layout = TableLayoutKind.MonthsInColumns, bool m11Computed = false, int sumMonth = OwnerMonth)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(ViewMonth), columnCount: 3, rowCount: sum ? 2 : 1,
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

        var world = new World(
            builder, doc, others, Metadata(doc, absolute, offsets, sum, m11Default, layout, m11Computed, sumMonth), absolute,
            offsets, sumMonth);

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
        World world, List<(string Target, string Payload)> enqueued, object? request, bool routing)
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
        // B132: колонка формули — M{SumMonth} (за замовчуванням M11).
        var m11 = world.Doc.MonthColumnDefIds[world.SumMonth - 1];
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

    private static IMetadataCache Metadata(
        TestDocument doc, bool absolute, bool offsets, bool sum = false, string? m11Default = null,
        TableLayoutKind layout = TableLayoutKind.MonthsInColumns, bool m11Computed = false, int sumMonth = OwnerMonth)
    {
        var text = ColumnDefFor(doc, doc.ColumnDefIds[0], "C1", 1, CellDataType.String);
        var input = ColumnDefFor(doc, doc.ColumnDefIds[1], "IN", 2, CellDataType.Decimal);
        var output = ColumnDefFor(doc, doc.ColumnDefIds[2], "OUT", 3, CellDataType.Formula);

        var months = new List<ColumnDef>();
        for (var m = 1; m <= 12; m++)
        {
            var column = ColumnDefFor(
                doc, doc.MonthColumnDefIds[m - 1], $"M{m}", 3 + m,
                m == OwnerMonth && m11Computed ? CellDataType.Calculated : CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            // B132: дефолт — на колонці формули M{sumMonth} (за замовчуванням M11).
            if (m == sumMonth && m11Default is not null)
            {
                column.SetPresentation(m11Default, displayFormat: null, styleId: null);
            }
            months.Add(column);
        }

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            layout, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);
        foreach (var month in months)
        {
            table.AddColumn(month);
        }

        // B124: агрегат по колонці (усі живі рядки) — той самий граф: комірка зі зсувом null на M11.
        var expression = sum
            ? $"SUM([TB{doc.TableDefId}].[WHERE [M{sumMonth}] >= 0].[M{sumMonth}])"
            : absolute
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
        bool Offsets,
        int SumMonth = OwnerMonth);

}
