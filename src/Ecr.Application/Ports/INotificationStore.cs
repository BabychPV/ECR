// src/Ecr.Application/Ports/INotificationStore.cs
using Ecr.Domain.Entities.Notifications;

namespace Ecr.Application.Ports;

/// <summary>Сховище каналів сповіщень (<c>sys_ecr.NotificationChannel</c>, <c>BE-33</c>).</summary>
public interface INotificationStore
{
    /// <summary>Усі канали за назвою; лише для читання.</summary>
    public Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(CancellationToken ct);

    /// <summary>Канал для зміни; <c>null</c> — такого немає.</summary>
    public Task<NotificationChannel?> FindChannelAsync(int id, CancellationToken ct);

    /// <summary>Чи зайнята назва іншим каналом.</summary>
    public Task<bool> IsChannelNameTakenAsync(string name, int? exceptChannelId, CancellationToken ct);

    /// <summary>Додає канал; зберігає <c>IUnitOfWork</c>.</summary>
    public void AddChannel(NotificationChannel channel);

    /// <summary>
    /// Прибирає канал РАЗОМ із його правилами (зовнішній ключ — <c>Restrict</c>);
    /// журнал доставок лишається. Повертає кількість прибраних правил.
    /// </summary>
    public Task<int> RemoveChannelWithRulesAsync(NotificationChannel channel, CancellationToken ct);

    /// <summary>
    /// Усі правила «подія × канал» — ВІДСТЕЖУВАНІ: той самий перелік і читається,
    /// і переписується заміною матриці. Правил одиниці (види подій × канали).
    /// </summary>
    public Task<IReadOnlyList<NotificationRule>> ListRulesAsync(CancellationToken ct);

    /// <summary>Додає правило; зберігає <c>IUnitOfWork</c>.</summary>
    public void AddRule(NotificationRule rule);

    /// <summary>Прибирає правила, яких у новій матриці немає.</summary>
    public void RemoveRules(IEnumerable<NotificationRule> rules);

    /// <summary>
    /// Сторінка журналу доставок, новіші першими, звужена фільтром.
    /// </summary>
    /// <remarks>
    /// ⛔ Повертає ПРОЄКЦІЮ, а не сутність: у журналі немає ні секрету каналу,
    /// ні тіла повідомлення, і тип відповіді це закріплює.
    ///
    /// ⚠ Фільтр застосовується В ЗАПИТІ, а не після вибірки: інакше сторінка на
    /// 50 рядків після звуження віддавала б два, і «більше немає» означало б
    /// «більше немає в цих п'ятдесяти».
    /// </remarks>
    public Task<Common.PagedResult<Notifications.NotificationDeliveryView>> ReadDeliveriesAsync(
        Common.CursorRequest page, Notifications.NotificationDeliveryFilter filter, CancellationToken ct);
}

/// <summary>Захист секрету каналу (пароль SMTP, URL вебхука) перед записом у базу.</summary>
/// <remarks>
/// ⛔ Зворотної дії в порту для API немає навмисно: жоден обробник <c>BE-33</c>
/// секрету не читає. Розшифровує лише відправник (<c>BE-34</c>).
/// </remarks>
public interface INotificationSecretProtector
{
    /// <summary>Повертає захищений блоб.</summary>
    public byte[] Protect(string secret);
}
