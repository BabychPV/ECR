using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перевірка узгодженості даних; знахідки пише в <c>aud.ConsistencyIssue</c>.
/// </summary>
/// <remarks>
/// ⚠ Задача **нічого не виправляє**. Автоматичне «полагодження» неузгодженості
/// приховало б її причину, а причина тут завжди важливіша за наслідок:
/// осиротілий рядок означає, що десь видалили запис довідника, на який
/// посилаються подані документи.
/// <para>
/// Єдиний виняток — <c>IsOrphaned</c>: його задача переставляє через
/// <see cref="IOrphanScanner"/>, бо це не «виправлення», а ПЕРЕРАХУНОК
/// похідної ознаки. Причина лишається на місці й лишається видимою.
/// </para>
/// </remarks>
/// <remarks>
/// ⚠ Реалізує ще й <see cref="IConsistencyCheckJob"/> — маркер, яким прикладний
/// шар ставить ту саму перевірку НА ВИМОГУ (<c>BE-30</c>,
/// <c>POST /api/v1/consistency/run</c>). Нічний розклад
/// (<c>RecurringScheduleService</c>) лишається на конкретному типі, тож у
/// черзі задача має два різні <c>JobCode</c>.
/// </remarks>
public sealed class ConsistencyCheckJob(
    EcrDbContext db, IOrphanScanner scanner, IClock clock, IConsistencyMetrics metrics,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    : IBackgroundJob, IConsistencyCheckJob
{
    /// <summary>Код правила: рядки результатів/кроків/входів прогонів, що не <c>Current</c>/<c>Running</c> (Q-17).</summary>
    public const string NonCurrentRunRowsRule = "NON_CURRENT_RUN_ROWS";

    /// <summary>Код правила: комірка посилається на відсутній запис довідника.</summary>
    public const string OrphanedCellRule = "ORPHANED_CELL";

    /// <summary>Код правила: рядок посилається на відсутній екземпляр таблиці.</summary>
    public const string BrokenFkRule = "BROKEN_FK";

    /// <summary>Код правила: контрольні суми прогону архівації не збіглися.</summary>
    public const string ArchiveChecksumRule = "ARCHIVE_CHECKSUM";

    /// <summary>Код правила: колонка <c>Calculated</c> без жодного джерела значення.</summary>
    public const string UnboundCalculatedRule = "UNBOUND_CALCULATED_COLUMN";

    /// <summary>Код правила: колонка <c>Formula</c> опублікованої версії без джерела значення.</summary>
    public const string UnsourcedFormulaRule = "UNSOURCED_FORMULA_COLUMN";

    /// <summary>Префікс ключа каталогу тексту знахідки; повний ключ — префікс + код правила.</summary>
    /// <remarks>
    /// ⛔ Текст знахідки — КОНВЕРТ <see cref="JobProgressMessageEnvelope"/> (<c>{"k":…,"p":{…}}</c>), той самий, що й
    /// у <c>itg.JobProgress.Message</c>: мови читача задача не знає, а готове українське речення на англійському
    /// інтерфейсі читалося як дефект (аудит live 12.11). Клієнт (<c>consistencyFindingText.ts</c>) перекладає ключ;
    /// старі рядки (не конверт) показує як є. Підстановки — людські значення (коди таблиці/колонки, період), тож
    /// пошук журналу за кодом таблиці й далі знаходить знахідку.
    /// </remarks>
    public const string FindingKeyPrefix = "consistency.finding.";

    /// <summary>Скільки символів сирої суми архіву лишається в підстановці.</summary>
    private const int ChecksumParamMaxLength = 300;

    /// <summary>
    /// Межа запасу після прогону прибирання для <see cref="NonCurrentRunRowsRule"/>, коли прибирання ще не
    /// виконувалося жодного разу.
    /// </summary>
    public static readonly TimeSpan NonCurrentGrace = TimeSpan.FromMinutes(10);

    /// <summary>Текст знахідки: конверт ключа каталогу з підстановками.</summary>
    /// <param name="ruleCode">Код правила.</param>
    /// <param name="parameters">Підстановки <c>{name}</c>.</param>
    /// <returns>Закодований конверт.</returns>
    public static string FindingText(string ruleCode, params (string Name, object Value)[] parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            values[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(FindingKeyPrefix + ruleCode, values));
    }

    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "consistency-check";

    /// <summary>Стеля знахідок одного проходу.</summary>
    /// <remarks>
    /// Тисяча знахідок — це вже не «знахідки», а зламані дані: далі писати
    /// нема сенсу, треба дивитися на причину. Межа не дає перевірці
    /// перетворити інцидент на кількагодинний запис у журнал.
    /// </remarks>
    private const int MaxIssues = 1_000;

    /// <summary>
    /// Скільки секунд дається ОДНОМУ запиту нічної перевірки.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>S-19</c>. Перевірка йшла під глобальним
    /// <c>Database:CommandTimeoutSeconds = 60</c>
    /// (<c>DependencyInjection.cs:58</c>) — числом, поставленим під
    /// ІНТЕРАКТИВНИЙ запит. На прод-обсязі (108 млн рядків
    /// <c>doc.CellValue</c>) анти-джойн не вкладався б у хвилину, і нічна
    /// перевірка мовчки падала б щоночі.
    /// <para>
    /// ⚠ Десять хвилин — стеля НА ЗАПИТ, і вона має сенс лише разом із
    /// розбиттям по періодах нижче: запит тепер бачить одну партицію, а не всю
    /// таблицю, тож десять хвилин на партицію — це вже не «побільше про всяк
    /// випадок», а межа, за якою щось справді не так із партицією. Число —
    /// судження; ключа конфігурації немає навмисно (див.
    /// <see cref="ArchiveJob.CommandTimeoutSeconds"/>), потреба названа
    /// окремим <c>[debt]</c>.
    /// </para>
    /// </remarks>
    public const int CommandTimeoutSeconds = 10 * 60;

    /// <summary>Стеля кількості періодів одного проходу.</summary>
    /// <remarks>
    /// ⚠ Не «вікно останніх N», а запобіжник від необмеженої матеріалізації
    /// (правило 6 <c>LayerRulesTests</c>). 600 — це п'ятдесят років за
    /// місячної гранулярності, тобто більше за будь-який строк зберігання в
    /// системі. Порядок — від найновішого, щоб упертися в стелю могли лише
    /// найстаріші періоди.
    /// </remarks>
    private const int MaxPeriods = 600;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Q-240: усе, що після відкриття прогону, — під catch. Без нього
        // виняток лишав рядок `Running`/`FinishedAt = NULL` назавжди, а
        // зведення `NotificationJob` бере збої за `FinishedAt >= since` і
        // такий рядок не бачить узагалі: провалена нічна перевірка не
        // доходила до людини ЖОДНИМ шляхом.
        try
        {
            await RunAsync(run, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Власне перевірка; прогін уже відкрито.</summary>
    /// <param name="run">Відкритий прогін журналу обслуговування.</param>
    /// <param name="progress">Прогрес задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task RunAsync(MaintenanceRun run, IJobProgress progress, CancellationToken ct)
    {
        var issues = new List<ConsistencyIssue>();

        // ⚠ Таймаут — на КОНТЕКСТ задачі (він scoped, свій на прогін) і
        // повертається назад у `finally` нижче, щоб стеля перевірки не
        // лишилася на випадковому наступному користувачі цього контексту.
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        try
        {
            await CheckAsync(run, issues, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    /// <summary>Власне перевірки; таймаут уже підняно.</summary>
    /// <param name="run">Відкритий прогін журналу обслуговування.</param>
    /// <param name="issues">Накопичувач знахідок.</param>
    /// <param name="progress">Прогрес задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task CheckAsync(
        MaintenanceRun run, List<ConsistencyIssue> issues, IJobProgress progress, CancellationToken ct)
    {
        // ⛔ Список періодів знімається ОДИН раз на прогін і роздається обом
        // перевіркам: два однакові `SELECT DISTINCT` по `doc.Period` нічого не
        // додали б, крім другого читання.
        var periodKeys = await PeriodKeysAsync(ct).ConfigureAwait(false);

        // ⛔ Знімок (рішення людини 12.11: «перевірка узгодженості — перевірка на момент часу»; аудит live 12.11:
        // 260 відкритих знахідок, жодна не правдива тижнями — MERGE лише вставляв). Знахідки правила, яке прохід
        // ПЕРЕВІРИВ ПОВНІСТЮ, ЗАМІНЮЮТЬСЯ його результатом: неправдиві видаляються, правдиві лишаються з моментом першого
        // виявлення, нові додаються. Пропущене правило (прибирання вимкнено) чи обрізане стелею (`MaxIssues`,
        // `MaxPeriods`) нічого не доводить про знахідки, яких не побачило, — його рядки не чіпаються. Область — пара
        // (RuleCode, EntityType): `ARCHIVE_CHECKSUM` з `EntityType = 'Period'` пише процедура архівації
        // (`03-archive-proc.sql`), а не ця задача, і видаляти їх вона не має права.
        var evaluated = new List<(string RuleCode, string EntityType)>();
        var periodsComplete = periodKeys.Count < MaxPeriods;

        void Collect(List<ConsistencyIssue> found, string ruleCode, string entityType, bool complete)
        {
            issues.AddRange(found);
            if (complete && found.Count < MaxIssues)
            {
                evaluated.Add((ruleCode, entityType));
            }
        }

        await progress.ReportKeyAsync(10, "jobs.consistencyOrphanedCells", ct).ConfigureAwait(false);
        Collect(await OrphanedCellsAsync(periodKeys, ct).ConfigureAwait(false), OrphanedCellRule, "doc.CellValue", periodsComplete);

        await progress.ReportKeyAsync(40, "jobs.consistencyBrokenRefs", ct).ConfigureAwait(false);
        Collect(await BrokenReferencesAsync(periodKeys, ct).ConfigureAwait(false), BrokenFkRule, "doc.TableRow", periodsComplete);

        await progress.ReportKeyAsync(60, "jobs.consistencyArchiveCheck", ct).ConfigureAwait(false);
        var archive = await ArchiveChecksumsAsync(ct).ConfigureAwait(false);
        Collect(archive.Issues, ArchiveChecksumRule, "itg.ArchiveRun", archive.Complete);

        await progress.ReportKeyAsync(70, "jobs.consistencyUnboundCalculated", ct).ConfigureAwait(false);
        Collect(await UnboundCalculatedColumnsAsync(ct).ConfigureAwait(false), UnboundCalculatedRule, "cfg.ColumnDef", true);
        Collect(await UnsourcedFormulaColumnsAsync(ct).ConfigureAwait(false), UnsourcedFormulaRule, "cfg.ColumnDef", true);

        var nonCurrent = await NonCurrentRunRowsAsync(periodKeys, ct).ConfigureAwait(false);
        Collect(nonCurrent.Issues, NonCurrentRunRowsRule, "calc.CalculationRun", nonCurrent.Evaluated && periodsComplete);

        // ⚠ Перерахунок ознаки IsOrphaned — В ОБИДВА боки (ФВ-8.13a). Задача
        // симетрична: те, що ставить ознаку, її ж і знімає. Асиметрія тут не
        // половина функції, а пастка — виправлення довідника не розблокувало б
        // Submit, і користувач лишився б із помилкою, причину якої вже усунуто.
        await progress.ReportKeyAsync(80, "jobs.consistencyRecalcOrphaned", ct).ConfigureAwait(false);
        var rescanned = await scanner.ScanAllAsync(ct).ConfigureAwait(false);

        await WriteIssuesAsync(issues, ct).ConfigureAwait(false);
        var removed = await DeleteVanishedAsync(issues, evaluated, run.StartedAt, ct).ConfigureAwait(false);

        // ⚠ Метрика — ЗА РІЗНОВИДОМ (RuleCode), а не одним сумарним числом
        // (директива №11, T10 #41): "ORPHANED_CELL" росте поступово (хтось
        // видаляє записи довідника), а "ARCHIVE_CHECKSUM" — це завжди
        // системна аварія; злите в одне число, друге ховалося б у шумі
        // першого на графіку.
        foreach (var group in issues.GroupBy(i => i.RuleCode, StringComparer.Ordinal))
        {
            metrics.RecordIssues(group.Count(), group.Key);
        }

        // ⚠ Підсумок пишеться ЗАВЖДИ, зокрема нульовий. Знахідка — баг, а не
        // шум (ФВ-7.7): якщо перевірка регулярно щось знаходить і це вважають
        // нормою, вона перестає працювати як сигнал.
        run.Complete(
            issues.Count == 0 ? "Succeeded" : "Degraded",
            // ⚠ Поруч зі «скільки ознак змінено» пишеться «скільки рядків
            // оглянуто» і «скільки повних обходів набору зроблено». Саме лише
            // `orphanFlagsChanged` описує однаковим нулем і здорову систему, і
            // сканер, що стоїть; деталі прогону — єдине місце, де цю різницю
            // взагалі можна побачити постфактум.
            $"{{\"issues\":{issues.Count},\"removed\":{removed},\"orphanFlagsChanged\":{rescanned.Changed},"
            + $"\"orphanRowsExamined\":{rescanned.ExaminedRows},"
            + $"\"orphanScanCycles\":{rescanned.CyclesCompleted}}}",
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await progress
            .ReportKeyAsync(
                100,
                "jobs.consistencyIssuesFound",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = issues.Count.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ключі періодів, по яких ходить перевірка — від найновішого.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>S-19</c>, і це головна половина виправлення: запити нижче ходять
    /// ПО ПЕРІОДАХ, а не по всій таблиці. <c>PeriodKey</c> — ключ партиціювання
    /// (<c>pf_ByPeriodKey</c>), тож запит із рівністю по ньому читає одну
    /// партицію, а без нього — усі; <c>WR-05</c> уже виміряв цю різницю на
    /// трьох партиціях: 3 логічні читання проти 53.
    /// <para>
    /// ⚠ Розбиття ПОВНЕ, а не вікно останніх N періодів. Вікно було б дешевше,
    /// але воно мовчки перестало б перевіряти старі дані — а саме там знахідка
    /// найнебезпечніша (дані вже подані), і саме її ніхто не шукав би. Ціна
    /// повного розбиття — кількадесят запитів замість одного: різних
    /// <c>PeriodKey</c> у системі стільки, скільки періодів у календарі
    /// (12 на рік за місячної гранулярності), і всі проєкти ділять їх між
    /// собою, бо ключ не знає про проєкт (<c>R-A6</c>).
    /// </para>
    /// <para>
    /// ⚠ <see cref="MaxPeriods"/> — запобіжник, а не те саме вікно: правило 6
    /// <c>LayerRulesTests</c> забороняє матеріалізацію без межі, і воно має
    /// рацію навіть тут. Межа стоїть настільки далеко (п'ятдесят років за
    /// місячної гранулярності), що впертися в неї означає негаразд у
    /// <c>doc.Period</c>, а не те, що перевірка чогось не додивилася.
    /// </para>
    /// <para>
    /// ⚠ Джерело списку — <c>doc.Period</c>, а не <c>DISTINCT</c> по самій
    /// <c>doc.CellValue</c>: друге і є той самий повний обхід таблиці, якого ми
    /// тут позбуваємося. Комірка в періоді, якого немає в <c>doc.Period</c>,
    /// неможлива — ланцюг <c>CellValue → TableRow → TableInstance →
    /// Document → Period</c> тримають FK, і <c>PeriodKey</c> входить у кожен
    /// із них.
    /// </para>
    /// <para>
    /// ⚠ Від найновішого: стеля <see cref="MaxIssues"/> спільна на перевірку,
    /// і якщо в неї впертися, обрізаними мають лишитися СТАРІ періоди, а не
    /// поточний, який зараз заповнюють.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<int>> PeriodKeysAsync(CancellationToken ct)
        => await db.Periods
            .AsNoTracking()
            .Select(p => p.PeriodKeyValue)
            .Distinct()
            .OrderByDescending(key => key)
            .Take(MaxPeriods)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Комірки, що посилаються на записи довідника, яких немає.
    /// </summary>
    /// <remarks>
    /// ⛔ Закриті періоди тут теж перевіряються — як і в <see cref="OrphanScanPlan"/>
    /// (✎ rule h4, 08.11: нічний прохід переоцінює й закриті місяці, тож ознака
    /// <c>IsOrphaned</c> знімається після виправлення). Знайдене порушення
    /// посилання записують — саме там воно найнебезпечніше, бо дані вже подані.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> OrphanedCellsAsync(
        IReadOnlyList<int> periodKeys, CancellationToken ct)
    {
        var issues = new List<ConsistencyIssue>();

        foreach (var periodKey in periodKeys)
        {
            var budget = MaxIssues - issues.Count;
            if (budget <= 0)
            {
                break;
            }

            // ⛔ `cell.PeriodKeyValue == periodKey` — предикат КЛЮЧА ПАРТИЦІЇ,
            // і він тут не оптимізація, а умова здійсненності: без нього
            // анти-джойн читав усю `doc.CellValue` (`S-19`).
            var query =
                from cell in db.CellValues.AsNoTracking()
                where cell.PeriodKeyValue == periodKey
                      && cell.ValueRegistryEntryId != null
                      && !db.RegistryEntries.Any(e => e.Id == cell.ValueRegistryEntryId)
                // Порядок — ключ комірки в межах партиції: на бюджеті `Take`
                // повторний прогін бачить ТІ САМІ перші знахідки, а не
                // довільну вибірку плану (EF 10102).
                orderby cell.TableRowId, cell.ColumnDefId
                select new OrphanRow(cell.PeriodKeyValue, cell.TableRowId, cell.ValueRegistryEntryId!.Value);

            var found = await query.Take(budget).ToListAsync(ct).ConfigureAwait(false);

            issues.AddRange(found.ConvertAll(f => new ConsistencyIssue(
                OrphanedCellRule,
                Severity: 2,
                "doc.CellValue",
                f.TableRowId,
                FindingText(
                    OrphanedCellRule, ("rowId", f.TableRowId), ("period", f.PeriodKey), ("entryId", f.RegistryEntryId)),
                f.PeriodKey)));
        }

        return issues;
    }

    /// <summary>
    /// Порушені посилання в гібридному режимі зберігання.
    /// </summary>
    /// <remarks>
    /// ⚠ У гібридній моделі (D-21) частина значень лежить у JSON, і зовнішній
    /// ключ їх не тримає: перевірити посилання може лише ця задача. У
    /// нормалізованій моделі те саме тримає FK, і знахідок тут не буває — саме
    /// тому ненульовий результат означає або гібрид, або зламане обмеження.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> BrokenReferencesAsync(
        IReadOnlyList<int> periodKeys, CancellationToken ct)
    {
        var issues = new List<ConsistencyIssue>();

        foreach (var periodKey in periodKeys)
        {
            var budget = MaxIssues - issues.Count;
            if (budget <= 0)
            {
                break;
            }

            // ⚠ Константа `periodKey` стоїть і в анти-джойні (`doc.TableInstance`
            // партиційована тим самим ключем), а не лише зовні: рівність
            // `i.PeriodKeyValue == row.PeriodKeyValue` семантично та сама, але
            // лише константа дає відсікання партицій на ОБОХ боках.
            var query =
                from row in db.TableRows.AsNoTracking()
                where row.PeriodKeyValue == periodKey
                      && !db.TableInstances.Any(
                          i => i.Id == row.TableInstanceId && i.PeriodKeyValue == periodKey)
                orderby row.Id
                select new BrokenRow(row.PeriodKeyValue, row.Id, row.TableInstanceId);

            var found = await query.Take(budget).ToListAsync(ct).ConfigureAwait(false);

            issues.AddRange(found.ConvertAll(f => new ConsistencyIssue(
                BrokenFkRule,
                Severity: 3,
                "doc.TableRow",
                f.RowId,
                FindingText(BrokenFkRule, ("rowId", f.RowId), ("instanceId", f.TableInstanceId), ("period", f.PeriodKey)),
                f.PeriodKey)));
        }

        return issues;
    }

    /// <summary>
    /// Звіряє архів із джерелом за контрольними сумами.
    /// </summary>
    /// <remarks>
    /// ⚠ Звіряються **суми прогону**, а не рядки: перечитати десятки мільйонів
    /// рядків архіву щоночі неможливо. Три суми (<c>COUNT</c>,
    /// <c>CHECKSUM_AGG</c>, <c>SUM</c>) записав сам прогін архівації — тут
    /// перевіряється, що вони збіглися і що прогін дійшов до кінця.
    /// </remarks>
    /// <returns>
    /// Знахідки й ознака повноти: <c>false</c>, коли прогонів не менше за стелю — старіші за неї не звірено, і
    /// закривати їхні знахідки не можна.
    /// </returns>
    private async Task<(List<ConsistencyIssue> Issues, bool Complete)> ArchiveChecksumsAsync(CancellationToken ct)
    {
        var runs = await db.ArchiveRuns
            .AsNoTracking()
            .Where(r => r.Status != "Running")
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxIssues)
            .Select(r => new ArchiveRow(r.Id, r.Status, r.ChecksumSourceJson, r.ChecksumTargetJson))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var issues = new List<ConsistencyIssue>();

        foreach (var run in runs)
        {
            // ⛔ Розбіжність сум — найважча знахідка: вона означає, що архів і
            // джерело кажуть різне про ті самі дані, і жодне з двох чисел не
            // можна вважати правильним.
            if (!string.Equals(run.SourceJson, run.TargetJson, StringComparison.Ordinal))
            {
                issues.Add(new ConsistencyIssue(
                    ArchiveChecksumRule,
                    Severity: 3,
                    "itg.ArchiveRun",
                    run.Id,
                    // ⚠ Суми — сирий JSON довільної довжини; конверт мусить уміститися в `Message` nvarchar(2000).
                    FindingText(
                        ArchiveChecksumRule,
                        ("runId", run.Id),
                        ("source", JobProgressMessageCodec.Shorten(run.SourceJson ?? "—", ChecksumParamMaxLength)),
                        ("target", JobProgressMessageCodec.Shorten(run.TargetJson ?? "—", ChecksumParamMaxLength)))));
            }
        }

        return (issues, runs.Count < MaxIssues);
    }

    /// <summary>
    /// Колонки типу <c>Calculated</c>, до яких не веде жодна ЧИННА прив'язка
    /// виходу методології (<c>cfg.CalculationBinding</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Це та сама діра, яку залишив по собі `#284`, і місце перевірки тут —
    /// не зручність, а єдиний чесний варіант. `#276` поставив вимогу «джерело
    /// для будь-якої обчислюваної колонки» на ПУБЛІКАЦІЮ СТРУКТУРИ, і `#284`
    /// звузив її до <c>Formula</c>, бо джерело <c>Calculated</c>-колонки живе
    /// ПОЗА версією шаблону і заводить його ІНША людина — методолог — уже
    /// після публікації. Ціна звуження була названа прямо: така колонка
    /// публікується, і в опублікованій формі це порожня клітинка, яку оператор
    /// не має права заповнити (<c>EditDenyReason.CalculatedCell</c>), і про яку
    /// не говорить ніхто.
    ///
    /// ⛔ Чому не повернути це в жоден ГЕЙТ життєвого циклу. Виміряно по
    /// наявних сценаріях, а не з міркувань:
    /// <list type="bullet">
    /// <item>публікація МЕТОДОЛОГІЇ —
    /// <c>CalculationOrchestratorConcurrencyScenarios.ArrangeMethodologyAsync</c>
    /// прив'язує вихід (крок 170) і лише потім публікує версію (крок 172), по
    /// одній методології за раз; на публікації першої друга
    /// <c>Calculated</c>-колонка ще не прив'язана — гейт відхилив би штатний
    /// порядок. До того ж методологія бачить лише ті таблиці, куди прив'язана
    /// САМА: колонку, якої не торкнулася жодна методологія, там не видно
    /// взагалі — тобто рівно той випадок, заради якого перевірка й потрібна;</item>
    /// <item>активація ПРОЄКТУ й відкриття періоду —
    /// <c>DataEntryScenarios.ArrangeRealDocumentAsync</c> активує проєкт
    /// (крок 1290) і створює документ (крок 1308) ДО того, як з'явиться хоч
    /// одна прив'язка; періоди ж відкриває <c>PeriodStateJob</c> за розкладом,
    /// а фонова задача нікому не може відмовити.</item>
    /// </list>
    /// Отже «<c>Calculated</c> без прив'язки» — законний ПЕРЕХІДНИЙ стан на
    /// КОЖНОМУ переході життєвого циклу, і жорсткий гейт там означав би втретє
    /// зіткнутися з тим самим порядком ролей.
    ///
    /// ⚠ Тому знахідка, а не відмова — і саме тут вона має сенс: ця задача
    /// єдина в системі бачить УСЮ конфігурацію одразу, обидві її половини
    /// (структуру версії й прив'язки), не будучи прив'язаною до чийогось
    /// кроку. Запис у <c>aud.ConsistencyIssue</c> переживає прогін і
    /// зіставляється за трійкою <c>(RuleCode, EntityType, EntityId)</c>, тож
    /// повторні проходи не плодять копій, а адміністратор дістає перевірку на
    /// вимогу через <c>POST /api/v1/jobs/{jobId}/restart</c>.
    ///
    /// ⚠ Лише версії НЕ заархівованих проєктів. Перевіряти всі версії підряд
    /// означало б щоночі доповідати про чернетки, які ще пишуть, і про
    /// виведені з обігу версії, яких уже ніхто не відкриє: журнал, у якому
    /// знахідка — норма, перестає бути сигналом (ФВ-7.7). Порожня клітинка
    /// шкодить рівно там, де за версією працює живий проєкт.
    ///
    /// ⚠ Лише <c>IsActive</c>-прив'язки — те саме звуження, що й у
    /// <c>ICalculationBindingStore.ListBoundColumnIdsAsync</c>: вимкнену
    /// прив'язку <c>RecalculationJob.BindingsAsync</c> не бере, тож джерелом
    /// для колонки вона не є. Тип <c>Formula</c> сюди не входить: його
    /// перевіряє окремий <see cref="UnsourcedFormulaColumnsAsync"/> з власним
    /// кодом правила, бо джерел у нього два (прив'язка АБО формула шаблону) і
    /// межа — стан версії, а не проєкту (HSE301 C5c).
    ///
    /// ⛔ Формула шаблону на колонці — теж джерело (аудит live 12.11): колонку, яку заповнює формула шаблону, правило
    /// не має називати «порожньою клітинкою». Визначення джерела — те саме, що в <see cref="UnsourcedFormulaColumnsAsync"/>
    /// і <c>PublishChecks.CheckComputedColumns</c>: не видалена формула цієї таблиці на цій колонці АБО активна прив'язка.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> UnboundCalculatedColumnsAsync(CancellationToken ct)
    {
        var liveVersions = db.Projects
            .AsNoTracking()
            .Where(p => p.Status != ProjectStatus.Archived)
            .Select(p => p.TemplateVersionId);

        var query =
            from column in db.ColumnDefs.AsNoTracking()
            join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
            join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
            where column.DataType == CellDataType.Calculated
                  && !column.IsDeleted && !table.IsDeleted && !sheet.IsDeleted
                  && liveVersions.Contains(sheet.TemplateVersionId)
                  && !db.FormulaDefs.Any(f =>
                      f.TableDefId == column.TableDefId && f.ColumnDefId == column.Id && !f.IsDeleted)
                  && !db.CalculationBindings.Any(b => b.ColumnDefId == column.Id && b.IsActive)
            orderby column.Id
            select new UnboundColumnRow(column.Id, table.Code, column.Code, sheet.TemplateVersionId);

        var found = await query.Take(MaxIssues).ToListAsync(ct).ConfigureAwait(false);

        return found.ConvertAll(f => new ConsistencyIssue(
            UnboundCalculatedRule,
            // ⚠ Вага 2, як і в осиротілої комірки: дані не зіпсовані — їх
            // просто НЕМАЄ там, де форма обіцяє число. Вага 3 стоїть за
            // станами, де система суперечить сама собі (розбіжність сум
            // архіву, порушений FK), а тут конфігурація ще не добудована.
            Severity: 2,
            "cfg.ColumnDef",
            f.ColumnDefId,
            // Названо КОНКРЕТНУ колонку (`{table}.{column}`) і рівно ту дію, яка лишається: шукати
            // винуватця серед сотень колонок версії руками — не діагностика.
            FindingText(
                UnboundCalculatedRule,
                ("table", f.TableCode),
                ("column", f.ColumnCode),
                ("version", f.TemplateVersionId),
                ("columnId", f.ColumnDefId))));
    }

    /// <summary>
    /// Колонки типу <c>Formula</c> на опублікованій або виведеній з обігу
    /// версії шаблону, в яких немає ЖОДНОГО джерела значення (HSE301 C5c).
    /// </summary>
    /// <remarks>
    /// ⛔ Визначення «джерела» — ТЕ САМЕ, що в
    /// <c>PublishChecks.CheckComputedColumns</c> (<c>ECR-TMPL-4226</c>): не
    /// видалена формула шаблону цієї таблиці на цій колонці
    /// (<c>cfg.FormulaDef.ColumnDefId</c>) АБО активна прив'язка виходу
    /// методології (<c>cfg.CalculationBinding</c>, <c>IsActive</c>). Там воно
    /// працює над графом версії в пам'яті, тут — SQL-запитом, тож спільного
    /// помічника без правки <c>PublishChecks</c> не винести; тому копія, і
    /// правити обидві — разом, інакше публікація й нічна перевірка
    /// розійдуться в тому, що вважати джерелом.
    ///
    /// ⚠ Друга лінія, а не перша. Публікація відхиляє такий стан
    /// (<c>ECR-TMPL-4226</c>), а відв'язка останнього джерела на опублікованій
    /// версії — <c>409</c> (<c>D-215</c>). У нормі знахідок тут немає; вони
    /// означають дані, що з'явилися до цих гейтів, або правку прямим SQL — і
    /// саме тому їх видно лише тут.
    ///
    /// ⚠ Лише <c>Published</c>/<c>Deprecated</c>: чернетку ще пишуть, і
    /// формула в ній «з'явиться пізніше» законно — її відхилить публікація.
    /// Виведену з обігу версію перевіряємо: за нею можуть досі жити документи.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> UnsourcedFormulaColumnsAsync(CancellationToken ct)
    {
        var query =
            from column in db.ColumnDefs.AsNoTracking()
            join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
            join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
            join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
            where column.DataType == CellDataType.Formula
                  && !column.IsDeleted && !table.IsDeleted && !sheet.IsDeleted
                  && (version.Status == TemplateVersionStatus.Published
                      || version.Status == TemplateVersionStatus.Deprecated)
                  && !db.FormulaDefs.Any(f =>
                      f.TableDefId == column.TableDefId && f.ColumnDefId == column.Id && !f.IsDeleted)
                  && !db.CalculationBindings.Any(b => b.ColumnDefId == column.Id && b.IsActive)
            orderby column.Id
            select new UnboundColumnRow(column.Id, table.Code, column.Code, sheet.TemplateVersionId);

        var found = await query.Take(MaxIssues).ToListAsync(ct).ConfigureAwait(false);

        return found.ConvertAll(f => new ConsistencyIssue(
            UnsourcedFormulaRule,
            // ⚠ Вага 2 — з тієї ж причини, що й у `UNBOUND_CALCULATED_COLUMN`:
            // дані не зіпсовані, їх просто немає там, де форма обіцяє число.
            Severity: 2,
            "cfg.ColumnDef",
            f.ColumnDefId,
            FindingText(
                UnsourcedFormulaRule,
                ("table", f.TableCode),
                ("column", f.ColumnCode),
                ("version", f.TemplateVersionId),
                ("columnId", f.ColumnDefId))));
    }

    /// <summary>
    /// Q-17: рядки <c>calc.CalculationResult/Step/Input</c> прогонів, що не <c>Current</c>/<c>Running</c>
    /// (інваріант «лише актуальний розрахунок», рішення людини 28.10).
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється лише коли прибирання увімкнено (<c>Calculations:SupersededPurge:Enabled</c>): доти кожна база,
    /// де історія ще не прибрана (Land - мільйони рядків), щоночі давала б знахідки за кожен період. Очікування після
    /// проходу <c>SupersededResultsSweepJob</c> - нуль; ненульове означає, що швидкий шлях/прохід недопрацював. Одна
    /// знахідка на «таблиця × період» (`EntityId` = період), кількість - у тексті. Партиція 0 (прогін без періоду)
    /// перевіряється теж: доти рядки нульового періоду лишалися «історією» (HSE301).
    /// <para>
    /// ⛔ Лічаться лише рядки прогонів, які стали неактуальними ДО старту останнього завершеного проходу прибирання
    /// (або понад <see cref="NonCurrentGrace"/> тому, якщо проходу ще не було). Аудит live 12.11: 30.10 перевірка й
    /// прибирання стартували одним cron за 10 мс одна від одної, перевірка порахувала рядки, які прибирання саме
    /// видаляло, і записала сім знахідок, неправдивих уже за секунду. Так само ручний запуск посеред перерахунку
    /// бачив щойно витіснений прогін, чиї рядки швидкий шлях прибирає після коміту. Рядок, якого прибирання ще НЕ
    /// МАЛО шансу побачити, — не порушення.
    /// </para>
    /// <para>
    /// ⚠ Моменту витіснення в схемі немає (`SupersededAt` — міграція, якої тут свідомо немає), тому — консервативна
    /// оцінка зверху: прогін витісняє лише новіший прогін того самого проєкту й періоду в момент свого завершення
    /// (<c>CalculationResultStore</c>: <c>Id &lt;</c> свого, той самий <c>ProjectId</c>/<c>PeriodKey</c>). Якщо жоден
    /// новіший не завершився після межі й сам прогін завершився до неї — він був неактуальним уже на старті
    /// проходу. Ціна оцінки — пізніше на добу, а не хибна тривога: пропущене сьогодні правило знайде наступна ніч.
    /// </para>
    /// </remarks>
    /// <returns>Знахідки й ознака <c>Evaluated</c>: <c>false</c> — правило пропущено (прибирання вимкнено).</returns>
    private async Task<(List<ConsistencyIssue> Issues, bool Evaluated)> NonCurrentRunRowsAsync(
        IReadOnlyList<int> periodKeys, CancellationToken ct)
    {
        var issues = new List<ConsistencyIssue>();
        if (!Persistence.SupersededPurgeOptions.Read(configuration).Enabled)
        {
            return (issues, false);
        }

        var cutoff = await NonCurrentCutoffAsync(ct).ConfigureAwait(false);
        var settled = SettledNonCurrentRuns(cutoff);

        foreach (var periodKey in periodKeys.Append(0).Distinct())
        {
            var budget = MaxIssues - issues.Count;
            if (budget <= 0)
            {
                break;
            }

            var results = await db.CalculationResults.AsNoTracking()
                .Where(row => row.PeriodKey == periodKey && settled.Contains(row.CalculationRunId))
                .LongCountAsync(ct).ConfigureAwait(false);
            var steps = await db.CalculationSteps.AsNoTracking()
                .Where(row => row.PeriodKey == periodKey && settled.Contains(row.CalculationRunId))
                .LongCountAsync(ct).ConfigureAwait(false);
            var inputs = await db.CalculationInputs.AsNoTracking()
                .Where(row => row.PeriodKey == periodKey && settled.Contains(row.CalculationRunId))
                .LongCountAsync(ct).ConfigureAwait(false);

            if (results + steps + inputs == 0)
            {
                continue;
            }

            issues.Add(new ConsistencyIssue(
                NonCurrentRunRowsRule,
                Severity: 2,
                "calc.CalculationRun",
                periodKey,
                FindingText(
                    NonCurrentRunRowsRule,
                    ("period", periodKey),
                    ("results", results),
                    ("steps", steps),
                    ("inputs", inputs)),
                periodKey));
        }

        return (issues, true);
    }

    /// <summary>
    /// Межа «прибирання вже мало шанс»: старт останнього успішного проходу <c>superseded-results-sweep</c>, а без
    /// нього — <see cref="NonCurrentGrace"/> тому.
    /// </summary>
    private async Task<DateTime> NonCurrentCutoffAsync(CancellationToken ct)
    {
        var sweepCode = SupersededResultsSweepJob.Code;
        var lastSweep = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.JobCode == sweepCode && r.Status == "Succeeded" && r.FinishedAt != null)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (DateTime?)r.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return lastSweep ?? clock.UtcNow - NonCurrentGrace;
    }

    /// <summary>Прогони, неактуальні вже на момент <paramref name="cutoff"/> (див. <see cref="NonCurrentRunRowsAsync"/>).</summary>
    private IQueryable<long> SettledNonCurrentRuns(DateTime cutoff)
        => from run in db.CalculationRuns.AsNoTracking()
           where run.Status != CalculationRun.CurrentStatus && run.Status != "Running"
                 && (run.FinishedAt ?? run.StartedAt) < cutoff
                 && !db.CalculationRuns.Any(newer =>
                     newer.ProjectId == run.ProjectId
                     && newer.PeriodKey == run.PeriodKey
                     && newer.Id > run.Id
                     && newer.FinishedAt != null
                     && newer.FinishedAt >= cutoff)
           select run.Id;

    /// <summary>
    /// Записує знахідки, не плодячи дублікатів.
    /// </summary>
    /// <remarks>
    /// Повторна знахідка тієї самої проблеми зіставляється за трійкою
    /// <c>(RuleCode, EntityType, EntityId)</c>. Без цього щонічний прохід
    /// перетворив би журнал на копії однієї проблеми — і справжня нова
    /// знахідка потонула б серед них.
    /// </remarks>
    /// <summary>Скільки знахідок іде в один <c>MERGE</c>.</summary>
    /// <remarks>
    /// SQL Server приймає максимум 2100 параметрів на запит, а тут їх 7 на
    /// знахідку (N1-05 додав <c>PeriodKey</c>). 250 × 7 = 1750 — із запасом на службові (той самий розрахунок,
    /// що й <c>NormalizedCellStore.MergeChunkSize</c>).
    /// </remarks>
    private const int IssueChunkSize = 250;

    private async Task WriteIssuesAsync(IReadOnlyList<ConsistencyIssue> issues, CancellationToken ct)
    {
        if (issues.Count == 0)
        {
            return;
        }

        // ⛔ Q-169 (аудит фази 2, продуктивність): ОДИН MERGE на чанк замість
        // окремого `IF NOT EXISTS...INSERT` на кожну знахідку — та сама
        // ідемпотентність за трійкою (RuleCode, EntityType, EntityId) серед
        // НЕЗАКРИТИХ знахідок, лише пакетна умова замість запиту на рядок.
        var now = clock.UtcNow;

        // ⚠ Одна знахідка на трійку: `WHEN MATCHED THEN UPDATE` нижче падає (Msg 8672), якщо джерело двічі зачіпає той
        // самий рядок цілі, — а дві осиротілі комірки одного рядка дають саме дві однакові трійки.
        var distinct = issues
            .GroupBy(i => (i.RuleCode, i.EntityType, i.EntityId))
            .Select(g => g.First())
            .ToList();

        foreach (var chunk in distinct.Chunk(IssueChunkSize))
        {
            var parameters = new List<SqlParameter>(chunk.Length * 7);
            var values = new System.Text.StringBuilder();

            for (var i = 0; i < chunk.Length; i++)
            {
                var issue = chunk[i];

                if (i > 0)
                {
                    values.Append(',');
                }

                values.Append(CultureInfo.InvariantCulture,
                    $"(@now{i},@severity{i},@rule{i},@type{i},@id{i},@message{i},@period{i})");

                parameters.Add(new SqlParameter($"@now{i}", now));
                parameters.Add(new SqlParameter($"@severity{i}", issue.Severity));
                parameters.Add(new SqlParameter($"@rule{i}", issue.RuleCode));
                parameters.Add(new SqlParameter($"@type{i}", issue.EntityType));
                parameters.Add(new SqlParameter($"@id{i}", issue.EntityId));
                parameters.Add(new SqlParameter($"@message{i}", issue.Message));

                // ⚠ Типізований параметр: безтиповий `DBNull` у `VALUES` вивівся б як nvarchar і зламав `int`-колонку.
                parameters.Add(new SqlParameter($"@period{i}", System.Data.SqlDbType.Int)
                {
                    Value = (object?)issue.PeriodKey ?? DBNull.Value,
                });
            }

            // ⚠ Конкатенація, а не `$"""..."""`: EF1002 забороняє інтерпольований
            // рядок у `ExecuteSqlRawAsync` навіть коли підставляються лише
            // ІМЕНА параметрів (`values` — плейсхолдери `@now0`..`@messageN`,
            // не значення) — аналізатор не вміє довести це статично.
            var sql =
                "MERGE aud.ConsistencyIssue WITH (HOLDLOCK) AS target\n" +
                "USING (VALUES " + values + ") AS source\n" +
                "    (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message, PeriodKey)\n" +
                "ON  target.RuleCode = source.RuleCode\n" +
                "AND target.EntityType = source.EntityType\n" +
                "AND target.EntityId = source.EntityId\n" +
                "AND target.ResolvedAt IS NULL\n" +
                // ⚠ Відкрита знахідка, яку прохід побачив знову, отримує ТЕПЕРІШНІЙ текст (кількості рядків NON_CURRENT
                // змінюються; старий український текст стає конвертом каталогу). `DetectedAt` — момент ПЕРШОГО
                // виявлення, і його оновлення приховало б вік проблеми.
                "WHEN MATCHED THEN UPDATE SET\n" +
                "    Message = source.Message, Severity = source.Severity, PeriodKey = source.PeriodKey\n" +
                "WHEN NOT MATCHED THEN INSERT\n" +
                "    (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message, PeriodKey)\n" +
                "    VALUES (source.DetectedAt, source.Severity, source.RuleCode,\n" +
                "            source.EntityType, source.EntityId, source.Message, source.PeriodKey);";

            await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Видаляє знахідки повністю перевірених областей, яких цей прохід НЕ знайшов (знімок на момент перевірки).
    /// </summary>
    /// <param name="issues">Знахідки цього проходу.</param>
    /// <param name="evaluated">Області (RuleCode, EntityType), перевірені повністю.</param>
    /// <param name="runStartedAt">Старт цього проходу.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки знахідок видалено.</returns>
    /// <remarks>
    /// ⛔ Рішення людини 12.11: «система має реально показувати проблеми, а коли вони зникають — видаляти
    /// повідомлення; узгодженість — це перевірка на момент часу». Аудит live 12.11: журнал показував 260 відкритих
    /// знахідок, а нічні проходи тижнями писали <c>"issues":0</c> — умова зникла, а знахідка лишалася назавжди.
    /// Тому неправдива знахідка ВИДАЛЯЄТЬСЯ (а не позначається <c>ResolvedAt</c>); так само йдуть і закриті рядки
    /// області — у знімку їм місця немає.
    /// <para>
    /// ⚠ Не видаляється знахідка, виявлена ПІСЛЯ старту цього проходу (<c>DetectedAt &gt;= runStartedAt</c>): її міг
    /// записати паралельний прохід (нічний і ручний), що бачив новіші дані, ніж цей.
    /// </para>
    /// <para>
    /// ⚠ Один запит на прохід — обидва переліки йдуть JSON-масивами через <c>OPENJSON</c>, без межі 2100 параметрів.
    /// <c>aud.ConsistencyIssue</c> — журнал знахідок, а не журнал аудиту (<c>11-audit-tables.sql</c>: таблиця не
    /// входить у незмінні), тож видалення з неї дозволене.
    /// </para>
    /// </remarks>
    private async Task<int> DeleteVanishedAsync(
        IReadOnlyList<ConsistencyIssue> issues,
        List<(string RuleCode, string EntityType)> evaluated,
        DateTime runStartedAt,
        CancellationToken ct)
    {
        if (evaluated.Count == 0)
        {
            return 0;
        }

        var scopes = System.Text.Json.JsonSerializer.Serialize(
            evaluated.Select(e => new { r = e.RuleCode, t = e.EntityType }));
        var found = System.Text.Json.JsonSerializer.Serialize(
            issues.Select(i => new { r = i.RuleCode, t = i.EntityType, id = i.EntityId }));

        const string sql = """
            DELETE target
              FROM aud.ConsistencyIssue AS target
              JOIN OPENJSON(@scopes) WITH (r nvarchar(64) '$.r', t nvarchar(64) '$.t') AS scope
                ON scope.r = target.RuleCode AND scope.t = target.EntityType
             WHERE target.DetectedAt < @runStartedAt
               AND (target.ResolvedAt IS NOT NULL
                    OR NOT EXISTS (
                       SELECT 1
                         FROM OPENJSON(@found) WITH (r nvarchar(64) '$.r', t nvarchar(64) '$.t', id bigint '$.id') AS f
                        WHERE f.r = target.RuleCode AND f.t = target.EntityType AND f.id = target.EntityId));
            """;

        return await db.Database
            .ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@runStartedAt", System.Data.SqlDbType.DateTime2) { Value = runStartedAt },
                    new SqlParameter("@scopes", System.Data.SqlDbType.NVarChar, -1) { Value = scopes },
                    new SqlParameter("@found", System.Data.SqlDbType.NVarChar, -1) { Value = found },
                ],
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Знахідка перевірки.</summary>
    /// <param name="RuleCode">Код правила.</param>
    /// <param name="Severity">Вага: 1 інформація, 2 попередження, 3 помилка.</param>
    /// <param name="EntityType">Тип сутності.</param>
    /// <param name="EntityId">Ідентифікатор сутності.</param>
    /// <param name="Message">Текст для людини.</param>
    /// <param name="PeriodKey">
    /// Період рядка документа (N1-05): ключ партиції, за яким читач журналу шукає <c>doc.TableRow</c>; <c>null</c> —
    /// знахідка без періоду (структура шаблону, архів).
    /// </param>
    private sealed record ConsistencyIssue(
        string RuleCode, byte Severity, string EntityType, long EntityId, string Message, int? PeriodKey = null);

    private sealed record OrphanRow(int PeriodKey, long TableRowId, long RegistryEntryId);

    private sealed record BrokenRow(int PeriodKey, long RowId, long TableInstanceId);

    private sealed record ArchiveRow(long Id, string Status, string? SourceJson, string? TargetJson);

    private sealed record UnboundColumnRow(
        int ColumnDefId, string TableCode, string ColumnCode, int TemplateVersionId);
}
