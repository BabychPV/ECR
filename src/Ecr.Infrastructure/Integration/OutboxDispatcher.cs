// src/Ecr.Infrastructure/Integration/OutboxDispatcher.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
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
    /// Відправляє чергу сповіщень.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки надіслано і скільки лишилося.</returns>
    public async Task<(int Sent, int Pending)> FlushAsync(CancellationToken ct)
    {
        var pending = await db.NotificationOutbox
            .Where(n => n.State == "Pending")
            .OrderBy(n => n.CreatedAt)
            .Take(MaxPerRun)
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
}
