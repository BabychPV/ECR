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
    /// **гейт `BR-07` не пройдено**; <c>3</c> — гейт не виконувався (база
    /// недоступна або її схема застаріла).
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
                  --store-bench     МІКРОБЕНЧМАРК СХОВИЩА (він же --gate): б'є у
                                    NormalizedCellStore напряму, ПОВЗ права, аудит, «дотик»
                                    документа й чергу задач. Його числа — оптимістична стеля
                                    doc.CellValue, а НЕ числа продукту
                  --load-seconds N  тривалість заміру №6 (типово 900 — 15 хв повного гейта)
                  --load-slice S    у який зріз б'є замір №6: typical (типово, ~5 000 комірок
                                    за tz/08 §8.2) або worst (500×60, критерій №1)

                  --http-gate       ГЕЙТ ПРОДУКТУ (MS-01): навантаження на піднятий Ecr.Api
                                    через HTTP — вхід, cookie, права, аудит, черга. Критерії
                                    з tz/08 §8.2 (p95/p99), а не RPS сховища
                  --url S           адреса піднятого застосунку (типово http://localhost:5099)
                  --bootstrap S     разовий пароль bootstrap, відданий застосунку при першому старті
                  --load-rps N      цільовий темп операцій (типово 25)
                  --operators N     скільки операторів б'є в ОДИН документ (типово 3, мінімум 2:
                                    з одним дефект DAT-01 не проявляється взагалі)
                  --workers N       скільки ОДНОЧАСНИХ робітників (типово за темпом). --workers 1
                                    серіалізує все — контрольний прогін, у якому фальшивих 409
                                    не може бути за побудовою
                  --samples-out F   (--http-gate) кожен запит вікна рядком CSV: вид, код, завершення
                                    від старту вікна (с), затримка й обслуговування (мс) — щоб
                                    рахувати p50/p95 у довільному під-вікні (I2 ФВ-9.8)

                  Перевизначення профілю (I2 ФВ-9.8, замір ПРД-13; без них — профіль шаблону):
                  --tables N        таблиць на документ
                  --median-rows N   медіана рядків у таблиці
                  --max-rows N      максимум рядків (хвіст)
                  --no-gate-table   без контрольної таблиці 500×60
                  --formula-columns остання колонка кожної таблиці — формула [C2] + [C3] + [C4]
                  --parallel N      генерувати N документів одночасно (--cells тоді не діє)

                ⚠ Цільовий сценарій гейта — 300 документів при заповненості 90%:
                  замовник називає ≥200 на рік, і саме на 300 мають виконуватися бюджети.
                ⚠ Обсяг задається В КОМІРКАХ (--cells), бо BR-07 названий у рядках
                  doc.CellValue, а не в документах: скільки документів дасть 108 млн
                  комірок, залежить від заповненості.
                ⛔ Два режими заміру НЕ взаємозамінні. --store-bench каже, чи тримає
                  нормалізована модель; --http-gate каже, чи тримає СИСТЕМА. Рішення D-21
                  (гібридне зберігання) ухвалюється лише коли обидва зняті — інакше
                  переробляється модель через вузьке місце, якого в ній немає (MS-03).
                """);
            return 1;
        }

        if (options.HttpGate)
        {
            return await RunHttpGateAsync(options).ConfigureAwait(false);
        }

        return options.Gate
            ? await RunGateAsync(options).ConfigureAwait(false)
            : await GenerateAsync(options).ConfigureAwait(false);
    }

    /// <summary>
    /// Гейт продукту через HTTP (<c>MS-01</c>) і код виходу за його результатом.
    /// </summary>
    /// <param name="options">Розібрані аргументи командного рядка.</param>
    /// <returns><c>0</c> — бюджет §8.2 витриманий, <c>2</c> — ні, <c>3</c> — замір не відбувся.</returns>
    /// <remarks>
    /// ⛔ Окремий режим, а не заміна <see cref="RunGateAsync"/>. Мікробенчмарк
    /// сховища лишається потрібним: він відповідає на питання «чи тримає
    /// <c>doc.CellValue</c>», без якого не можна відрізнити «модель не тягне»
    /// від «конвеєр навколо моделі не тягне». Помилка була не в тому, що його
    /// написали, а в тому, що його числа видавали за числа продукту
    /// (директива №14, частина 3, §2.2 рядок F).
    /// </remarks>
    private static async Task<int> RunHttpGateAsync(Options options)
    {
        var benchmark = new HttpLoadBenchmark
        {
            BaseAddress = new Uri(options.Url, UriKind.Absolute),
            ConnectionString = options.ConnectionString,
            BootstrapPassword = options.BootstrapPassword,
            LoadSeconds = options.LoadSeconds,
            TargetRps = options.LoadRps,
            Operators = options.Operators,
            Workers = options.Workers,
            SamplesOut = options.SamplesOut,
        };

        var result = await benchmark.RunAsync(CancellationToken.None).ConfigureAwait(false);

        return Print(
            result,
            Fmt($"Гейт продукту через HTTP (MS-01), {options.Url}"),
            "Критерії — tz/08 §8.2: зріз 400/800 мс, PATCH 100 комірок 250/500 мс.");
    }

    /// <summary>Мікробенчмарк сховища і код виходу за його результатом.</summary>
    /// <param name="options">Розібрані аргументи командного рядка.</param>
    /// <returns><c>0</c> — бюджет витриманий, <c>2</c> — ні, <c>3</c> — заміри не відбулися.</returns>
    /// <remarks>
    /// ⛔ Назва режиму змінена свідомо. Це НЕ «гейт BR-07 продукту»: замір іде
    /// повз <c>PatchCellsHandler</c>, права, аудит, «дотик» документа й чергу
    /// задач (<c>GateBenchmark.cs:150,796</c>). Його 63 RPS — оптимістична
    /// стеля сховища; справжній <c>PATCH</c> повільніший, і рішення
    /// <c>D-21</c> на цих числах ухвалювати не можна (<c>MS-03</c>).
    /// Прапорець <c>--gate</c> лишено робочим, бо його кличе
    /// <c>tools/br07-load-test.ps1</c>, але друкований заголовок більше не
    /// стверджує, що це числа продукту.
    /// </remarks>
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

        return Print(
            result,
            "Мікробенчмарк сховища doc.CellValue (НЕ гейт продукту)",
            "⛔ Замір іде ПОВЗ PatchCellsHandler, права, аудит, «дотик» документа й чергу задач. "
            + "Це оптимістична стеля моделі, а не число системи; для числа системи — --http-gate.");
    }

    /// <summary>Друкує результат заміру і віддає код виходу.</summary>
    /// <param name="result">Результат будь-якого з двох режимів.</param>
    /// <param name="title">Заголовок — він мусить чесно називати, ЩО виміряно.</param>
    /// <param name="subtitle">Другий рядок: межі або застереження.</param>
    /// <returns><c>0</c> — пройдено, <c>2</c> — порушено, <c>3</c> — замір не відбувся.</returns>
    private static int Print(GateResult result, string title, string subtitle)
    {
        // ⛔ Окремий код виходу, а не 2. Двійка означає «бюджет не
        // витриманий» — тобто що заміри БУЛИ. Тут їх не було, і видати це за
        // порушення бюджету означало б збрехати конвеєру про причину.
        if (result.Blocked is not null)
        {
            Console.WriteLine();
            Console.WriteLine(Fmt($"{title} — НЕ виконувався:"));
            Console.WriteLine(Fmt($"  ⛔ {result.Blocked}"));

            return 3;
        }

        Console.WriteLine();
        Console.WriteLine(Fmt($"{title} — заміри:"));
        Console.WriteLine(Fmt($"  {subtitle}"));
        Console.WriteLine();
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
        Console.WriteLine(result.Passed
            ? Fmt($"{title}: бюджет витриманий.")
            : Fmt($"{title}: бюджет НЕ витриманий."));

        // ⛔ Ненульовий код виходу — єдине, що відрізняє перевірку від звіту.
        // Скрипт, який друкує числа і завжди виходить нулем, конвеєр пропустить.
        return result.Passed ? 0 : 2;
    }

    /// <summary>Наповнення <c>doc.CellValue</c> до заданого обсягу.</summary>
    /// <param name="options">Розібрані аргументи командного рядка.</param>
    /// <returns>Завжди <c>0</c>: генерація або відпрацювала, або кинула виняток.</returns>
    private static async Task<int> GenerateAsync(Options options)
    {
        // ⚠ I2 ФВ-9.8: перевизначення профілю — лише явними ключами. Без них
        // профіль лишається тим, що знятий із чинного шаблону.
        var defaults = new DistributionProfile();
        var profile = defaults with
        {
            TablesPerDocument = options.Tables ?? defaults.TablesPerDocument,
            MedianRowsPerTable = options.MedianRows ?? defaults.MedianRowsPerTable,
            MaxRowsPerTable = options.MaxRows ?? defaults.MaxRowsPerTable,
        };

        if (options.Tables is not null || options.MedianRows is not null || options.MaxRows is not null
            || options.NoGateTable || options.FormulaColumns)
        {
            Console.WriteLine(Fmt(
                $"⚠ Профіль ПЕРЕВИЗНАЧЕНО: таблиць {profile.TablesPerDocument}, медіана рядків {profile.MedianRowsPerTable}, максимум {profile.MaxRowsPerTable}, контрольна 500×60 — {(options.NoGateTable ? "ні" : "так")}, формульна колонка — {(options.FormulaColumns ? "так" : "ні")}."));
        }

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

        // ⚠ I2 ФВ-9.8: `--parallel N` — документи генеруються N потоками (власний
        // контекст, завантажувач і генератор чисел на потік). Один потік дає
        // ~40 тис. комірок/с — 108 млн за ~45 хв; для заміру це недоречно довго.
        // Межа за комірками (`--cells`) у паралельному режимі не діє.
        // ⚠ Виміряно 2026-09-29: виграш МАЛИЙ — `BulkCellLoader` бере TABLOCK на
        // `doc.CellValue`, і потоки стоять у LCK_M_X один за одним; паралелиться
        // лише вставка рядків і каркаса. Більше 2 потоків сенсу не має.
        if (options.Parallel > 1)
        {
            var next = 0;
            var done = 0;
            var gate = new object();
            var workers = Enumerable.Range(0, options.Parallel).Select(w => Task.Run(async () =>
            {
                await using var wdb = CreateContext(options.ConnectionString);
                var wloader = new BulkCellLoader(options.ConnectionString, batchSize: 10_000);
                var wrandom = new Random(Seed: 20260904 + w);
                int docIndex;
                while ((docIndex = Interlocked.Increment(ref next)) <= options.Documents)
                {
                    var (r, c) = await GenerateDocumentAsync(
                        wdb, wloader, scaffold, profile, options, docIndex, wrandom).ConfigureAwait(false);
                    wdb.ChangeTracker.Clear();
                    lock (gate)
                    {
                        rows += r;
                        cells += c;
                        done++;
                        if (done % 10 == 0 || done == options.Documents)
                        {
                            Console.WriteLine(Fmt(
                                $"  {done}/{options.Documents}: рядків {rows}, комірок {cells}, {DateTime.UtcNow - started:hh\\:mm\\:ss}"));
                        }
                    }
                }
            })).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);
            await BuildStatisticsAsync(db).ConfigureAwait(false);
            await PrintSizeAsync(db, rows, cells).ConfigureAwait(false);
            await db.DisposeAsync().ConfigureAwait(false);
            return 0;
        }

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

        await BuildStatisticsAsync(db).ConfigureAwait(false);
        await PrintSizeAsync(db, rows, cells).ConfigureAwait(false);
        await db.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Створює й оновлює статистику після масового завантаження.</summary>
    /// <remarks>
    /// ⛔ Без цього першу статистику на 2 млн рядків <c>doc.CellValue</c> будує
    /// СИНХРОННО перша ж компіляція запиту користувача. Замір на стенді
    /// (2026-09-21): компіляція <c>MERGE doc.CellValue</c> 3634 мс → 210 мс,
    /// перший <c>PATCH …/cells</c> на 4 комірки ніс ці секунди на собі.
    /// </remarks>
    private static async Task BuildStatisticsAsync(EcrDbContext db)
    {
        var started = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            "EXEC sys.sp_createstats; EXEC sys.sp_updatestats;").ConfigureAwait(false);
        Console.WriteLine(Fmt($"Статистика: {(DateTime.UtcNow - started).TotalSeconds:F0} с."));
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
        var tableCount = profile.TablesPerDocument + (options.NoGateTable ? 0 : 1);
        for (var t = 1; t <= tableCount; t++)
        {
            var isGateTable = !options.NoGateTable && t == profile.TablesPerDocument + 1;

            var table = new TableDef(
                sheet.Id, EcrCode.Create($"T{t}_{tag}"), Name($"Table {t}"), t,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.Add(table);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var columnCount = isGateTable
                ? profile.GateSliceColumns
                : random.Next(profile.MinColumns, profile.MaxColumns + 1);

            // ⚠ I2 ФВ-9.8 (`--formula-columns`): ОСТАННЯ колонка — обчислювана
            // (`Formula`), її рахує формула шаблону `[C2] + [C3] + [C4]`. Без
            // формул повний перерахунок нічого не рахує й не пише, і замір
            // ПРД-13 показав би лише накладні черги. Колонка не заповнюється:
            // її значення дає сам перерахунок.
            var formulaColumn = options.FormulaColumns && !isGateTable && columnCount >= 5
                ? columnCount
                : 0;

            ColumnDef? computed = null;
            for (var c = 1; c <= columnCount; c++)
            {
                var type = c == 1 ? CellDataType.String
                    : c == formulaColumn ? CellDataType.Formula
                    : CellDataType.Decimal;
                var column = new ColumnDef(
                    table.Id, EcrCode.Create($"C{c}"), Name($"C{c}"), c, type);
                db.Add(column);
                if (c == formulaColumn)
                {
                    computed = column;
                }
            }

            await db.SaveChangesAsync().ConfigureAwait(false);

            if (computed is not null)
            {
                var formula = new FormulaDef(
                    table.Id, FormulaScope.Column, "[C2] + [C3] + [C4]", ExpressionDialect.Template);
                formula.AssignColumn(computed.Id);
                formula.SetEvaluationOrder(t);
                db.Add(formula);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }

            var ids = await db.ColumnDefs.AsNoTracking()
                .Where(x => x.TableDefId == table.Id && (computed == null || x.Id != computed.Id))
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
            // ⛔ IANA, не Windows. Літерал `Central Asia Standard Time` стояв тут
            // із самого початку і перестав працювати 2026-09-07 (`II.7`, `H-13`):
            // домен відтоді приймає лише IANA. Генератор під зміну не підправили,
            // і стенд `e2e-stand.ps1` падав на кроці 1 — тобто ВСІ прогони в
            // браузері не виконувалися жодного разу від тієї зміни.
            //
            // ⚠ Не помітив цього ніхто, бо помічати не було чим: крок виведений
            // із конвеєра як `ci-exempt`, а `npm run test:e2e` наодинці виходить
            // нулем, мовчки пропустивши 17 прогонів із 20.
            version.Id, PeriodKind.Monthly, policyId, "Asia/Aqtau");
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
    /// <param name="Gate">Мікробенчмарк сховища замість генерації.</param>
    /// <param name="LoadSeconds">Тривалість заміру №6, секунд.</param>
    /// <param name="LoadWorstSlice">Бити заміром №6 у найважчий зріз, а не в типовий.</param>
    /// <param name="HttpGate">Гейт продукту через HTTP (<c>MS-01</c>).</param>
    /// <param name="Url">Адреса піднятого застосунку.</param>
    /// <param name="BootstrapPassword">Разовий пароль bootstrap.</param>
    /// <param name="LoadRps">Цільовий темп операцій.</param>
    /// <param name="Operators">Скільки операторів б'є в один документ.</param>
    /// <param name="Workers">Скільки одночасних робітників; <c>0</c> — за темпом.</param>
    /// <param name="Tables">Перевизначення кількості таблиць на документ (I2).</param>
    /// <param name="MedianRows">Перевизначення медіани рядків (I2).</param>
    /// <param name="MaxRows">Перевизначення максимуму рядків (I2).</param>
    /// <param name="NoGateTable">Без контрольної таблиці 500×60 (I2).</param>
    /// <param name="FormulaColumns">Остання колонка кожної таблиці — формула шаблону (I2).</param>
    /// <param name="SamplesOut">Файл CSV із сирими вибірками навантаження (I2).</param>
    /// <param name="Parallel">Скільки документів генерувати одночасно (I2).</param>
    private sealed record Options(
        int Documents,
        long TargetCells,
        int Fill,
        int Year,
        string ConnectionString,
        bool Gate,
        int LoadSeconds,
        bool LoadWorstSlice,
        bool HttpGate,
        string Url,
        string BootstrapPassword,
        int LoadRps,
        int Operators,
        int Workers,
        int? Tables = null,
        int? MedianRows = null,
        int? MaxRows = null,
        bool NoGateTable = false,
        bool FormulaColumns = false,
        string? SamplesOut = null,
        int Parallel = 1)
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
            var httpGate = flags.Contains("http-gate");

            return new Options(
                // ⚠ Коли обсяг заданий комірками, межа за документами має не
                // заважати: інакше типові 300 обірвали б наповнення раніше за
                // ціль і замір пішов би на недоборі, не сказавши про це.
                Documents: Read(map, "documents", cells > 0 ? int.MaxValue : 300),
                TargetCells: cells,
                Fill: Read(map, "fill", 90),
                Year: Read(map, "year", 2026),
                ConnectionString: connection,

                // ⚠ `--gate` лишається синонімом `--store-bench`: його кличе
                // `tools/br07-load-test.ps1`, і тихо зламати чинний скрипт
                // заради чеснішої назви прапорця було б гіршим обміном.
                // Чесність винесена туди, де її читають, — у заголовок звіту.
                Gate: flags.Contains("gate") || flags.Contains("store-bench"),
                LoadSeconds: Read(map, "load-seconds", httpGate ? 120 : 900),
                LoadWorstSlice: map.GetValueOrDefault("load-slice") == "worst",
                HttpGate: httpGate,
                Url: map.GetValueOrDefault("url") ?? "http://localhost:5099",
                BootstrapPassword: map.GetValueOrDefault("bootstrap")
                    ?? Environment.GetEnvironmentVariable("ECR_Bootstrap__Password")
                    ?? "Dev-Bootstrap-2026!",
                LoadRps: Read(map, "load-rps", 25),
                Operators: Read(map, "operators", 3),
                Workers: Read(map, "workers", 0),
                Tables: ReadOptional(map, "tables"),
                MedianRows: ReadOptional(map, "median-rows"),
                MaxRows: ReadOptional(map, "max-rows"),
                NoGateTable: flags.Contains("no-gate-table"),
                FormulaColumns: flags.Contains("formula-columns"),
                SamplesOut: map.GetValueOrDefault("samples-out"),
                Parallel: Read(map, "parallel", 1));
        }

        private static int? ReadOptional(Dictionary<string, string> map, string key)
            => map.TryGetValue(key, out var raw)
               && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        private static int Read(Dictionary<string, string> map, string key, int fallback)
            => map.TryGetValue(key, out var raw)
               && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
    }
}
