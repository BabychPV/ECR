using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
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
    EcrDbContext db, IOrphanScanner scanner, IClock clock, IConsistencyMetrics metrics)
    : IBackgroundJob, IConsistencyCheckJob
{
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

        await progress.ReportKeyAsync(10, "jobs.consistencyOrphanedCells", ct).ConfigureAwait(false);
        issues.AddRange(await OrphanedCellsAsync(periodKeys, ct).ConfigureAwait(false));

        await progress.ReportKeyAsync(40, "jobs.consistencyBrokenRefs", ct).ConfigureAwait(false);
        issues.AddRange(await BrokenReferencesAsync(periodKeys, ct).ConfigureAwait(false));

        await progress.ReportKeyAsync(60, "jobs.consistencyArchiveCheck", ct).ConfigureAwait(false);
        issues.AddRange(await ArchiveChecksumsAsync(ct).ConfigureAwait(false));

        await progress.ReportKeyAsync(70, "jobs.consistencyUnboundCalculated", ct).ConfigureAwait(false);
        issues.AddRange(await UnboundCalculatedColumnsAsync(ct).ConfigureAwait(false));

        // ⚠ Перерахунок ознаки IsOrphaned — В ОБИДВА боки (ФВ-8.13a). Задача
        // симетрична: те, що ставить ознаку, її ж і знімає. Асиметрія тут не
        // половина функції, а пастка — виправлення довідника не розблокувало б
        // Submit, і користувач лишився б із помилкою, причину якої вже усунуто.
        await progress.ReportKeyAsync(80, "jobs.consistencyRecalcOrphaned", ct).ConfigureAwait(false);
        var rescanned = await scanner.ScanAllAsync(ct).ConfigureAwait(false);

        await WriteIssuesAsync(issues, ct).ConfigureAwait(false);

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
            $"{{\"issues\":{issues.Count},\"orphanFlagsChanged\":{rescanned.Changed},"
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
    /// ⛔ Закриті періоди тут теж перевіряються — і це не суперечить
    /// <see cref="OrphanScanPlan"/>. Різниця в тому, ЩО робиться: ознаку
    /// <c>IsOrphaned</c> у закритому періоді не чіпають (вона нічого не
    /// розблокує), а от знайдене порушення посилання записують — саме там
    /// воно найнебезпечніше, бо дані вже подані.
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
                select new OrphanRow(cell.PeriodKeyValue, cell.TableRowId, cell.ValueRegistryEntryId!.Value);

            var found = await query.Take(budget).ToListAsync(ct).ConfigureAwait(false);

            issues.AddRange(found.ConvertAll(f => new ConsistencyIssue(
                "ORPHANED_CELL",
                Severity: 2,
                "doc.CellValue",
                f.TableRowId,
                $"Комірка рядка {f.TableRowId} періоду {f.PeriodKey} посилається на запис довідника "
                + $"{f.RegistryEntryId}, якого не існує.")));
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
                select new BrokenRow(row.PeriodKeyValue, row.Id, row.TableInstanceId);

            var found = await query.Take(budget).ToListAsync(ct).ConfigureAwait(false);

            issues.AddRange(found.ConvertAll(f => new ConsistencyIssue(
                "BROKEN_FK",
                Severity: 3,
                "doc.TableRow",
                f.RowId,
                $"Рядок {f.RowId} посилається на екземпляр таблиці {f.TableInstanceId} "
                + $"періоду {f.PeriodKey}, якого не існує.")));
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
    private async Task<List<ConsistencyIssue>> ArchiveChecksumsAsync(CancellationToken ct)
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
                    "ARCHIVE_CHECKSUM",
                    Severity: 3,
                    "itg.ArchiveRun",
                    run.Id,
                    $"Контрольні суми прогону архівації {run.Id} не збіглися: "
                    + $"джерело {run.SourceJson ?? "—"}, архів {run.TargetJson ?? "—"}."));
            }
        }

        return issues;
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
    /// для колонки вона не є. Тип <c>Formula</c> сюди не входить навмисно —
    /// його джерело перевіряє публікація структури (<c>ECR-TMPL-4226</c>), і
    /// друга перевірка того самого стану доповідала б про вже відхилене.
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
                  && !db.CalculationBindings.Any(b => b.ColumnDefId == column.Id && b.IsActive)
            select new UnboundColumnRow(column.Id, table.Code, column.Code, sheet.TemplateVersionId);

        var found = await query.Take(MaxIssues).ToListAsync(ct).ConfigureAwait(false);

        return found.ConvertAll(f => new ConsistencyIssue(
            "UNBOUND_CALCULATED_COLUMN",
            // ⚠ Вага 2, як і в осиротілої комірки: дані не зіпсовані — їх
            // просто НЕМАЄ там, де форма обіцяє число. Вага 3 стоїть за
            // станами, де система суперечить сама собі (розбіжність сум
            // архіву, порушений FK), а тут конфігурація ще не добудована.
            Severity: 2,
            "cfg.ColumnDef",
            f.ColumnDefId,
            // Названо КОНКРЕТНУ колонку і рівно ту дію, яка лишається: шукати
            // винуватця серед сотень колонок версії руками — не діагностика.
            // ⚠ Подвійні дужки в маршруті — літерали `{id}`/`{outputCode}`:
            // саме так виглядає шлях, який має відкрити методолог.
            string.Format(
                CultureInfo.InvariantCulture,
                "Колонка {0}.{1} (версія шаблону {2}) має тип Calculated, тобто значення для неї "
                + "пише методологія, але жодної чинної прив'язки виходу до неї немає: перерахунок "
                + "для цієї колонки не робить нічого — мовчки, без помилки, — і оператор бачить "
                + "порожню клітинку, яку не має права заповнити. Прив'яжіть вихід методології до "
                + "цієї колонки (PUT /api/v1/methodologies/{{id}}/bindings/{3}/{{outputCode}}) або "
                + "увімкніть наявну прив'язку, якщо її вимкнено.",
                f.TableCode,
                f.ColumnCode,
                f.TemplateVersionId,
                f.ColumnDefId)));
    }

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
    /// SQL Server приймає максимум 2100 параметрів на запит, а тут їх 6 на
    /// знахідку. 300 × 6 = 1800 — із запасом на службові (той самий розрахунок,
    /// що й <c>NormalizedCellStore.MergeChunkSize</c>).
    /// </remarks>
    private const int IssueChunkSize = 300;

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

        foreach (var chunk in issues.Chunk(IssueChunkSize))
        {
            var parameters = new List<SqlParameter>(chunk.Length * 6);
            var values = new System.Text.StringBuilder();

            for (var i = 0; i < chunk.Length; i++)
            {
                var issue = chunk[i];

                if (i > 0)
                {
                    values.Append(',');
                }

                values.Append(CultureInfo.InvariantCulture,
                    $"(@now{i},@severity{i},@rule{i},@type{i},@id{i},@message{i})");

                parameters.Add(new SqlParameter($"@now{i}", now));
                parameters.Add(new SqlParameter($"@severity{i}", issue.Severity));
                parameters.Add(new SqlParameter($"@rule{i}", issue.RuleCode));
                parameters.Add(new SqlParameter($"@type{i}", issue.EntityType));
                parameters.Add(new SqlParameter($"@id{i}", issue.EntityId));
                parameters.Add(new SqlParameter($"@message{i}", issue.Message));
            }

            // ⚠ Конкатенація, а не `$"""..."""`: EF1002 забороняє інтерпольований
            // рядок у `ExecuteSqlRawAsync` навіть коли підставляються лише
            // ІМЕНА параметрів (`values` — плейсхолдери `@now0`..`@messageN`,
            // не значення) — аналізатор не вміє довести це статично.
            var sql =
                "MERGE aud.ConsistencyIssue WITH (HOLDLOCK) AS target\n" +
                "USING (VALUES " + values + ") AS source\n" +
                "    (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)\n" +
                "ON  target.RuleCode = source.RuleCode\n" +
                "AND target.EntityType = source.EntityType\n" +
                "AND target.EntityId = source.EntityId\n" +
                "AND target.ResolvedAt IS NULL\n" +
                "WHEN NOT MATCHED THEN INSERT\n" +
                "    (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)\n" +
                "    VALUES (source.DetectedAt, source.Severity, source.RuleCode,\n" +
                "            source.EntityType, source.EntityId, source.Message);";

            await db.Database.ExecuteSqlRawAsync(sql, parameters.ToArray(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Знахідка перевірки.</summary>
    /// <param name="RuleCode">Код правила.</param>
    /// <param name="Severity">Вага: 1 інформація, 2 попередження, 3 помилка.</param>
    /// <param name="EntityType">Тип сутності.</param>
    /// <param name="EntityId">Ідентифікатор сутності.</param>
    /// <param name="Message">Текст для людини.</param>
    private sealed record ConsistencyIssue(
        string RuleCode, byte Severity, string EntityType, long EntityId, string Message);

    private sealed record OrphanRow(int PeriodKey, long TableRowId, long RegistryEntryId);

    private sealed record BrokenRow(int PeriodKey, long RowId, long TableInstanceId);

    private sealed record ArchiveRow(long Id, string Status, string? SourceJson, string? TargetJson);

    private sealed record UnboundColumnRow(
        int ColumnDefId, string TableCode, string ColumnCode, int TemplateVersionId);
}
