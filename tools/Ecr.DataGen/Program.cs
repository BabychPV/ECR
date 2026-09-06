using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.DataGen;

/// <summary>
/// Генератор синтетичного обсягу для гейта Етапу 0.
/// </summary>
/// <remarks>
/// Розподіл знімається **з реального шаблону**, а не рівномірний шум: ~90
/// таблиць на документ, медіана ~30 рядків, хвіст до 471, 7…60 колонок,
/// 12 періодів. Рівномірний розподіл дав би оптимістичні цифри, яких не буде
/// в проді (tz/04 §4.3).
/// </remarks>
internal static class Program
{
    /// <summary>Точка входу.</summary>
    /// <param name="args">
    /// <c>--documents 300 --fill 90 --year 2026 --connection "..."</c>, або
    /// <c>--cells 108000000 …</c>, або <c>--gate --load-seconds 900 …</c>.
    /// </param>
    /// <returns>
    /// <c>0</c> — зроблено; <c>1</c> — аргументи не розібрані; <c>2</c> —
    /// **гейт `BR-07` не пройдено**.
    /// </returns>
    /// <remarks>
    /// ⛔ Код виходу <c>2</c> — це і є вся суть режиму <c>--gate</c>. До нього
    /// <see cref="GateBenchmark"/> не викликався **нізвідки**: клас існував,
    /// був описаний у `05j-skeleton-tools.md`, згаданий у `progress.md` як
    /// «5 із 6 замірів», — і не мав жодної точки входу. Бюджет, який не можна
    /// запустити, не перевіряється ніколи.
    /// </remarks>
    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.WriteLine("""
                Ecr.DataGen — синтетичний обсяг і гейт BR-07 (Етап 0).

                  --documents N     скільки документів (типово 300 — цільовий сценарій гейта)
                  --cells N         зупинитися, коли в doc.CellValue стане N комірок;
                                    має пріоритет над --documents (0 — не обмежувати)
                  --fill N          заповненість комірок у відсотках (35 | 60 | 90)
                  --year N          рік періодів (типово 2026)
                  --connection S    рядок підключення; без нього береться ECR_ConnectionStrings__Ecr
                  --gate            не генерувати, а ЗАМІРЯТИ; код виходу 2, якщо бюджет не пройдено
                  --load-seconds N  тривалість заміру №6 (типово 900 — 15 хв повного гейта)
                  --load-slice S    у який зріз б'є замір №6: typical (типово, ~5 000 комірок
                                    за tz/08 §8.2) або worst (500×60, критерій №1)

                ⚠ Цільовий сценарій гейта — 300 документів при заповненості 90%:
                  замовник називає ≥200 на рік, і саме на 300 мають виконуватися бюджети.
                ⚠ Обсяг задається В КОМІРКАХ (--cells), бо BR-07 названий у рядках
                  doc.CellValue, а не в документах: скільки документів дасть 108 млн
                  комірок, залежить від заповненості.
                """);
            return 1;
        }

        return options.Gate
            ? await RunGateAsync(options).ConfigureAwait(false)
            : await GenerateAsync(options).ConfigureAwait(false);
    }

    /// <summary>Заміри гейта і код виходу за їхнім результатом.</summary>
    /// <param name="options">Розібрані аргументи командного рядка.</param>
    /// <returns><c>0</c> — бюджет витриманий, <c>2</c> — ні.</returns>
    private static async Task<int> RunGateAsync(Options options)
    {
        var benchmark = new GateBenchmark
        {
            LoadSeconds = options.LoadSeconds,
            LoadWorstSlice = options.LoadWorstSlice,
        };
        var result = await benchmark
            .RunAsync(options.ConnectionString, CancellationToken.None)
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("Гейт BR-07 — заміри:");
        foreach (var (name, value) in result.Measurements)
        {
            Console.WriteLine(Fmt($"  {name,-28} {value,12:F1}"));
        }

        if (result.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Порушення бюджету:");
            foreach (var failure in result.Failures)
            {
                Console.WriteLine(Fmt($"  ✗ {failure}"));
            }
        }

        foreach (var note in result.Notes)
        {
            Console.WriteLine(Fmt($"  ! {note}"));
        }

        Console.WriteLine();
        Console.WriteLine(result.Passed ? "Гейт BR-07 пройдено." : "Гейт BR-07 НЕ пройдено.");

        // ⛔ Ненульовий код виходу — єдине, що відрізняє перевірку від звіту.
        // Скрипт, який друкує числа і завжди виходить нулем, конвеєр пропустить.
        return result.Passed ? 0 : 2;
    }

    /// <summary>Наповнення <c>doc.CellValue</c> до заданого обсягу.</summary>
    /// <param name="options">Розібрані аргументи командного рядка.</param>
    /// <returns>Завжди <c>0</c>: генерація або відпрацювала, або кинула виняток.</returns>
    private static async Task<int> GenerateAsync(Options options)
    {
        var profile = new DistributionProfile();
        var db = CreateContext(options.ConnectionString);
        var loader = new BulkCellLoader(options.ConnectionString, batchSize: 10_000);

        var goal = options.TargetCells > 0
            ? Fmt($"до {options.TargetCells} комірок")
            : Fmt($"{options.Documents} документів");

        Console.WriteLine(Fmt(
            $"Генерація: {goal}, заповненість {options.Fill}%, рік {options.Year}."));
        var started = DateTime.UtcNow;

        var scaffold = await BuildScaffoldAsync(db, profile, options).ConfigureAwait(false);
        Console.WriteLine(Fmt(
            $"Каркас: шаблон {scaffold.TemplateVersionId}, таблиць {scaffold.Tables.Count}, проєкт {scaffold.ProjectId}."));

        long rows = 0, cells = 0;
        var random = new Random(Seed: 20260904);

        // ⚠ Межа за комірками, а не лише за документами: BR-07 названий у
        // рядках `doc.CellValue`. Обидві межі діють одночасно — генерація
        // спиняється на тій, що настане раніше, інакше `--cells` на малому
        // `--documents` мовчки недобрав би обсяг.
        for (var docIndex = 1; docIndex <= options.Documents; docIndex++)
        {
            var (r, c) = await GenerateDocumentAsync(
                db, loader, scaffold, profile, options, docIndex, random).ConfigureAwait(false);
            rows += r;
            cells += c;

            var reached = options.TargetCells > 0 && cells >= options.TargetCells;

            if (docIndex % 10 == 0 || docIndex == options.Documents || reached)
            {
                var elapsed = DateTime.UtcNow - started;
                Console.WriteLine(Fmt(
                    $"  {docIndex}/{options.Documents}: рядків {rows}, комірок {cells}, {elapsed:hh\\:mm\\:ss}"));
            }

            if (reached)
            {
                break;
            }
        }

        await PrintSizeAsync(db, rows, cells).ConfigureAwait(false);
        await db.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Шаблон, версія, структура таблиць і проєкт із періодами.</summary>
    /// <remarks>
    /// Каркас будується один раз на прогін: у проді 300 документів теж
    /// поділяють одну версію шаблону, і генерувати 300 різних структур
    /// означало б міряти не ту систему.
    /// </remarks>
    private static async Task<Scaffold> BuildScaffoldAsync(
        EcrDbContext db, DistributionProfile profile, Options options)
    {
        var now = DateTime.UtcNow;
        var tag = now.Ticks.ToString(CultureInfo.InvariantCulture)[^8..];

        var template = new Template(EcrCode.Create($"GEN{tag}"), Name($"DataGen {tag}"), 1, now);
        db.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, now);
        db.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var random = new Random(Seed: 42);
        var tables = new List<TableShape>(profile.TablesPerDocument + 1);

        // ⛔ Остання таблиця — контрольна: рівно 500×60, тобто той самий зріз,
        // під який записаний критерій №1 BR-07. Профіль її не дає ніколи
        // (хвіст — 471 рядок), тому без неї бюджет «< 600 мс на 500×60»
        // перевірявся б на зрізі в тридцять разів меншому.
        for (var t = 1; t <= profile.TablesPerDocument + 1; t++)
        {
            var isGateTable = t == profile.TablesPerDocument + 1;

            var table = new TableDef(
                sheet.Id, EcrCode.Create($"T{t}_{tag}"), Name($"Table {t}"), t,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.Add(table);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var columnCount = isGateTable
                ? profile.GateSliceColumns
                : random.Next(profile.MinColumns, profile.MaxColumns + 1);

            for (var c = 1; c <= columnCount; c++)
            {
                var column = new ColumnDef(
                    table.Id, EcrCode.Create($"C{c}"), Name($"C{c}"), c,
                    c == 1 ? CellDataType.String : CellDataType.Decimal);
                db.Add(column);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);

            var ids = await db.ColumnDefs.AsNoTracking()
                .Where(x => x.TableDefId == table.Id)
                .OrderBy(x => x.Ordinal)
                .Select(x => x.Id)
                .ToListAsync().ConfigureAwait(false);

            var rowCount = isGateTable ? profile.GateSliceRows : RowCount(profile, random);
            tables.Add(new TableShape(table.Id, ids, rowCount));
        }

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(false);

        var project = new Project(
            EcrCode.Create($"P{tag}"), Name($"DataGen {tag}"),
            new DateOnly(options.Year, 1, 1), new DateOnly(options.Year, 12, 31),
            version.Id, PeriodKind.Monthly, policyId, "Central Asia Standard Time");
        db.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⛔ Межі періодів ОБЧИСЛЮЮТЬСЯ, а проєкт АКТИВУЄТЬСЯ. Без цього
        // згенерований обсяг описує стан, у якому система не працює: період без
        // меж калькулятор читає як «усе вже минуло» і оголошує закритим, а
        // задача станів до чернетки взагалі не доходить (`A7-24`…`A7-26`).
        //
        // ⚠ Саме через це генератор і був небезпечним: він давав дані, на яких
        // гейт вимірював систему, якої не буває.
        var policy = await db.PeriodPolicies
            .FirstAsync(p => p.Id == policyId)
            .ConfigureAwait(false);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);

        for (var month = 1; month <= profile.PeriodsPerYear; month++)
        {
            var key = PeriodKey.Create(options.Year, month);
            var period = new Period(
                project.Id, key, (byte)month,
                new DateOnly(options.Year, month, 1),
                new DateOnly(options.Year, month, DateTime.DaysInMonth(options.Year, month)));

            period.RecomputeBoundaries(policy, zone);
            db.Add(period);
        }

        project.Activate(now);

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Scaffold(version.Id, project.Id, sheet.Id, tables);
    }

    /// <summary>
    /// Кількість рядків у таблиці: медіана з довгим хвостом.
    /// </summary>
    /// <remarks>
    /// ⚠ Не рівномірний розподіл. У чинному шаблоні більшість таблиць
    /// невеликі, а кілька — на сотні рядків, і саме вони визначають найгірший
    /// випадок. Рівномірний шум дав би оптимістичні цифри, яких у проді не буде.
    /// </remarks>
    private static int RowCount(DistributionProfile profile, Random random)
        => random.Next(100) < 90
            ? Math.Max(1, (int)(profile.MedianRowsPerTable * (0.5 + random.NextDouble())))
            : random.Next(profile.MedianRowsPerTable * 2, profile.MaxRowsPerTable + 1);

    private static async Task<(long Rows, long Cells)> GenerateDocumentAsync(
        EcrDbContext db, BulkCellLoader loader, Scaffold scaffold,
        DistributionProfile profile, Options options, int docIndex, Random random)
    {
        var now = DateTime.UtcNow;
        var document = new Document(scaffold.ProjectId, $"DOC-{docIndex:D6}", 1, now);

        // ⛔ Аркуш додається ОДРАЗУ. Документ без аркушів виглядає нормальним —
        // `sheetCount` чесно показує нуль, — але експорт віддає порожню книгу, а
        // подання нема чого подавати. Справжній шлях (`CreateDocumentHandler`)
        // аркуші додає; генератор обходив його і давав дані, на яких половина
        // сценаріїв не відтворюється.
        document.IncludeSheet(scaffold.SheetDefId);

        db.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        long totalRows = 0, totalCells = 0;

        for (var month = 1; month <= profile.PeriodsPerYear; month++)
        {
            var key = PeriodKey.Create(options.Year, month);

            // Id резервуються ОДНИМ викликом на весь період документа:
            // sp_sequence_get_range на батч, а не на рядок (B02 §2.3).
            var rowTotal = scaffold.Tables.Sum(t => t.Rows);
            var instanceFirst = await loader
                .ReserveIdsAsync("doc.TableInstanceSeq", scaffold.Tables.Count, CancellationToken.None)
                .ConfigureAwait(false);
            var rowFirst = await loader
                .ReserveIdsAsync("doc.TableRowSeq", rowTotal, CancellationToken.None)
                .ConfigureAwait(false);

            var instances = new List<TableInstance>(scaffold.Tables.Count);
            var rows = new List<TableRow>(rowTotal);
            var cells = new List<CellRecord>(rowTotal * 20);

            var instanceId = instanceFirst;
            var rowId = rowFirst;

            foreach (var table in scaffold.Tables)
            {
                instances.Add(new TableInstance(key, instanceId, document.Id, table.TableDefId, now));

                for (var r = 0; r < table.Rows; r++)
                {
                    rows.Add(new TableRow(key, rowId, instanceId, RowKey.Create($"R{r + 1}"), r + 1, now));

                    foreach (var columnId in table.ColumnIds)
                    {
                        // ⚠ Незаповнені комірки НЕ створюються взагалі: саме
                        // це й має бути видно в замірі розміру (ФВ-3.8).
                        if (random.Next(100) >= options.Fill)
                        {
                            continue;
                        }

                        cells.Add(new CellRecord(
                            new CellAddress(key, rowId, columnId),
                            table.TableDefId,
                            new CellValueData { ValueNumeric = Math.Round((decimal)random.NextDouble() * 1000, 4) }));
                    }

                    rowId++;
                }

                instanceId++;
            }

            db.AddRange(instances);
            db.AddRange(rows);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();

            await loader.LoadAsync(cells, CancellationToken.None).ConfigureAwait(false);

            totalRows += rows.Count;
            totalCells += cells.Count;
        }

        return (totalRows, totalCells);
    }

    private static async Task PrintSizeAsync(EcrDbContext db, long rows, long cells)
    {
        var mb = await db.Database.SqlQueryRaw<decimal>("""
            SELECT CAST(SUM(a.used_pages) * 8.0 / 1024 AS decimal(18,2)) AS Value
            FROM sys.tables t
            JOIN sys.indexes i     ON i.object_id = t.object_id
            JOIN sys.partitions p  ON p.object_id = i.object_id AND p.index_id = i.index_id
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE t.name = 'CellValue' AND SCHEMA_NAME(t.schema_id) = 'doc'
            """).ToListAsync().ConfigureAwait(false);

        var size = mb.Count > 0 ? mb[0] : 0m;
        Console.WriteLine();
        Console.WriteLine(Fmt($"Готово. Рядків {rows}, комірок {cells}."));
        Console.WriteLine(Fmt($"doc.CellValue після PAGE-стиснення: {size} МБ"));
        if (cells > 0)
        {
            Console.WriteLine(Fmt($"На 108 млн комірок/рік це ≈ {size / cells * 108_000_000 / 1024:F1} ГБ."));
        }
    }

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.CommandTimeout(600))
            .Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static string Fmt(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed record Scaffold(
        int TemplateVersionId, int ProjectId, int SheetDefId, IReadOnlyList<TableShape> Tables);

    private sealed record TableShape(int TableDefId, IReadOnlyList<int> ColumnIds, int Rows);

    /// <summary>Розібрані аргументи командного рядка.</summary>
    /// <param name="Documents">Верхня межа кількості документів.</param>
    /// <param name="TargetCells">Цільова кількість рядків <c>doc.CellValue</c>; <c>0</c> — без межі.</param>
    /// <param name="Fill">Заповненість комірок, %.</param>
    /// <param name="Year">Рік періодів.</param>
    /// <param name="ConnectionString">Рядок підключення до бази.</param>
    /// <param name="Gate">Режим заміру замість генерації.</param>
    /// <param name="LoadSeconds">Тривалість заміру №6, секунд.</param>
    /// <param name="LoadWorstSlice">Бити заміром №6 у найважчий зріз, а не в типовий.</param>
    private sealed record Options(
        int Documents,
        long TargetCells,
        int Fill,
        int Year,
        string ConnectionString,
        bool Gate,
        int LoadSeconds,
        bool LoadWorstSlice)
    {
        /// <summary>Розбирає аргументи; <c>null</c>, якщо немає рядка підключення.</summary>
        /// <param name="args">Аргументи командного рядка.</param>
        /// <returns>Опції або <c>null</c>.</returns>
        public static Options? Parse(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ⚠ Прапорці без значення розбираються ОКРЕМО. Попередній розбір
            // ішов парами `args[i]`/`args[i+1]`, і будь-який одиночний `--gate`
            // з'їв би наступний ключ як своє значення — тихо, без помилки.
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i].TrimStart('-');
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    map[key] = args[i + 1];
                    i++;
                }
                else
                {
                    flags.Add(key);
                }
            }

            var connection = map.GetValueOrDefault("connection")
                ?? Environment.GetEnvironmentVariable("ECR_ConnectionStrings__Ecr");

            if (string.IsNullOrWhiteSpace(connection))
            {
                return null;
            }

            var cells = (long)Read(map, "cells", 0);

            return new Options(
                // ⚠ Коли обсяг заданий комірками, межа за документами має не
                // заважати: інакше типові 300 обірвали б наповнення раніше за
                // ціль і замір пішов би на недоборі, не сказавши про це.
                Documents: Read(map, "documents", cells > 0 ? int.MaxValue : 300),
                TargetCells: cells,
                Fill: Read(map, "fill", 90),
                Year: Read(map, "year", 2026),
                ConnectionString: connection,
                Gate: flags.Contains("gate"),
                LoadSeconds: Read(map, "load-seconds", 900),
                LoadWorstSlice: map.GetValueOrDefault("load-slice") == "worst");
        }

        private static int Read(Dictionary<string, string> map, string key, int fallback)
            => map.TryGetValue(key, out var raw)
               && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
    }
}
