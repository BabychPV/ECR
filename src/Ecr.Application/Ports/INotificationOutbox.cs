// src/Ecr.Application/Ports/INotificationOutbox.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Черга сповіщень (<c>itg.NotificationOutbox</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт з'явився після `A7-31`. Черга, відправник і задача, яка її розбирає,
/// існували від Етапу 5 — а <b>покласти в неї подію не міг ніхто</b>: у всій
/// системі таблиця лише читалася. Жодне сповіщення не надсилалося ніколи, і
/// дізнатися про це можна було тільки з мовчання.
///
/// ⚠ Подія кладеться ТИМ САМИМ комітом, що й сама зміна: доставка листа не має
/// бути умовою збереження даних, але й губитися між збереженням і відправкою
/// вона не має.
/// </remarks>
public interface INotificationOutbox
{
    /// <summary>Кладе подію в чергу.</summary>
    /// <param name="eventCode">Що сталося: <c>sheet.submitted</c>, <c>sheet.approved</c>.</param>
    /// <param name="subject">Тема листа.</param>
    /// <param name="body">Текст; уже локалізований на момент постановки.</param>
    /// <param name="recipients">Адресати через кому; <c>null</c> — визначить політика при відправці.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task EnqueueAsync(
        string eventCode, string subject, string body, string? recipients, CancellationToken ct);
}
