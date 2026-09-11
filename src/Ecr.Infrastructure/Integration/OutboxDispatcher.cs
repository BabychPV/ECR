// src/Ecr.Infrastructure/Integration/OutboxDispatcher.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Відправляє чергу <c>itg.NotificationOutbox</c> — і зі зведення, і поза ним.
/// </summary>
/// <remarks>
/// ⛔ Виділено з <c>NotificationJob</c> тому, що відправників стало двоє.
/// Зведення збоїв іде за розкладом (щогодини), а відмова джерела в
/// автентифікації мусить піти НЕГАЙНО (<c>H-20</c>, <c>D-125</c>): чекати
/// годину на алерт про те, що збір не працює взагалі, — це втратити нічний
/// прогін. Двох копій «як саме розсилається пошта» бути не може: вони
/// розійшлися б у тому, що найважче помітити, — у тому, кому лист НЕ пішов.
///
/// ⚠ «Не налаштовано» і «не доставлено» — <b>різні стани</b>. Без відправника
/// події не позначаються невдалими: вони чекають, і саме тому налаштування
/// транспорту не потребує повторного створення подій.
///
/// ⛔ Q-241: саме через те, що відправників ДВА і вони НЕ серіалізовані між
/// собою (`NotificationJob` за розкладом і `CollectionJob.AlertAuthenticationAsync`
/// негайно, кожне джерело — окремий Quartz `JobKey`, тож `[DisallowConcurrentExecution]`
/// і `SqlDistributedLock` — обидва прив'язані до job'и, а не до відправки
/// — не серіалізують РІЗНІ джерела між собою), одна протермінована облікова
/// пара для PI AF валить автентифікацію на N сутностях джерела ОДНИМ тиком
/// — і N викликів `FlushAsync` раніше читали ту саму партію `Pending`-рядків
/// у пам'ять і надсилали той самий лист N разів. Захоплення нижче (`Pending`
/// → `Sending` ОДНИМ атомарним `UPDATE` з предикатом `State == "Pending"`)
/// розв'язує це на рівні самого запиту: рядок, який устиг забрати інший
/// виклик, просто не потрапляє під оновлення. Обраний свідомо ЗАМІСТЬ
/// серіалізації через спільний `SqlDistributedLock` (ресурс на кшталт
/// `Ecr.Outbox.Flush`) — той нині НЕ блокує (`@LockTimeout = 0`), і пропуск
/// пропустив би НЕГАЙНИЙ алерт `H-20` аж до наступного щогодинного тика,
/// перетворюючи «негайно» на «за годину»; зробити лок блокуючим означало б
/// або новий параметр очікування в `SqlDistributedLock` (зміна, що зачіпає
/// й `StartupSequence`, і `QuartzJobAdapter`), або мовчазне «пропущено» в
/// ТОЧНО тому місці, де ФВ-11.3 вимагає протилежного. Атомарний `UPDATE`
/// не має жодного з цих компромісів: жодного очікування, жодної зміни
/// існуючого механізму локів.
/// </remarks>
public sealed class OutboxDispatcher(EcrDbContext db, IClock clock, INotificationSender sender)
{
    /// <summary>Скільки подій відправляти за один прогін.</summary>
    /// <remarks>
    /// Задача працює щогодини. Дві сотні листів за раз — це межа, за якою
    /// поштовий сервер починає вважати нас розсилкою.
    /// </remarks>
    public const int MaxPerRun = 200;

    /// <summary>Після скількох спроб перестати пробувати.</summary>
    /// <remarks>
    /// П'ять спроб — це п'ять годин. Довше означало б, що недоступна пошта
    /// щогодини стукає в мертвий сервер тижнями.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Скільки часу захоплення (<c>Sending</c>) лишається чинним, перш ніж
    /// вважати його завислим і повернути подію в чергу.
    /// </summary>
    /// <remarks>
    /// ⛔ Q-241: без цієї межі захоплення без відповідного повернення
    /// перетворило б один обірваний прогін (процес убито посеред SMTP-виклику)
    /// на подію, що зникла в стані <c>Sending</c> НАЗАВЖДИ — рівно та вада,
    /// заради якої черга взагалі існує (`P-13`). Десять хвилин — це з великим
    /// запасом більше за час одного SMTP-виклику (секунди) і набагато менше
    /// за годину між прогонами зведення: захоплення, що зависло, повертається
    /// в чергу задовго до наступного тика, а не після нього.
    /// </remarks>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Відправляє чергу сповіщень.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки надіслано і скільки лишилося в очікуванні (`Pending`) у всій черзі.</returns>
    public async Task<(int Sent, int Pending)> FlushAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        // ⛔ Захоплення, зависле довше межі, — процес, що впав посеред
        // відправки. Повертаємо в чергу ДО спроби взяти нову партію: інакше
        // зависла подія лишається невидимою до наступного виклику.
        await ReclaimStaleAsync(now, ct).ConfigureAwait(false);

