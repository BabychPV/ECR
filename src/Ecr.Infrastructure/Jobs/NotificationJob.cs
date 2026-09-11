using System.Globalization;
using System.Text.Json;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
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
/// </remarks>
public sealed class NotificationJob(
    EcrDbContext db, IClock clock, Integration.OutboxDispatcher outbox) : IBackgroundJob
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

        await progress.ReportAsync(20, "Читання збоїв збору", ct).ConfigureAwait(false);

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

        // ⚠ Коди сутностей читаються ОДНИМ запитом на всі збої. Джерело у
        // зведенні має бути назване так, як його знає адміністратор, а не
        // числом: за `42` він не знайде нічого (`H-20`).
        var sourceIds = failed.Select(r => r.SourceEntityId).Distinct().ToList();

        var sourceCodes = sourceIds.Count == 0
            ? []
            : await db.SourceEntities
                .AsNoTracking()
                .Where(e => sourceIds.Contains(e.Id))
                .Take(MaxDigestItems)
                .Select(e => new { e.Id, e.Code })
                .ToDictionaryAsync(e => e.Id, e => e.Code, ct)
                .ConfigureAwait(false);

        var collection = failed
            .Select(r => new DigestItem(
                KindOf(r.ErrorMessage),
                sourceCodes.GetValueOrDefault(
                    r.SourceEntityId, r.SourceEntityId.ToString(CultureInfo.InvariantCulture)),
                r.Status,
                r.ErrorMessage,
                r.FinishedAt))
            .ToList();

        await progress.ReportAsync(60, "Читання збоїв обслуговування", ct).ConfigureAwait(false);

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

        var items = collection.Concat(maintenance).Concat(materialization).Take(MaxDigestItems).ToList();

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

        await progress.ReportAsync(80, "Відправка черги сповіщень", ct).ConfigureAwait(false);

        var (sent, pending) = await outbox.FlushAsync(ct).ConfigureAwait(false);

        await progress
            .ReportAsync(
                100,
                $"Збоїв у зведенні: {items.Count}; надіслано: {sent}; лишилося в черзі: {pending}",
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
