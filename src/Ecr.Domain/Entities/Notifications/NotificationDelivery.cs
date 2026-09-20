// src/Ecr.Domain/Entities/Notifications/NotificationDelivery.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Notifications;

/// <summary>Підсумок спроби доставки. Числа зберігаються в базі — не перенумеровувати.</summary>
public enum NotificationDeliveryStatus : byte
{
    /// <summary>Канал прийняв повідомлення.</summary>
    Sent = 1,

    /// <summary>Канал відмовив або був недосяжний.</summary>
    Failed = 2,

    /// <summary>Не надсилалось: той самий <c>EventKey</c> уже йшов у цей канал у вікні дедуплікації.</summary>
    Suppressed = 3,
}

/// <summary>
/// Запис журналу доставок (<c>itg.NotificationDelivery</c>, <c>BE-32</c>).
/// </summary>
/// <remarks>
/// ⛔ Журнал довіри — лише вставка: методів зміни в класі немає (той самий
/// спосіб, що й у <c>wf.ApprovalEvent</c>).
///
/// ⚠ Зовнішнього ключа на канал немає навмисно: журнал мусить пережити
/// видалення каналу, а глобальне правило видалення — <c>Restrict</c>.
/// </remarks>
public sealed class NotificationDelivery : Entity<long>
{
    /// <summary>Найбільша довжина тексту помилки; довше — обрізається, бо журнал не має падати через довгу відмову.</summary>
    public const int ErrorMaxLength = 400;

    /// <summary>Найбільша довжина ключа події.</summary>
    public const int EventKeyMaxLength = 200;

    private NotificationDelivery() { }

    /// <summary>Створює запис.</summary>
    /// <param name="at">Момент спроби, UTC.</param>
    /// <param name="channelId">Канал.</param>
    /// <param name="eventKind">Подія.</param>
    /// <param name="eventKey">Ключ дедуплікації: та сама подія — той самий ключ.</param>
    /// <param name="status">Підсумок.</param>
    /// <param name="error">Причина відмови — без стека й без секрету.</param>
    public NotificationDelivery(
        DateTime at,
        int channelId,
        NotificationEventKind eventKind,
        string eventKey,
        NotificationDeliveryStatus status,
        string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eventKey.Length, EventKeyMaxLength);

        At = at;
        ChannelId = channelId;
        EventKind = eventKind;
        EventKey = eventKey;
        Status = status;
        Error = error is { Length: > ErrorMaxLength } ? error[..ErrorMaxLength] : error;
    }

    public DateTime At { get; private set; }
    public int ChannelId { get; private set; }
    public NotificationEventKind EventKind { get; private set; }
    public string EventKey { get; private set; } = null!;
    public NotificationDeliveryStatus Status { get; private set; }
    public string? Error { get; private set; }
}
