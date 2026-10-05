using System.Globalization;
using System.Text.Json;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Розсилання сповіщень про події робочого процесу і збоїв.
/// </summary>
/// <remarks>
/// Сповіщення — не транзакційна частина операції: якщо пошта недоступна,
/// подання документа все одно відбулося. Тому задача читає чергу подій, а не
/// викликається зсередини use-case.
///
/// ⚠ Задача робить дві речі: **зводить збої** в <c>itg.MaintenanceRun</c> і
/// **відправляє чергу** <c>itg.NotificationOutbox</c>.
/// <para>
/// ⛔ Транспорт доставки лишається за замовником (`P-13`): правила «кому що
/// надсилати» видно лише тоді, коли лист прийшов не тому. Поки відправника не
/// зареєстровано, події <b>лишаються в черзі</b> зі станом <c>Pending</c>, і
/// задача каже, скільки їх накопичилося. Мовчазна «успішна» доставка була б
/// гіршою за її відсутність: події зникали б, а система рапортувала б про
/// надіслані листи.
/// </para>
/// <para>
/// ⚠ `BE-34`: поруч із чергою процесу стоїть розсилка КАНАЛАМИ з бази
/// (<see cref="Notifications.NotificationDispatcher"/>). Це не другий транспорт
/// «про всяк випадок»: конфігурація процесу (`Smtp:*`) — запасний шлях першого
/// запуску, а канали заводить адміністратор в інтерфейсі, не чіпаючи
/// розгортання. Тому обидва шляхи отримують ОДИН і той самий перелік збоїв.
/// </para>
/// </remarks>
public sealed class NotificationJob(
    EcrDbContext db,
    IClock clock,
    Integration.OutboxDispatcher outbox,
    Notifications.NotificationDispatcher channels,
    IUiStringCatalog catalog) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "notification";

    /// <summary>
    /// Скільки збоїв входить у зведення.
    /// </summary>
    /// <remarks>
    /// Сто рядків у зведенні — це вже не сповіщення, а інцидент: читати їх
    /// ніхто не буде, а сам факт переповнення важливіший за перелік.
    /// </remarks>
    public const int MaxDigestItems = 100;

    /// <summary>
    /// Наскільки глибоко дивитися назад, якщо задача працює вперше.
    /// </summary>
    /// <remarks>
    /// Доба, а не «все». Перший запуск інакше вивалив би зведення за весь
    /// час існування журналу — і його б закрили, не читаючи.
    /// </remarks>
    public static TimeSpan FirstRunLookback => TimeSpan.FromDays(1);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var now = clock.UtcNow;
        var since = await SinceAsync(now, ct).ConfigureAwait(false);

        var run = new MaintenanceRun(Code, now);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Q-240: власний прогін зведення теж мусить закриватися при падінні.
        // У ЧУЖЕ зведення він не потрапить ніколи (`JobCode != Code` нижче —
        // сповіщати про себе нема кому), але рядок `Running` навічно псує
        // `SinceAsync` сусіднім прогонам і робить журнал обслуговування
        // неправдивим у єдиному місці, де стан задач узагалі видно.
        try
        {
            await RunAsync(run, now, since, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Власне зведення й розсилка; прогін уже відкрито.</summary>
    /// <param name="run">Відкритий прогін журналу обслуговування.</param>
    /// <param name="now">Момент початку прогону.</param>
    /// <param name="since">Від якого моменту брати збої.</param>
    /// <param name="progress">Прогрес задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task RunAsync(
        MaintenanceRun run, DateTime now, DateTime since, IJobProgress progress, CancellationToken ct)
    {
        await progress.ReportKeyAsync(20, "jobs.notificationReadingCollectionFailures", ct).ConfigureAwait(false);

        // Ідентифікатор перетворюється на рядок ВЖЕ після вибірки: усередині
        // запиту це був би виклик, який SQL Server форматує за своєю мовою.
        var failed = await db.CollectionRuns
            .AsNoTracking()
            .Where(r => r.FinishedAt >= since && r.Status != "Succeeded")
            .OrderByDescending(r => r.FinishedAt)
            .Take(MaxDigestItems)
            .Select(r => new { r.SourceEntityId, r.Status, r.ErrorMessage, r.FinishedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ ІНТ-3.3, D-118: ознака здоров'я — журнал покриття, а не тиша.
        // Матеріалізація, що ПРОПУСТИЛА інтервал (період закрито, стеля точок),
        // завершується успішно — у `JobProgress` вона не `Failed`, тож запит
        // матеріалізації нижче її не бачить. Єдиний слід — рядок
        // `itg.CollectionCoverage` зі статусом.
        //
        // ⚠ `ConflictKeptManual` свідомо поза зведенням: ручне значення в
        // комірці перемогло зібране — це очікувана поведінка («людина має
        // рацію»), її видно в стрічці подій UI, а не в листі про збої.
        // `SkippedWriteConflict` і `SkippedNeedsConfirmation` — навпаки, У
        // зведенні: значення не записано, і людина правки не робила.
        //
        // ⚠ `RegistryAutoCreated` (D-212) — теж поза зведенням: синк `External`
        // створив запис за політикою, це робота, а не збій; видно в журналі UI.
        //
        // ⚠ Групування (сутність, період, статус) — у базі: 5 000 пропусків
        // того самого періоду — ОДИН рядок із лічильником, а не сто рядків,
        // що витіснили б із зведення решту збоїв (`MaxDigestItems`).
        var coverage = await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => c.Status != null
                        && c.CoveredFrom >= since
                        && c.Status != CollectionCoverage.ConflictKeptManual
                        && c.Status != CollectionCoverage.RegistryAutoCreated)
            .GroupBy(c => new { c.SourceEntityId, c.PeriodKey, c.Status })
            .Select(g => new
            {
                g.Key.SourceEntityId,
                g.Key.PeriodKey,
                g.Key.Status,
                Count = g.Count(),
                At = g.Max(c => c.CoveredFrom),
                Details = g.Min(c => c.Details),
            })
            .OrderByDescending(g => g.At)
            .Take(MaxDigestItems)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Коди сутностей читаються ОДНИМ запитом на всі збої. Джерело у
        // зведенні має бути назване так, як його знає адміністратор, а не
        // числом: за `42` він не знайде нічого (`H-20`).
        var sourceIds = failed.Select(r => r.SourceEntityId)
            .Concat(coverage.Select(c => c.SourceEntityId))
            .Distinct()
            .ToList();

        var sourceCodes = sourceIds.Count == 0
            ? []
            : await db.SourceEntities
                .AsNoTracking()
                .Where(e => sourceIds.Contains(e.Id))
                .OrderBy(e => e.Id)
                .Take(sourceIds.Count)
                .Select(e => new { e.Id, e.Code })
                .ToDictionaryAsync(e => e.Id, e => e.Code, ct)
                .ConfigureAwait(false);

        // ⚠ U12: причину прогону збирач пише конвертом (ключ + параметри), а не
        // готовим реченням. У лист іде ТЕКСТ, резолвлений мовою листа; старі
        // рядки (сирий текст до U12) резолвер повертає як є. Вид рядка
        // (`KindOf`) — за СИРИМ значенням: ознака відмови в автентифікації живе
        // в ньому, а не в перекладі.
        var reasons = await JobProgressMessageResolver
            .ResolveManyAsync(catalog, DigestLanguage, [.. failed.Select(r => r.ErrorMessage)], ct)
            .ConfigureAwait(false);

        var collection = failed
            .Select((r, i) => new DigestItem(
                KindOf(r.ErrorMessage),
                sourceCodes.GetValueOrDefault(
                    r.SourceEntityId, r.SourceEntityId.ToString(CultureInfo.InvariantCulture)),
                r.Status,
                reasons[i],
                r.FinishedAt))
            .ToList();

        await progress.ReportKeyAsync(60, "jobs.notificationReadingMaintenanceFailures", ct).ConfigureAwait(false);

        var maintenance = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.FinishedAt >= since && r.Status != "Succeeded" && r.JobCode != Code)
            .OrderByDescending(r => r.FinishedAt)
            .Take(MaxDigestItems)
            .Select(r => new DigestItem("maintenance", r.JobCode, r.Status, r.DetailsJson, r.FinishedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ Q-235: матеріалізація (`MaterializeCollectedDataJob`) не пише НІ в
        // `itg.CollectionRun` (це не збір), НІ в `itg.MaintenanceRun` (вона не
        // ставиться через `ScheduleAsync`, а через `EnqueueAsync` — на кожен
        // документ+таблицю окремо). Єдиний слід її провалу — `itg.JobProgress`
        // (`QuartzJobAdapter.FinishAsync`, стан `Failed`, після вичерпання
        // ретраїв). Без цього запиту точки зібрано, а в комірки вони не
        // потрапили — і жодне зведення про це не сказало б ні слова: рівно та
        // сама тиша, яку решта цієї задачі свідомо не дозволяє (ІНТ-3.3).
        var materialization = await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.UpdatedAt >= since && p.State == "Failed" && p.JobCode == MaterializeJobCode)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(MaxDigestItems)
            .Select(p => new DigestItem(MaterializationKind, p.JobId, p.State, p.Error, p.UpdatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ ФВ-12.4/12.5 (Q-149, REQ-CLOSURE №36): провал перерахунку, імпорту, експорту й
        // знімка звіту — так само лише в `itg.JobProgress` зі станом `Failed` (після вичерпання
        // ретраїв). Клієнт уже отримав `202`/`200`, а без цього запиту збій бачив би лише той,
        // хто відкрив `/admin/jobs`: дані не перераховано, файл не вивантажено — і тиша.
        // ⚠ Кількість спроб («3 ретраї = 4 спроби») цей запит не змінює — лише повідомляє про
        // кінцевий `Failed`; проміжні спроби (`Requeue`) станом `Failed` не позначаються.
        var alertedCodes = AlertedJobKinds.Keys.ToArray();
        var jobFailures = (await db.JobProgresses
                .AsNoTracking()
                .Where(p => p.UpdatedAt >= since && p.State == "Failed" && alertedCodes.Contains(p.JobCode))
                .OrderByDescending(p => p.UpdatedAt)
                .Take(MaxDigestItems)
                .Select(p => new { p.JobId, p.JobCode, p.State, p.Error, p.UpdatedAt })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Select(p => new DigestItem(AlertedJobKinds[p.JobCode], p.JobId, p.State, p.Error, p.UpdatedAt))
            .ToList();

        var coverageItems = coverage
            .Select(c => new DigestItem(
                CoverageKind,
                sourceCodes.GetValueOrDefault(
                    c.SourceEntityId, c.SourceEntityId.ToString(CultureInfo.InvariantCulture)),
                c.Status!,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"період {c.PeriodKey}: {c.Count} подій; {c.Details}"),
                c.At))
            .ToList();

        var items = collection
            .Concat(maintenance)
            .Concat(materialization)
            .Concat(jobFailures)
            .Concat(coverageItems)
            .Take(MaxDigestItems)
            .ToList();

        // ⛔ Нуль адресатів — не помилка, а СТАН, який має бути видно (`D-125`).
        // Мовчазна система без адресатів і мовчазна система без збоїв ззовні
        // однакові; різницю показує лише цей рядок.
        //
        // ⚠ Перевіряється навіть коли транспорт не налаштований: спершу
        // виявиться, що писати нікому, і лише потім — що нічим.
        var subscriberCount = await db.Users
            .AsNoTracking()
            .CountAsync(u => u.ReceivesAlerts && u.IsActive && u.Email != null, ct)
            .ConfigureAwait(false);

        if (subscriberCount == 0)
        {
            items.Add(new DigestItem(
                "recipients",
                "sec.User.ReceivesAlerts",
                "None",
                "Алерти нікому не надсилаються: жоден активний користувач не має "
                + "увімкненого отримання алертів і заповненої пошти.",
                now));
        }

        // ⚠ Порожнє зведення теж записується, зі статусом «Succeeded».
        // Задача, що мовчить, коли все добре, і задача, що не запускалася,
        // ззовні виглядають однаково — а це різні стани.
        run.Complete(
            items.Count == 0 ? "Succeeded" : "Degraded",
            JsonSerializer.Serialize(new Digest(since, now, items.Count, items), Options),
            clock.UtcNow);

        // ⛔ У чергу йдуть САМЕ ЗБОЇ і одним листом на прогін (`D-119`).
        // Лист на кожну подію — шум; шум вимикають разом із корисними листами.
        // Інформаційний рядок про відсутність адресатів у лист не потрапляє:
        // писати нікому про те, що писати нікому, безглуздо.
        var failures = items.Where(i => i.Kind != "recipients").ToList();

        if (failures.Count > 0)
        {
            db.NotificationOutbox.Add(new Domain.Entities.Integration.NotificationOutboxItem(
                "maintenance.failures",
                $"ECR: збоїв за період — {failures.Count}",
                string.Join(
                    Environment.NewLine,
                    failures.Select(f => $"[{f.Kind}] {f.Subject}: {f.Status}. {f.Details}")),
                recipients: null,
                now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ `BE-34`. Одне повідомлення на ГРУПУ збоїв, а не на кожен збій — та
        // сама причина, що й у черзі вище (`D-119`): лист на подію є шум, а шум
        // вимикають разом із корисним. Груп рівно стільки, скільки різних подій
        // матриці правил, бо адміністратор має змогу надіслати «збій збору» в
        // один канал, а «збій задачі» — в інший.
        //
        // ⚠ CL-5: відправлене каналами входить у `sent` підсумку. Інакше розсилка
        // ЛИШЕ каналом (Teams без `Smtp:*`) читалася б як «збої є, відправлено 0» —
        // `SucceededWithErrors` у `/jobs` і жовта картка `jobs`, хоча лист дійшов.
        var channelSent = 0;

        foreach (var group in failures.GroupBy(f => EventKindOf(f.Kind, f.Subject, f.Status)))
        {
            var lines = group
                .Select(f => $"[{f.Kind}] {f.Subject}: {f.Status}. {f.Details}")
                .ToList();

            var dispatched = await channels
                .DispatchAsync(
                    new NotificationEvent(
                        group.Key,
                        group.Max(f => SeverityOf(f.Kind, f.Status)),
                        EventKeyOf(group.Key, group.Select(f => f.Subject)),
                        $"ECR: збоїв за період — {lines.Count}",
                        string.Join(Environment.NewLine, lines)),
                    ct)
                .ConfigureAwait(false);

            channelSent += dispatched.Sent;
        }

        await progress.ReportKeyAsync(80, "jobs.notificationSendingQueue", ct).ConfigureAwait(false);

        var (outboxSent, pending) = await outbox.FlushAsync(ct).ConfigureAwait(false);
        var sent = outboxSent + channelSent;

        await progress
            .ReportKeyAsync(
                100,
                "jobs.notificationDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                    // ⚠ CL-5: збоїв для ДОСТАВКИ, без інформаційного рядка «адресатів немає».
                    // Шаблон `jobs.notificationDone` його не показує; читає `JobCompletionWarning`.
                    [JobCompletionWarning.FailuresParam] = failures.Count.ToString(CultureInfo.InvariantCulture),
                    ["sent"] = sent.ToString(CultureInfo.InvariantCulture),
                    ["pending"] = pending.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Вид рядка зведення для відмови джерела в автентифікації (<c>H-20</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Рядок із цим видом означає, що збір не почнеться взагалі, доки не
    /// втрутиться людина. Решта видів означає «даних поки немає»; сплутати їх —
    /// це чекати на наздоганяння, якого не буде.
    /// </remarks>
    public const string AuthenticationKind = "collection.auth";

    /// <summary>Мова, якою в лист резолвиться причина прогону збору (U12).</summary>
    /// <remarks>
    /// ⚠ Судження: лист — ОДИН на всіх адресатів (<c>recipients: null</c>, політика
    /// <c>sec.User.ReceivesAlerts</c>), і мови адресата модель не зберігає. Тому —
    /// базова мова каталогу, на яку падає будь-який відсутній переклад
    /// (<see cref="Application.Localization.UiStringResolver.DefaultLanguage"/>).
    /// Лист мовою кожного адресата — окрема зміна черги сповіщень.
    /// </remarks>
    public const string DigestLanguage = Application.Localization.UiStringResolver.DefaultLanguage;

    /// <summary>Звичайний вид рядка про збій збору.</summary>
    public const string CollectionKind = "collection";

    /// <summary>
    /// Вид рядка зведення для провалу перенесення в комірки (<c>Q-235</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий від <see cref="CollectionKind"/> навмисно: збір і
    /// матеріалізація — різні задачі з різною ціною відмови
    /// (`MaterializeCollectedDataJob`, D-118) — «точки зібрано, але в комірки
    /// не потрапили» вимагає іншої дії, ніж «джерело не віддало даних».
    /// </remarks>
    public const string MaterializationKind = "materialize";

    /// <summary>
    /// Код задачі матеріалізації в <c>itg.JobProgress</c> — тим самим рядком,
    /// яким її ставить <c>CollectionJob.EnqueueMaterializationAsync</c>
    /// (<c>jobs.EnqueueAsync&lt;IMaterializeCollectedDataJob&gt;</c>).
    /// </summary>
    private static readonly string MaterializeJobCode =
        typeof(IMaterializeCollectedDataJob).FullName!;

    /// <summary>
    /// Задачі, чий кінцевий <c>Failed</c> потрапляє в зведення (<c>ФВ-12.4/12.5</c>): код у
    /// <c>itg.JobProgress</c> (повне ім'я маркера) → вид рядка зведення.
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік явний, а не «усі Failed»: збір і матеріалізація мають власні види, а
    /// службові задачі (узгодженість, пошук осиротілих) — власні шляхи. Вид рядка не збігається
    /// з <see cref="CollectionKind"/>, тож <see cref="EventKindOf(string)"/> відносить їх до
    /// <see cref="NotificationEventKind.JobFailed"/> — «збій задачі», а не «збій збору».
    /// </remarks>
    public static IReadOnlyDictionary<string, string> AlertedJobKinds { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [typeof(IRecalculationJob).FullName!] = "recalculation",
            [typeof(IFormulaRecalculationJob).FullName!] = "formula-recalculation",
            [typeof(IExcelImportJob).FullName!] = "excel-import",
            [typeof(IExcelExportJob).FullName!] = "excel-export",
            [typeof(IReportSnapshotJob).FullName!] = "report-snapshot",
        };

    /// <summary>
    /// Вид рядка зведення за текстом відмови прогону.
    /// </summary>
    /// <param name="errorMessage">Текст із <c>itg.CollectionRun.ErrorMessage</c>.</param>
    /// <returns><see cref="AuthenticationKind"/> або <see cref="CollectionKind"/>.</returns>
    /// <remarks>
    /// ⛔ Відмова в автентифікації дістає ВЛАСНИЙ вид рядка (<c>H-20</c>). У
    /// спільному <c>collection</c> вона читалася б як «зібрано 0 рядків» і
    /// нічим не відрізнялася б від джерела, яке просто мовчить, — а лікують ці
    /// два стани по-різному: перше править адміністратор, друге минає само.
    ///
    /// ⚠ Окремий метод, а не вираз усередині проєкції, саме щоб це правило
    /// можна було перевірити без бази.
    /// </remarks>
    public static string KindOf(string? errorMessage)
        => CollectionFailure.IsAuthenticationRefusal(errorMessage)
            ? AuthenticationKind
            : CollectionKind;

    /// <summary>
    /// Подія матриці правил за видом рядка зведення (<c>BE-34</c>).
    /// </summary>
    /// <param name="digestKind">Вид рядка: <see cref="CollectionKind"/> та сусіди.</param>
    /// <remarks>
    /// ⚠ Збій ЗБОРУ і збій ЗАДАЧІ — різні події матриці, бо їх лікують різні
    /// люди: перше — той, хто відповідає за джерело, друге — той, хто за
    /// сервер. Відмова джерела в автентифікації (<see cref="AuthenticationKind"/>)
    /// лишається збоєм збору: адресат той самий, хоч дія і термінова.
    /// </remarks>
    public static NotificationEventKind EventKindOf(string digestKind)
        => digestKind is CollectionKind or AuthenticationKind or CoverageKind
            ? NotificationEventKind.CollectionFailed
            : NotificationEventKind.JobFailed;

    /// <summary>
    /// Подія матриці за видом, темою і статусом рядка: уточнює <see cref="EventKindOf(string)"/>
    /// для подій, що мають власну клітинку матриці (ФВ-12.5, REQ-CLOSURE №36).
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього правило «ExportFailed / PartitionsRunningOut / ConsistencyIssuesFound → канал»
    /// можна було налаштувати, але воно ніколи не спрацьовувало: усі три йшли як
    /// <see cref="NotificationEventKind.JobFailed"/>. ⚠ Лише <c>Degraded</c> (знахідки, мала
    /// запас партицій) — власна подія; падіння самої перевірки (<c>Failed</c>) — збій задачі.
    /// </remarks>
    public static NotificationEventKind EventKindOf(string digestKind, string subject, string status)
    {
        if (digestKind == "excel-export")
        {
            return NotificationEventKind.ExportFailed;
        }

        if (digestKind == "maintenance" && status == "Degraded")
        {
            if (subject == PartitionCheckJob.Code)
            {
                return NotificationEventKind.PartitionsRunningOut;
            }

            if (subject == ConsistencyCheckJob.Code)
            {
                return NotificationEventKind.ConsistencyIssuesFound;
            }
        }

        return EventKindOf(digestKind);
    }

    /// <summary>
    /// Вид рядка зведення для події журналу покриття (<c>ІНТ-3.3</c>, <c>D-118</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Подія матриці — <see cref="NotificationEventKind.CollectionFailed"/>, а
    /// не <see cref="NotificationEventKind.JobFailed"/>: задача відпрацювала, а
    /// пропуск лікує той, хто відповідає за джерело й мапінг (стеля точок,
    /// закритий період), а не той, хто за сервер.
    /// </remarks>
    public const string CoverageKind = "coverage";

    /// <summary>
    /// Серйозність рядка зведення для матриці правил (<c>BE-34</c>).
    /// </summary>
    /// <param name="digestKind">Вид рядка.</param>
    /// <param name="status">Статус рядка.</param>
    /// <remarks>
    /// ⚠ Попередження — події покриття, де значення не записано, але причина
    /// відома й не є дефектом:
    /// <list type="bullet">
    /// <item><see cref="CollectionCoverage.SkippedPeriodClosed"/> — період закрито
    /// навмисно, людина вирішує, чи відкривати його;</item>
    /// <item><see cref="CollectionCoverage.SkippedWriteConflict"/> — рядок
    /// змінювали під час запису; значення не втрачено, наступний прогін
    /// спробує знову, і стан зазвичай минає сам;</item>
    /// <item><see cref="CollectionCoverage.SkippedNeedsConfirmation"/> — правило
    /// періоду вимагає підтвердження людини: потрібна дія, але це робота за
    /// правилом, а не збій.</item>
    /// </list>
    /// Стеля точок (<see cref="CollectionCoverage.SkippedPointCeiling"/>) —
    /// помилка: інтервал не згорнуто через конфігурацію, і сам він не мине.
    /// Події синку довідника (<c>D-212</c>): <see cref="CollectionCoverage.RegistryAutoCreated"/>
    /// — інформація (синк зробив свою роботу за політикою <c>External</c>);
    /// <see cref="CollectionCoverage.RegistryElementUnlinked"/>,
    /// <see cref="CollectionCoverage.RegistryDeactivated"/>,
    /// <see cref="CollectionCoverage.RegistryReactivated"/>,
    /// <see cref="CollectionCoverage.RegistryRuleViolation"/>,
    /// <see cref="CollectionCoverage.RegistryExternalKeyRelinked"/> — попередження:
    /// довідник змінився або чекає рішення людини, але це не збій.
    /// Решта рядків зведення — збої, як і раніше.
    /// Серйозність групи — найвища серед її рядків.
    /// </remarks>
    public static NotificationSeverity SeverityOf(string digestKind, string status)
        => digestKind != CoverageKind
            ? NotificationSeverity.Error
            : status switch
            {
                CollectionCoverage.RegistryAutoCreated or CollectionCoverage.SkippedDependency => NotificationSeverity.Info,
                CollectionCoverage.SkippedPeriodClosed
                    or CollectionCoverage.SkippedWriteConflict
                    or CollectionCoverage.SkippedNeedsConfirmation
                    or CollectionCoverage.RegistryElementUnlinked
                    or CollectionCoverage.RegistryDeactivated
                    or CollectionCoverage.RegistryReactivated
                    or CollectionCoverage.RegistryRuleViolation
                    or CollectionCoverage.RegistryExternalKeyRelinked => NotificationSeverity.Warning,
                _ => NotificationSeverity.Error,
            };

    /// <summary>
    /// Ключ дедуплікації: той самий НАБІР збоїв дає той самий ключ.
    /// </summary>
    /// <param name="kind">Подія матриці правил.</param>
    /// <param name="subjects">Що саме збоїло — джерела, коди задач.</param>
    /// <remarks>
    /// ⛔ Довгий набір згортається у відбиток, а НЕ обрізається. Обрізання дало
    /// б один ключ різним наборам збоїв: новий збій, що не вліз у двісті
    /// символів, придушувався б як «та сама подія» — тиха втрата рівно того
    /// сповіщення, заради якого все це й існує.
    /// </remarks>
    public static string EventKeyOf(NotificationEventKind kind, IEnumerable<string> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        var joined = string.Join(
            "|", subjects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var key = $"{kind}:{joined}";

        if (key.Length <= NotificationDelivery.EventKeyMaxLength)
        {
            return key;
        }

        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined));

        return $"{kind}:{Convert.ToHexString(digest)[..32]}";
    }

    /// <summary>Налаштування серіалізації зведення; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Від якого моменту брати збої.</summary>
    /// <remarks>
    /// ⚠ Межа — початок ПОПЕРЕДНЬОГО прогону цієї задачі, а не «останні N
    /// годин». Розклад можуть змінити, задачу — перезапустити руками, і фіксоване
    /// вікно тоді або пропустило б збої, або показало б їх удруге.
    /// </remarks>
    private async Task<DateTime> SinceAsync(DateTime now, CancellationToken ct)
    {
        var previous = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.JobCode == Code)
            .OrderByDescending(r => r.StartedAt)
            .Take(1)
            .Select(r => (DateTime?)r.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return previous ?? now - FirstRunLookback;
    }

    /// <summary>Зведення за період.</summary>
    /// <param name="Since">Початок вікна.</param>
    /// <param name="Until">Кінець вікна.</param>
    /// <param name="Count">Скільки збоїв увійшло.</param>
    /// <param name="Items">Самі збої.</param>
    private sealed record Digest(DateTime Since, DateTime Until, int Count, IReadOnlyList<DigestItem> Items);

    /// <summary>Один збій у зведенні.</summary>
    /// <param name="Kind">Звідки: <c>collection</c> або <c>maintenance</c>.</param>
    /// <param name="Subject">Що саме: сутність джерела або код задачі.</param>
    /// <param name="Status">Статус прогону.</param>
    /// <param name="Details">Подробиці — без стеків (ФВ-6.11).</param>
    /// <param name="At">Коли завершився.</param>
    private sealed record DigestItem(
        string Kind, string Subject, string Status, string? Details, DateTime? At);
}
