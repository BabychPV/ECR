// src/Ecr.Infrastructure/Notifications/NotificationStore.cs
using Ecr.Application.Common;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Notifications;

/// <summary>Канали сповіщень поверх <see cref="EcrDbContext"/>.</summary>
public sealed class NotificationStore(EcrDbContext db) : INotificationStore
{
    /// <summary>Стеля переліку: каналів — одиниці, але запит без межі не йде в базу взагалі.</summary>
    public const int MaxChannels = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(CancellationToken ct)
        => await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).Take(MaxChannels)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<NotificationChannel?> FindChannelAsync(int id, CancellationToken ct)
        => db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <inheritdoc />
    public Task<bool> IsChannelNameTakenAsync(string name, int? exceptChannelId, CancellationToken ct)
        => db.NotificationChannels.AnyAsync(c => c.Name == name && c.Id != (exceptChannelId ?? 0), ct);

    /// <inheritdoc />
    public void AddChannel(NotificationChannel channel) => db.NotificationChannels.Add(channel);

    /// <inheritdoc />
    public async Task<int> RemoveChannelWithRulesAsync(NotificationChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var rules = await db.NotificationRules.Where(r => r.ChannelId == channel.Id).ToListAsync(ct).ConfigureAwait(false);
        db.NotificationRules.RemoveRange(rules);
        db.NotificationChannels.Remove(channel);

        return rules.Count;
    }

    /// <summary>Стеля матриці: види подій × <see cref="MaxChannels"/> каналів.</summary>
    public const int MaxRules = 1000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRule>> ListRulesAsync(CancellationToken ct)
        => await db.NotificationRules.OrderBy(r => r.EventKind).ThenBy(r => r.ChannelId).Take(MaxRules)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public void AddRule(NotificationRule rule) => db.NotificationRules.Add(rule);

    /// <inheritdoc />
    public void RemoveRules(IEnumerable<NotificationRule> rules) => db.NotificationRules.RemoveRange(rules);

    /// <inheritdoc />
    public async Task<PagedResult<NotificationDeliveryView>> ReadDeliveriesAsync(
        CursorRequest page, NotificationDeliveryFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(filter);

        // ⚠ Курсор іде ВНИЗ (`Id < before`), бо й порядок спадний: журнал
        // читають із кінця. Порожній курсор — `long.MaxValue`, а не 0: із нулем
        // перша сторінка була б порожня завжди (той самий підводний камінь, що
        // в `ConsistencyIssueReader`).
        var decoded = Cursor.Decode(page.Cursor);
        var before = decoded == 0 ? long.MaxValue : decoded;

        // ⚠ Локальні змінні, а не поля запису у виразі: так умова стає
        // параметром запиту, і план не залежить від того, чи фільтр заданий.
        var channelId = filter.ChannelId;
        var status = filter.Status;

        // ⚠ Беремо на рядок більше за сторінку — це й є ознака «є ще», без COUNT.
        var rows = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.Id < before)
            .Where(d => channelId == null || d.ChannelId == channelId)
            .Where(d => status == null || d.Status == status)
            .OrderByDescending(d => d.Id)
            .Take(page.Limit + 1)
            .Select(d => new
            {
                d.Id,
                d.At,
                d.ChannelId,
                d.EventKind,
                d.EventKey,
                d.Status,
                d.Error,

                // ⚠ Підзапит, а не `Join`: зовнішнього ключа на канал немає
                // навмисно (журнал переживає видалення каналу), тож назва тут
                // буває відсутня — і це не помилка даних.
                ChannelName = db.NotificationChannels
                    .Where(c => c.Id == d.ChannelId).Select(c => c.Name).FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows.Take(page.Limit)
            .Select(r => new NotificationDeliveryView(
                r.Id, DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.ChannelId, r.ChannelName,
                r.EventKind, r.EventKey, r.Status, r.Error))
            .ToList();

        return new PagedResult<NotificationDeliveryView>(
            items,
            rows.Count > page.Limit ? Cursor.Encode(items[^1].Id) : null,
            TotalCount: null);
    }
}

/// <summary>Секрет каналу під DataProtection з власним призначенням.</summary>
/// <remarks>
/// ⚠ Окремий purpose: блоб секрету не розшифрувати захисником cookie чи
/// антифорджері, хоч кільце ключів спільне (<c>MI-01</c>).
/// </remarks>
public sealed class DataProtectionNotificationSecretProtector(IDataProtectionProvider provider)
    : INotificationSecretProtector
{
    /// <summary>Призначення захисника; ним же розшифровуватиме відправник (<c>BE-34</c>).</summary>
    public const string Purpose = "Ecr.Notifications.ChannelSecret.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    /// <inheritdoc />
    public byte[] Protect(string secret) => _protector.Protect(System.Text.Encoding.UTF8.GetBytes(secret));
}
