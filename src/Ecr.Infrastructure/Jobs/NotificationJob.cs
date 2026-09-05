using System.Globalization;
using System.Text.Json;
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
public sealed class NotificationJob(EcrDbContext db, IClock clock, INotificationSender sender)
    : IBackgroundJob
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

        var collection = failed
            .Select(r => new DigestItem(
                "collection",
                r.SourceEntityId.ToString(CultureInfo.InvariantCulture),
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

        var items = collection.Concat(maintenance).Take(MaxDigestItems).ToList();

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

        var (sent, pending) = await FlushOutboxAsync(ct).ConfigureAwait(false);

        await progress
            .ReportAsync(
                100,
                $"Збоїв у зведенні: {items.Count}; надіслано: {sent}; лишилося в черзі: {pending}",
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Відправляє чергу сповіщень.
    /// </summary>
    /// <returns>Скільки надіслано і скільки лишилося.</returns>
    /// <remarks>
    /// ⚠ «Не налаштовано» і «не доставлено» — <b>різні стани</b>. Без
    /// відправника події не позначаються невдалими: вони чекають, і саме тому
    /// налаштування транспорту не потребує повторного створення подій.
    /// </remarks>
    private async Task<(int Sent, int Pending)> FlushOutboxAsync(CancellationToken ct)
    {
        var pending = await db.NotificationOutbox
            .Where(n => n.State == "Pending")
            .OrderBy(n => n.CreatedAt)
            .Take(MaxOutboxPerRun)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pending.Count == 0 || !sender.IsConfigured)
        {
            return (0, pending.Count);
        }

        // ⚠ Адресати — ДАНІ, а не конфігурація (`D-125`): прапорець на
        // користувачі з заповненою поштою. Список у змінних оточення довелося б
        // міняти розгортанням щоразу, коли хтось іде у відпустку.
        var subscribers = await db.Users
            .AsNoTracking()
            .Where(u => u.ReceivesAlerts && u.IsActive && u.Email != null)
            .Select(u => u.Email!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sent = 0;

        foreach (var item in pending)
        {
            // ⚠ Адресати події перекривають загальних: подія може бути
            // адресною (наприклад, автору), і тоді розсилати її всім — шум.
            var explicitTo = (item.Recipients ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var recipients = explicitTo.Length > 0 ? explicitTo : [.. subscribers];

            if (recipients.Length == 0)
            {
                // ⛔ Подія без адресата не «надсилається нікуди»: вона
                // позначається невдалою з причиною. Інакше вона зникла б, і
                // ніхто не дізнався б, що адресатів не задано.
                item.MarkFailed(
                    "Адресатів не визначено: жоден активний користувач не має "
                    + "увімкненого отримання алертів і пошти.",
                    MaxAttempts);
                continue;
            }

            try
            {
                await sender.SendAsync(recipients, item.Subject, item.Body, ct).ConfigureAwait(false);
                item.MarkSent(clock.UtcNow);
                sent++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // ⛔ Текст без стека (ФВ-6.11): він видимий в інтерфейсі
                // обслуговування.
                item.MarkFailed(error.Message, MaxAttempts);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (sent, pending.Count - sent);
    }

    /// <summary>Скільки подій відправляти за один прогін.</summary>
    /// <remarks>
    /// Задача працює щогодини. Дві сотні листів за раз — це межа, за якою
    /// поштовий сервер починає вважати нас розсилкою.
    /// </remarks>
    public const int MaxOutboxPerRun = 200;

    /// <summary>Після скількох спроб перестати пробувати.</summary>
    /// <remarks>
    /// П'ять спроб — це п'ять годин. Довше означало б, що недоступна пошта
    /// щогодини стукає в мертвий сервер тижнями.
    /// </remarks>
    public const int MaxAttempts = 5;

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
