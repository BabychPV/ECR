using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Integration;

/// <summary>
/// Черга сповіщень (<c>itg.NotificationOutbox</c>).
/// </summary>
/// <remarks>
/// ⚠ Сповіщення — **не транзакційна частина операції**: якщо пошта
/// недоступна, подання документа все одно відбулося. Тому подія кладеться в
/// чергу тим самим комітом, що й сама зміна, а відправляє її окрема задача.
/// Спроба відправити всередині use-case зробила б доставку листа умовою
/// збереження даних.
///
/// ⛔ Черга з'явилася після `P-13`: у пакеті її не було взагалі, і
/// <c>NotificationJob</c> не мав що читати. Політика — кому й на які події
/// писати — лишається за замовником; черга ж потрібна за будь-якої політики,
/// бо без неї подія втрачається між збереженням і відправкою.
/// </remarks>
public sealed class NotificationOutboxItem : Entity<long>
{
    private NotificationOutboxItem() { }

    /// <summary>Кладе подію в чергу.</summary>
    /// <param name="eventCode">Що сталося: <c>sheet.submitted</c>, <c>collection.failed</c>.</param>
    /// <param name="subject">Тема листа.</param>
    /// <param name="body">Текст; уже локалізований на момент постановки.</param>
    /// <param name="recipients">Адресати через кому; порожньо — визначить політика.</param>
    /// <param name="utcNow">Момент постановки.</param>
    public NotificationOutboxItem(
        string eventCode, string subject, string body, string? recipients, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        EventCode = eventCode;
        Subject = subject;
        Body = body;
        Recipients = recipients;
        CreatedAt = utcNow;
        State = "Pending";
    }

    public string EventCode { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;

    /// <summary>Адресати; <c>null</c> — визначаються при відправці.</summary>
    public string? Recipients { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }

    /// <summary>«Pending», «Sent», «Failed».</summary>
    public string State { get; private set; } = null!;

    /// <summary>Скільки разів пробували відправити.</summary>
    public int Attempts { get; private set; }

    /// <summary>Текст останньої помилки — без стеків (ФВ-6.11).</summary>
    public string? Error { get; private set; }

    /// <summary>Фіксує успішну відправку.</summary>
    /// <param name="utcNow">Момент відправки.</param>
    public void MarkSent(DateTime utcNow)
    {
        State = "Sent";
        SentAt = utcNow;
        Error = null;
        Attempts++;
    }

    /// <summary>
    /// Фіксує невдалу спробу.
    /// </summary>
    /// <param name="error">Причина; без стека.</param>
    /// <param name="maxAttempts">Після скількох спроб перестати пробувати.</param>
    /// <remarks>
    /// ⚠ Невдала відправка — <b>не втрата</b>: подія лишається в черзі й
    /// повторюється. Але й не вічно: після межі вона позначається невдалою,
    /// інакше недоступна пошта перетворює чергу на нескінченний цикл, який
    /// щогодини стукає в мертвий сервер.
    /// </remarks>
    public void MarkFailed(string error, int maxAttempts)
    {
        Attempts++;
        Error = error;

        if (Attempts >= maxAttempts)
        {
            State = "Failed";
        }
    }
}