        if (!sender.IsConfigured)
        {
            return (0, await PendingCountAsync(ct).ConfigureAwait(false));
        }

        var claimed = await ClaimBatchAsync(now, ct).ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return (0, await PendingCountAsync(ct).ConfigureAwait(false));
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

        foreach (var item in claimed)
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

        return (sent, await PendingCountAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Захоплює партію: атомарно переводить <c>Pending</c> → <c>Sending</c>
    /// рівно тих рядків, які ДІЙСНО були <c>Pending</c> у момент виконання
    /// <c>UPDATE</c> — інакше два одночасні відправники читають ту саму
    /// партію <c>Pending</c>-рядків у пам'ять і надсилають той самий лист
    /// двічі (Q-241).
    /// </summary>
    /// <remarks>
    /// ⛔ Захоплення в ДВА кроки навмисно. Перший — звичайний <c>SELECT</c>,
    /// що визначає КАНДИДАТІВ (за <c>CreatedAt</c>, до <see cref="MaxPerRun"/>
    /// штук). Другий — <c>ExecuteUpdateAsync</c>, що виконується ОДНИМ
    /// SQL-запитом із предикатом <c>State == "Pending"</c> у самому
    /// <c>WHERE</c>: рядок, який конкурентний виклик устиг забрати першим
    /// (і вже змінив на <c>Sending</c>), просто не підпадає під оновлення —
    /// саме звідси атомарність, а не з того, що виклики якось координуються
    /// між собою.
    ///
    /// ⚠ Токен захоплення (<see cref="NotificationOutboxItem.ClaimToken"/>,
    /// унікальний на кожен виклик) — щоб після <c>UPDATE</c> однозначно
    /// дістати САМЕ ТІ рядки, які забрав цей виклик. Звірка за часовою
    /// позначкою була б крихкою: <c>datetime2(3)</c> округлює значення при
    /// збереженні, і те, що лишилося в змінній .NET, може не збігтися
    /// побітово з тим, що повернеться з бази.
    /// </remarks>
    private async Task<List<NotificationOutboxItem>> ClaimBatchAsync(DateTime now, CancellationToken ct)
    {
        var candidateIds = await db.NotificationOutbox
            .Where(n => n.State == "Pending")
            .OrderBy(n => n.CreatedAt)
            .Take(MaxPerRun)
            .Select(n => n.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0)
        {
            return [];
        }

        var token = Guid.NewGuid();

        var claimedCount = await db.NotificationOutbox
            .Where(n => candidateIds.Contains(n.Id) && n.State == "Pending")
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(n => n.State, "Sending")
                    .SetProperty(n => n.ClaimedAt, now)
                    .SetProperty(n => n.ClaimToken, token),
                ct)
            .ConfigureAwait(false);

        if (claimedCount == 0)
        {
            // Уся партія-кандидат уже забрана кимось іншим між першим SELECT
            // і цим UPDATE — не помилка, просто нема чого відправляти цим
            // викликом.
            return [];
        }

        return await db.NotificationOutbox
            .Where(n => n.ClaimToken == token)
            .OrderBy(n => n.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Повертає в чергу захоплення, старіші за <see cref="ClaimTimeout"/>.</summary>
    private async Task ReclaimStaleAsync(DateTime now, CancellationToken ct)
    {
        var threshold = now - ClaimTimeout;

        await db.NotificationOutbox
            .Where(n => n.State == "Sending" && n.ClaimedAt != null && n.ClaimedAt < threshold)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(n => n.State, "Pending")
                    .SetProperty(n => n.ClaimedAt, (DateTime?)null)
                    .SetProperty(n => n.ClaimToken, (Guid?)null),
                ct)
            .ConfigureAwait(false);
    }

    private Task<int> PendingCountAsync(CancellationToken ct)
        => db.NotificationOutbox.CountAsync(n => n.State == "Pending", ct);
}
