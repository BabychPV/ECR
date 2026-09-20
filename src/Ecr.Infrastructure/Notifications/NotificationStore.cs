// src/Ecr.Infrastructure/Notifications/NotificationStore.cs
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
