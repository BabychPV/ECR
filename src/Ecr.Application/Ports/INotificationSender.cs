// src/Ecr.Application/Ports/INotificationSender.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Доставка сповіщення.
/// </summary>
/// <remarks>
/// ⚠ Порт існує окремо від черги (`P-13`), бо транспорт — рішення замовника:
/// SMTP, Teams або обидва. Черга потрібна за будь-якого вибору, доставка —
/// різна.
/// <para>
/// ⛔ Реалізації за замовчуванням **немає навмисно**. Заглушка, що мовчки
/// «доставляє», перетворила б чергу на місце, де листи зникають, і про це
/// дізналися б лише тоді, коли хтось не отримав повідомлення про закриття
/// періоду. Поки транспорт не налаштований, задача лишає події в черзі й
/// каже про це вголос.
/// </para>
/// </remarks>
public interface INotificationSender
{
    /// <summary>Чи налаштований транспорт.</summary>
    /// <remarks>
    /// Задача питає це ПЕРЕД відправкою, щоб не позначати події невдалими
    /// через відсутність налаштування: «не налаштовано» і «не доставлено» —
    /// різні стани, і плутати їх означає ховати перший за другим.
    /// </remarks>
    public bool IsConfigured { get; }

    /// <summary>Надсилає одне сповіщення.</summary>
    /// <param name="recipients">Адресати.</param>
    /// <param name="subject">Тема.</param>
    /// <param name="body">Текст.</param>
    /// <param name="ct">Скасування.</param>
    public Task SendAsync(
        IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct);
}
