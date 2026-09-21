// tests/Ecr.Infrastructure.Tests/Persistence/OrphanScannerCoverageTests.cs
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Нічний прохід <c>OrphanScanner</c> зобов'язаний УРЕШТІ оглянути КОЖЕН
/// рядок-кандидат, а не одне й те саме довільне вікно щоночі (ФВ-7.7,
/// ФВ-8.13a).
/// </summary>
/// <remarks>
/// ⛔ ДЕФЕКТ, який стережуть ці тести. Вибірка кандидатів була
/// <c>…ToListAsync()</c> з <c>Take(20_000)</c> і БЕЗ <c>OrderBy</c>, без
/// курсора й без циклу — рівно один запит на ніч. Тобто прохід брав ДОВІЛЬНІ
/// 20 000 рядків (порядок не заданий узагалі, його обирає оптимізатор) і на
/// цьому вважав ніч відпрацьованою. Таблиця розрахована на ~108 млн рядків на
/// рік; усе, що не потрапило у вікно, не перевірялося НІКОЛИ, а задача щоразу
/// звітувала про успіх. Перевірка, яка не може провалитися, бо майже не
/// дивиться, — це не перевірка.
///
/// ⚠ Обидва тести оперують ВЛАСНИМИ рядками (свій період, свій тег), бо база
/// тестової збірки спільна: інші тести теж лишають у ній відкриті періоди й
/// рядки-кандидати. Тому жодне твердження нижче не залежить від ЗАГАЛЬНОЇ
/// кількості кандидатів у базі — лише від власних рядків і від власного
/// курсора.
/// </remarks>
[Collection("SqlServer")]
public sealed class OrphanScannerCoverageTests(SqlServerFixture sql) : IAsyncLifetime
{
    /// <summary>Засіяні набори цього тесту — їх прибирає <see cref="DisposeAsync"/>.</summary>
    private readonly List<SeededSet> _seeded = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Прибирає засіяні рядки: база спільна на всю колекцію.</summary>
    /// <remarks>
    /// ⛔ Лишені 3 × 21 000 комірок ламали ЧУЖИЙ тест. `arc.usp_ArchiveYear`
    /// фільтрує `PeriodKey = @k` за локальною змінною, тобто оцінює рядки за
    /// щільністю всієї `doc.CellValue`: із цим сміттям ~13 000 замість 1, і
    /// `INSERT … arc.CellValue WITH (TABLOCK)` отримував план DOP 12. Під
    /// завантаженим CPU такий план стояв на `CXCONSUMER` понад 30 с при ~1 с CPU
    /// — `ArchiveJobTests` падали на таймауті, а поодинці були зелені.
    /// </remarks>
    public async Task DisposeAsync()
    {
        // Спершу звільнити журнал від засіву й прогону сканера — інакше
        // видалення лягає поверх них і журнал росте.
        await ExecuteAsync("CHECKPOINT;", _ => { });

        foreach (var set in _seeded)
        {
            foreach (var table in new[] { "doc.CellValue", "doc.TableRow" })
            {
                var idColumn = table == "doc.CellValue" ? "TableRowId" : "Id";
                // Порціями і з CHECKPOINT: база без резервної копії звільняє
                // журнал лише на контрольній точці, і 42 000 видалень поспіль
                // роздували його понад стелю `TestDatabaseSizeTests` (136 МБ > 128).
                await ExecuteAsync(
                    $"""
                    WHILE 1 = 1
                    BEGIN
                        DELETE TOP (2000) FROM {table}
                         WHERE PeriodKey = @pk AND {idColumn} >= @first AND {idColumn} < @first + @count;
                        IF @@ROWCOUNT = 0 BREAK;
                        CHECKPOINT;
                    END
                    CHECKPOINT;
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue("@pk", set.PeriodKey);
                        command.Parameters.AddWithValue("@first", set.FirstRowId);
                        command.Parameters.AddWithValue("@count", SeededRows);
                    });
            }
        }
    }

    /// <summary>
    /// Скільки рядків-кандидатів засівається понад стелю однієї вибірки.
    /// </summary>
    /// <remarks>
    /// ⚠ Число більше за <c>20 000</c> — стару стелю одного запиту — НАВМИСНО і
    /// рівно з тієї причини, що менший набір дефекту не показує: на 19 999
    /// рядках зламаний код віддає ті самі відповіді, що й справний. Запас у
    /// 1 000 рядків — це те, що зламаний прохід НЕ ПОБАЧИТЬ ЖОДНОЇ ночі.
    /// </remarks>
    private const int SeededRows = 21_000;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    public async Task Нічний_прохід_урешті_охоплює_всі_рядки_а_не_довільне_вікно()
    {
        var seed = await SeedAsync(periodKey: 202603, orphaned: false);

        // ⚠ Три прогони, а не один, і це не «доки не позеленіє». Курсор —
        // СПІЛЬНИЙ стан бази: його могли зсунути сусідні тести тієї самої
        // збірки, тож перший прогін цілком законно починається з середини й
        // доходить лише до кінця набору. Другий іде від початку (курсор
        // обернувся) і покриває все. Третій — запас.
        //
        // ⛔ На ЗЛАМАНОМУ коді кількість прогонів не має значення взагалі:
        // курсора немає, впорядкування немає, і кожен прогін бере ТЕ САМЕ
        // довільне вікно. Саме тому цикл тут не робить тест «поблажливим» —
        // він не може врятувати код, який не рухається.
        for (var night = 0; night < 3; night++)
        {
            await using var scan = Context();
            var scanner = new OrphanScanner(scan, new RegistryResolver(), Clock);
            await scanner.ScanAllAsync(CancellationToken.None);
        }

        var flagged = await CountFlaggedAsync(seed.PeriodKey, seed.FirstRowId, isOrphaned: true);

        // Жоден із засіяних рядків не має права лишитися неоглянутим: усі
        // 21 000 посилаються на запис, нечинний у своєму періоді.
        Assert.Equal(SeededRows, flagged);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Підсумок_прогону_відрізняє_оглянуте_від_зміненого()
    {
        // ⛔ Доти прохід повертав саме́ лише число змінених рядків, і три різні
        // стани давали однаковий нуль: «оглянув усе, міняти не було чого»,
        // «оглянув шматок і вичерпав бюджет», «не оглянув нічого, бо не
        // запустився». Перший — здорова система, третій — сканер, що стоїть;
        // у журналі вони були нерозрізненні.
        var seed = await SeedAsync(periodKey: 202605, orphaned: false);

        await using var scan = Context();
        var scanner = new OrphanScanner(scan, new RegistryResolver(), Clock);

        var summary = await scanner.ScanAllAsync(CancellationToken.None);

        // ⚠ Головне твердження: прохід звітує про ОГЛЯНУТЕ, а не лише про
        // змінене. Без цього поля «нуль» не має значення.
        Assert.True(
            summary.ExaminedRows > 0,
            $"прохід мусить звітувати про оглянуті рядки; отримано {summary.ExaminedRows}");

        // ⚠ І про замикання обходу — саме за ним видно, що сканер ВСТИГАЄ за
        // зростанням таблиці. Бюджет тут за замовчуванням (500 000), а
        // засіяно 21 000, тож одна ніч мусить дійти до кінця набору.
        Assert.True(summary.CycleCompleted, "обхід мусив дійти до кінця набору за один прогін");
        Assert.True(summary.CyclesCompleted > 0, "замкнутий обхід мусить порахуватися");

        // ⚠ І змінене теж рахується — засіяні рядки посилаються на запис,
        // нечинний у своєму періоді, тож ознака мусить з'явитися. Без цього
        // твердження тест був би зеленим і на сканері, який «оглядає», нічого
        // не роблячи.
        Assert.True(summary.Changed > 0, "прохід мусив поставити ознаку хоч одному рядку");
        Assert.True(
            summary.ExaminedRows >= summary.Changed,
            "оглянутих не може бути менше за змінених");

        Assert.Equal(202605, seed.PeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    public async Task Послідовні_прогони_беруть_наступні_рядки_а_не_ті_самі()
    {
        // Рядки народжуються ВЖЕ позначеними і посилаються на ЧИННИЙ запис:
        // сканер має ознаку ЗНЯТИ. Напрямок обрано навмисно — «зняти» видно
        // лише на рядку, який сканер справді ОГЛЯНУВ, тоді як «не поставити»
        // не відрізнити від «не дійшов». Зняття ознаки — це слід проходу.
        var seed = await SeedAsync(periodKey: 202604, orphaned: true);

        // Власний курсор і мала порція. Курсор власний, бо стеля тут менша за
        // засіяний набір — на спільному з іншими тестами курсорі це був би не
        // тест, а гонитва. Порція менша за набір, бо саме в цьому вся суть:
        // довести, що НАСТУПНИЙ прогін бере НАСТУПНІ рядки, можна лише тоді,
        // коли один прогін набір не вичерпує.
        var budget = new OrphanScanBudget(
            CursorCode: $"orphan-scan-test-{_tag}",
            RowBudget: 5_000,
            BatchCells: 5_000,
            TimeBudget: TimeSpan.FromHours(1));

        var remaining = new List<int>();
        var advanced = 0;
        var wrapped = 0;
        var before = (PeriodKey: ScanCursor.StartPeriodKeyValue, RowId: ScanCursor.StartRowId, Cycles: 0);

        // ⚠ Стеля прогонів — не «скільки треба, щоб позеленіло». У базі тестової
        // збірки живуть кандидати ІНШИХ тестів, і вони так само з'їдають порцію,
        // тож точне число прогонів до повного обходу невідоме. Стеля відсікає
        // саме той випадок, який тест і стереже: курсор, що не рухається.
        for (var night = 0; night < 40 && (remaining.Count == 0 || remaining[^1] > 0); night++)
        {
            await using (var scan = Context())
            {
                var scanner = new OrphanScanner(scan, new RegistryResolver(), Clock, budget);
                await scanner.ScanAllAsync(CancellationToken.None);
            }

            var after = await CursorAsync(budget.CursorCode);

            // Після кожного прогону курсор або пішов УПЕРЕД, або замкнув цикл і
            // обернувся на початок. Третього не дано: позиція, що не змінилася,
            // означає прохід, який нічого не оглянув, — рівно той дефект, який
            // тут виправляється.
            if (after.Cycles > before.Cycles)
            {
                wrapped++;
            }
            else
            {
                Assert.True(
                    after.PeriodKey > before.PeriodKey
                    || (after.PeriodKey == before.PeriodKey && after.RowId > before.RowId),
                    $"Курсор не зрушив: було ({before.PeriodKey}, {before.RowId}), "
                    + $"стало ({after.PeriodKey}, {after.RowId}).");

                advanced++;
            }

            before = after;
            remaining.Add(await CountFlaggedAsync(seed.PeriodKey, seed.FirstRowId, isOrphaned: true));
        }

        // 1. Один прогін набору НЕ вичерпує — порція справді обмежена.
        Assert.True(
            remaining[0] > 0,
            $"Перший прогін зняв ознаку з усіх {SeededRows} рядків — порція не обмежена.");

        // 2. Ознаку знімали НЕ за один прогін: щонайменше два різні прогони
        //    зачепили різні частини набору. Саме це відрізняє курсор, який
        //    рухається, від курсора, який щоночі починає з того самого місця.
        var nightsThatWorked = remaining
            .Select((left, i) => i == 0 ? SeededRows - left : remaining[i - 1] - left)
            .Count(cleared => cleared > 0);

        Assert.True(
            nightsThatWorked >= 2,
            $"Набір оглянуто за один прогін ({nightsThatWorked}) — просування не доведено.");

        // 3. Прогрес НЕ відкочується: жоден прогін не повертає ознаку рядкам,
        //    з яких її вже зняли.
        Assert.Equal(remaining.OrderByDescending(x => x), remaining);

        // 4. Набір урешті пройдено ЦІЛКОМ.
        Assert.Equal(0, remaining[^1]);

        // 5. І курсор справді обернувся, а не просто повз: без обернення
        //    сканування один раз дійшло б до кінця й більше не оглянуло б
        //    нічого ніколи.
        Assert.True(wrapped >= 1, "Курсор жодного разу не замкнув цикл.");
        Assert.True(advanced >= 2, $"Курсор рухався вперед лише {advanced} раз(и).");
    }

    /// <summary>Поточна позиція курсора сканування.</summary>
    private async Task<(int PeriodKey, long RowId, int Cycles)> CursorAsync(string scanCode)
    {
        await using var db = Context();

        var cursor = await db.ScanCursors
            .AsNoTracking()
            .SingleAsync(c => c.ScanCode == scanCode, CancellationToken.None);

        return (cursor.PeriodKeyValue, cursor.RowId, cursor.CyclesCompleted);
    }

    /// <summary>Засіває набір рядків-кандидатів і повертає його адресу.</summary>
    /// <param name="periodKey">Ключ періоду (він же ключ партиції).</param>
    /// <param name="orphaned">З якою ознакою народжуються рядки.</param>
    /// <remarks>
    /// ⚠ Рядки і комірки вставляються set-based SQL, а не доменними
    /// конструкторами через EF. Це свідомий виняток із правила
    /// <c>TestDocumentBuilder</c> («будувати доменом, щоб заразом перевірити
    /// мапінг»): мапінг тут перевіряє сам будівник, яким створено ланцюг
    /// шаблон→проєкт→період→документ→таблиця, а 21 000 викликів
    /// <c>SaveChanges</c> перетворили б тест на кількахвилинний.
    /// </remarks>
    private async Task<SeededSet> SeedAsync(int periodKey, bool orphaned)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: periodKey, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();

        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"ORPHCOV_{_tag}_{periodKey}"), Text("Orphan coverage scratch"), isTemporal: true);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        // Запис, НЕчинний у засіяному періоді (вікно відкривається у 2030-му):
        // саме він робить кожен засіяний рядок осиротілим.
        var stale = new RegistryEntry(
            def.Id, EcrCode.Create($"S_{_tag}_{periodKey}"), Text("Stale"), 9, DateTime.UnixEpoch);
        stale.SetValidity(new DateOnly(2030, 1, 1), null);
        db.RegistryEntries.Add(stale);

        // І чинний — для другого напрямку механізму (зняття ознаки).
        var fresh = new RegistryEntry(
            def.Id, EcrCode.Create($"F_{_tag}_{periodKey}"), Text("Fresh"), 9, DateTime.UnixEpoch);
        fresh.SetValidity(new DateOnly(2020, 1, 1), null);
        db.RegistryEntries.Add(fresh);
        await db.SaveChangesAsync(CancellationToken.None);

        // Період має бути Open: інакше `OrphanScanPlan.IsScannable` відсіює
        // кандидатів, і тест перевіряв би ранній вихід, а не покриття.
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == doc.ProjectId, CancellationToken.None);
        period.TransitionTo(PeriodState.Open, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(CancellationToken.None);

        var entryId = orphaned ? fresh.Id : stale.Id;
        var loader = new BulkCellLoader(sql.ConnectionString, 5_000);
        var firstRowId = await loader
            .ReserveIdsAsync("doc.TableRowSeq", SeededRows, CancellationToken.None)
            .ConfigureAwait(false);

        // Реєструється ДО вставки: обрив посередині теж прибирається.
        var set = new SeededSet(periodKey, firstRowId);
        _seeded.Add(set);

        const string tally =
            "SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i " +
            "FROM sys.all_objects a CROSS JOIN sys.all_objects b";

        await ExecuteAsync(
            "INSERT INTO doc.TableRow " +
            "(PeriodKey, Id, TableInstanceId, RowKey, Ordinal, IsDeleted, IsOrphaned, OrphanedAt, ModifiedAt) " +
            "SELECT @pk, @first + n.i, @inst, CONCAT(N'BR', @tag, N'_', n.i), 1000 + n.i, 0, " +
            "@orphaned, CASE WHEN @orphaned = 1 THEN @now END, @now " +
            $"FROM ({tally}) n;",
            command =>
            {
                command.Parameters.AddWithValue("@count", SeededRows);
                command.Parameters.AddWithValue("@pk", periodKey);
                command.Parameters.AddWithValue("@first", firstRowId);
                command.Parameters.AddWithValue("@inst", doc.TableInstanceId);
                command.Parameters.AddWithValue("@tag", _tag);
                command.Parameters.AddWithValue("@orphaned", orphaned);
                command.Parameters.AddWithValue("@now", new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
            });

        await ExecuteAsync(
            "INSERT INTO doc.CellValue " +
            "(PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueRegistryEntryId, IsCalculated, IsEmpty) " +
            "SELECT @pk, @first + n.i, @col, @tbl, @entry, 0, 0 " +
            $"FROM ({tally}) n;",
            command =>
            {
                command.Parameters.AddWithValue("@count", SeededRows);
                command.Parameters.AddWithValue("@pk", periodKey);
                command.Parameters.AddWithValue("@first", firstRowId);
                command.Parameters.AddWithValue("@col", doc.ColumnDefIds[0]);
                command.Parameters.AddWithValue("@tbl", doc.TableDefId);
                command.Parameters.AddWithValue("@entry", (int)entryId);
            });

        return set;
    }

    /// <summary>Скільки із засіяних рядків мають задану ознаку.</summary>
    private async Task<int> CountFlaggedAsync(int periodKey, long firstRowId, bool isOrphaned)
    {
        await using var db = Context();

        return await db.TableRows
            .AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey
                        && r.Id >= firstRowId
                        && r.Id < firstRowId + SeededRows
                        && r.IsOrphaned == isOrphaned)
            .CountAsync(CancellationToken.None);
    }

    private async Task ExecuteAsync(string sqlText, Action<SqlCommand> bind)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.CommandTimeout = 300;
        bind(command);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Адреса засіяного набору: період і перший <c>Id</c>.</summary>
    private sealed record SeededSet(int PeriodKey, long FirstRowId);

    private static IClock Clock
        => new FixedClock(new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc));

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
