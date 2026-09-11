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

    /// <summary>«Pending», «Sending», «Sent», «Failed».</summary>
    public string State { get; private set; } = null!;

    /// <summary>Скільки разів пробували відправити.</summary>
    public int Attempts { get; private set; }

    /// <summary>Текст останньої помилки — без стеків (ФВ-6.11).</summary>
    public string? Error { get; private set; }

    /// <summary>Коли рядок захопив флешер (стан <c>Sending</c>).</summary>
    /// <remarks>
    /// ⛔ Q-241: без цього поля захоплення нічим не відрізнялося б від
    /// зависання — рядок, чий процес упав посеред <c>SendAsync</c>, лишався
    /// б у стані <c>Sending</c> назавжди, і черга втратила б подію
    /// назавжди. Вік цього поля — єдине, за чим відправник (<c>OutboxDispatcher</c>,
    /// `Ecr.Infrastructure`) відрізняє «хтось відправляє прямо зараз» від
    /// «відправник упав, поверни в чергу».
    /// </remarks>
    public DateTime? ClaimedAt { get; private set; }

    /// <summary>Токен, що однозначно ідентифікує ЦЕЙ виклик захоплення.</summary>
    /// <remarks>
    /// ⚠ Не часова позначка: <c>datetime2(3)</c> округлює значення при
    /// збереженні, і звіряти округлене значення з тим, що лишилося в
    /// змінній .NET, — крихко. Унікальний <see cref="Guid"/> на кожен виклик
    /// <c>ClaimBatchAsync</c> дає однозначний спосіб дістати ПІСЛЯ
    /// атомарного <c>UPDATE</c> саме ті рядки, які захопив САМЕ ЦЕЙ виклик.
    /// </remarks>
    public Guid? ClaimToken { get; private set; }

    /// <summary>Фіксує успішну відправку.</summary>
    /// <param name="utcNow">Момент відправки.</param>
    public void MarkSent(DateTime utcNow)
    {
        State = "Sent";
        SentAt = utcNow;
        Error = null;
        Attempts++;
        ClaimedAt = null;
        ClaimToken = null;
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
    ///
    /// ⛔ Q-241: рядок приходить сюди захопленим (<c>State == "Sending"</c>)
    /// — без явного повернення в <c>"Pending"</c> (коли спроб ще лишилось)
    /// він застряг би в захопленні до спливання таймауту захоплення, хоча
    /// процес живий і просто не зміг надіслати САМЕ ЦЮ спробу.
    /// </remarks>
    public void MarkFailed(string error, int maxAttempts)
    {
        Attempts++;
        Error = error;
        State = Attempts >= maxAttempts ? "Failed" : "Pending";
        ClaimedAt = null;
        ClaimToken = null;
    }
}
