// src/Ecr.Application/Ports/INotificationDispatchStore.cs
using Ecr.Domain.Entities.Notifications;

namespace Ecr.Application.Ports;

/// <summary>Подія, яку розсилають каналами (<c>BE-34</c>).</summary>
/// <param name="Kind">Подія матриці правил.</param>
/// <param name="Severity">Серйозність; правило пропускає події не нижчі за свою межу.</param>
/// <param name="EventKey">
/// Ключ дедуплікації: та сама подія — той самий ключ. Він же лягає в
/// <c>itg.NotificationDelivery.EventKey</c>.
/// </param>
/// <param name="Subject">Тема.</param>
/// <param name="Body">Текст — без стеків і без секретів (ФВ-6.11).</param>
public sealed record NotificationEvent(
    NotificationEventKind Kind, NotificationSeverity Severity, string EventKey, string Subject, string Body);

/// <summary>Те, що доставляють у канал.</summary>
/// <param name="Subject">Тема.</param>
/// <param name="Body">Текст.</param>
public sealed record NotificationMessage(string Subject, string Body);

/// <summary>Підсумок розсилки однієї події.</summary>
/// <param name="Sent">Скільки каналів прийняли.</param>
/// <param name="Failed">Скільки відмовили.</param>
/// <param name="Suppressed">Скільки пропущено дедуплікацією.</param>
public sealed record NotificationDispatchResult(int Sent, int Failed, int Suppressed);

/// <summary>
/// Знімок конфігурації розсилки: увімкнені канали і правила до них.
/// </summary>
/// <param name="Revision">
/// Мітка, за якою знімок кешується; інша конфігурація — інша мітка, тому явної
/// інвалідації немає (той самий прийом, що й <c>v{id}:r{rev}</c> у метаданих,
/// D-16).
/// </param>
/// <param name="Channels">Канали.</param>
/// <param name="Rules">Правила «подія × канал».</param>
public sealed record NotificationDispatchPlan(
    string Revision,
    IReadOnlyList<NotificationChannel> Channels,
    IReadOnlyList<NotificationRule> Rules)
{
    /// <summary>Канали, яким подія такої серйозності має піти.</summary>
    /// <param name="kind">Подія.</param>
    /// <param name="severity">Серйозність події.</param>
    /// <remarks>
    /// ⚠ Вимкнений канал не отримує нічого, навіть якщо правило на нього
    /// ввімкнене: інакше «вимкнути канал» означало б «вимкнути кожне правило
    /// окремо», і забутий перемикач слав би пошту після того, як канал
    /// вважають прибраним.
    /// </remarks>
    public IEnumerable<NotificationChannel> TargetsOf(
        NotificationEventKind kind, NotificationSeverity severity)
    {
        var matched = Rules
            .Where(r => r.EventKind == kind && r.Matches(severity))
            .Select(r => r.ChannelId)
            .ToHashSet();

        return Channels.Where(c => c.IsEnabled && matched.Contains(c.Id)).OrderBy(c => c.Id);
    }
}

/// <summary>
/// Дані розсилки: конфігурація каналів і журнал доставок (<c>BE-34</c>).
/// </summary>
/// <remarks>
/// ⛔ Окремо від <see cref="INotificationStore"/> навмисно: той обслуговує
/// ЕКРАН керування каналами (право, назви, секрет write-only), а цей —
/// РОЗСИЛКУ, яку виконує фонова задача без користувача. Спільний порт означав
/// би, що кожна зміна екрана зачіпає відправника й навпаки.
/// </remarks>
public interface INotificationDispatchStore
{
    /// <summary>Знімок конфігурації; кешується за ревізією.</summary>
    public Task<NotificationDispatchPlan> GetPlanAsync(CancellationToken ct);

    /// <summary>
    /// Чи ЙШЛА вже ця подія в цей канал УСПІШНО з моменту <paramref name="since"/>.
    /// </summary>
    /// <param name="channelId">Канал.</param>
    /// <param name="eventKey">Ключ події.</param>
    /// <param name="since">Початок вікна дедуплікації.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Рахуються лише <see cref="NotificationDeliveryStatus.Sent"/>. Невдала
    /// спроба вікна не закриває — ретраєм тут і є наступний прогін задачі;
    /// <see cref="NotificationDeliveryStatus.Suppressed"/> теж, інакше подія,
    /// що повторюється частіше за вікно, не пішла б НІКОЛИ: кожне придушення
    /// зсувало б вікно вперед.
    /// </remarks>
    public Task<bool> WasSentSinceAsync(int channelId, string eventKey, DateTime since, CancellationToken ct);

    /// <summary>Дописує рядок журналу доставок і фіксує його.</summary>
    /// <param name="delivery">Запис спроби.</param>
    /// <param name="ct">Скасування.</param>
    public Task AppendDeliveryAsync(NotificationDelivery delivery, CancellationToken ct);
}

/// <summary>
/// Доставка в КОНКРЕТНИЙ канал із бази.
/// </summary>
/// <remarks>
/// ⚠ Не плутати з <see cref="INotificationSender"/>: той — транспорт ПРОЦЕСУ
/// (`Smtp:*` у конфігурації), один на застосунок. Цей — транспорт КАНАЛУ, і
/// адресу з секретом бере із самого каналу.
/// </remarks>
public interface INotificationChannelSender
{
    /// <summary>Транспорт, який цей відправник обслуговує.</summary>
    public NotificationChannelKind Kind { get; }

    /// <summary>Надсилає повідомлення в канал; відмова — виняток.</summary>
    /// <param name="channel">Канал із секретом (ще захищеним).</param>
    /// <param name="message">Що надіслати.</param>
    /// <param name="ct">Скасування.</param>
    public Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct);
}
