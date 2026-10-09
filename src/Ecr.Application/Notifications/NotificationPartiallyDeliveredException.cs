// src/Ecr.Application/Notifications/NotificationPartiallyDeliveredException.cs
namespace Ecr.Application.Notifications;

/// <summary>
/// Лист ПІШОВ частині адресатів, а решту поштовий сервер відхилив (J1-03).
/// </summary>
/// <remarks>
/// ⛔ Це не провал події: повтор розіслав би лист удруге тим, хто його вже отримав. Транспорт
/// кидає цей виняток лише тоді, коли сервер прийняв лист хоча б для одного адресата; відмова
/// ВСІМ — звичайний збій відправки з повтором.
///
/// ⚠ Текст винятку — без адрес (він може піти в журнал як є); адреси — у <see cref="Rejected"/>.
/// </remarks>
public sealed class NotificationPartiallyDeliveredException : Exception
{
    /// <summary>Створює виняток.</summary>
    /// <param name="rejected">Адреси, які сервер відхилив.</param>
    public NotificationPartiallyDeliveredException(IReadOnlyList<string> rejected)
        : base("The notification was delivered to some recipients; the server rejected the rest.")
    {
        ArgumentNullException.ThrowIfNull(rejected);
        Rejected = rejected;
    }

    /// <summary>Адреси, які поштовий сервер відхилив.</summary>
    public IReadOnlyList<string> Rejected { get; }
}
