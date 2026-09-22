// src/Ecr.Infrastructure/Notifications/NotificationDispatcher.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;

namespace Ecr.Infrastructure.Notifications;

/// <summary>
/// Розсилає подію каналами за матрицею правил «подія × канал» (<c>BE-34</c>).
/// </summary>
/// <remarks>
/// ⛔ Провал ОДНОГО каналу не валить задачу і не блокує інші: сповіщення не
/// транзакційні, і недоступний вебхук не привід урвати прогін зведення. Кожна
/// спроба огорнута окремо, а її наслідок лягає рядком журналу.
///
/// ⛔ Мовчазного пропуску немає жодного: канал без відправника, дедуплікація і
/// відмова транспорту — ТРИ різні рядки, а не відсутність рядка. Система, що
/// мовчить, бо надіслала, і система, що мовчить, бо не мала кому, ззовні
/// однакові.
/// </remarks>
public sealed class NotificationDispatcher(
    INotificationDispatchStore store, IEnumerable<INotificationChannelSender> senders, IClock clock)
{
    /// <summary>
    /// Як часто той самий <c>EventKey</c> може піти в той самий канал.
    /// </summary>
    /// <remarks>
    /// Пів години — компроміс директиви №15 (§2.1): подія, що повторюється
    /// щохвилини (збір валиться на кожному тику), не має перетворювати канал на
    /// стрічку однакових повідомлень, яку вимикають разом із корисними. Вікно
    /// рахується від останньої УСПІШНОЇ доставки, тож невдала спроба не глушить
    /// подію на пів години.
    /// </remarks>
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(30);

    /// <summary>Розсилає подію; повертає, скільки куди пішло.</summary>
    /// <param name="notification">Подія.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<NotificationDispatchResult> DispatchAsync(
        NotificationEvent notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var plan = await store.GetPlanAsync(ct).ConfigureAwait(false);
        var targets = plan.TargetsOf(notification.Kind, notification.Severity).ToList();

        if (targets.Count == 0)
        {
            // Жодного правила на цю подію — не помилка конфігурації, а штатний
            // стан контуру, де каналів ще не заводили (§2.1 директиви №15).
            return new NotificationDispatchResult(0, 0, 0);
        }

        var now = clock.UtcNow;
        var since = now - DeduplicationWindow;
        var key = KeyOf(notification.EventKey);

        var sent = 0;
        var failed = 0;
        var suppressed = 0;

        foreach (var channel in targets)
        {
            if (await store.WasSentSinceAsync(channel.Id, key, since, ct).ConfigureAwait(false))
            {
                await AppendAsync(
                    channel,
                    notification,
                    key,
                    now,
                    NotificationDeliveryStatus.Suppressed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Та сама подія вже пішла в цей канал протягом {DeduplicationWindow.TotalMinutes:0} хв."),
                    ct)
                    .ConfigureAwait(false);

                suppressed++;
                continue;
            }

            var sender = senders.FirstOrDefault(s => s.Kind == channel.Kind);

            if (sender is null)
            {
                // ⚠ Саме `Failed`, а не тиша: канал увімкнений, правило на нього
                // є, а доставити його нічим — це стан, який мусить бути видно на
                // екрані доставок, а не лише в коді.
                await AppendAsync(
                    channel, notification, key, now, NotificationDeliveryStatus.Failed,
                    $"Відправника для транспорту {channel.Kind} не зареєстровано.", ct)
                    .ConfigureAwait(false);

                failed++;
                continue;
            }

            var error = await TrySendAsync(sender, channel, notification, ct).ConfigureAwait(false);

            await AppendAsync(
                channel,
                notification,
                key,
                clock.UtcNow,
                error is null ? NotificationDeliveryStatus.Sent : NotificationDeliveryStatus.Failed,
                error,
                ct)
                .ConfigureAwait(false);

            if (error is null)
            {
                sent++;
            }
            else
            {
                failed++;
            }
        }

        return new NotificationDispatchResult(sent, failed, suppressed);
    }

    /// <summary>
    /// Відправка однієї спроби; повертає текст відмови або <c>null</c> за успіху.
    /// </summary>
    /// <remarks>
    /// ⛔ Тут і тримається обіцянка «провал одного каналу не валить задачу»:
    /// виняток стає ЗНАЧЕННЯМ і йде в журнал. Нагору летить лише скасування —
    /// зупинка застосунку не є відмовою каналу.
    /// </remarks>
    private static async Task<string?> TrySendAsync(
        INotificationChannelSender sender,
        NotificationChannel channel,
        NotificationEvent notification,
        CancellationToken ct)
    {
        try
        {
            await sender
                .SendAsync(channel, new NotificationMessage(notification.Subject, notification.Body), ct)
                .ConfigureAwait(false);

            return null;
        }
        // ⚠ Умова `ct.IsCancellationRequested` обов'язкова: `OperationCanceledException`
        // БЕЗ зведеного токена — це не зупинка застосунку, а таймаут транспорту
        // (так поводиться `HttpClient`), і він має лягти рядком `Failed`, а не
        // обірвати розсилку по решті каналів.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Відмова каналу — це рядок журналу, а не аварія прогону.
        catch (Exception error)
#pragma warning restore CA1031
        {
            // ⛔ Без стека (ФВ-6.11): текст видно в інтерфейсі обслуговування.
            return error.Message;
        }
    }

    private Task AppendAsync(
        NotificationChannel channel,
        NotificationEvent notification,
        string key,
        DateTime at,
        NotificationDeliveryStatus status,
        string? error,
        CancellationToken ct)
        => store.AppendDeliveryAsync(
            new NotificationDelivery(at, channel.Id, notification.Kind, key, status, error), ct);

    /// <summary>
    /// Приводить ключ до того, що приймає стовпець журналу.
    /// </summary>
    /// <param name="eventKey">Ключ події.</param>
    /// <remarks>
    /// ⚠ Порожній ключ означав би «дедуплікації немає», і саме тому він тут
    /// заборонений, а не замінений на щось за замовчуванням.
    /// </remarks>
    private static string KeyOf(string eventKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);

        var trimmed = eventKey.Trim();

        return trimmed.Length > NotificationDelivery.EventKeyMaxLength
            ? trimmed[..NotificationDelivery.EventKeyMaxLength]
            : trimmed;
    }
}
