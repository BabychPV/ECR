// tests/Ecr.Application.Tests/Notifications/FakeNotificationStore.cs
using Ecr.Application.Common;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;

namespace Ecr.Application.Tests.Notifications;

/// <summary>
/// Сховище сповіщень у пам'яті — спільне для тестів каналів і правил
/// (<c>BE-33</c>).
/// </summary>
/// <remarks>
/// ⚠ Спільне навмисно: два майже однакові подвійники розходяться на третьому
/// методі, і тоді один із двох наборів перевіряє вже не ту поведінку.
/// </remarks>
internal sealed class FakeNotificationStore : INotificationStore
{
    public List<NotificationChannel> Channels { get; } = [];

    public List<NotificationRule> Rules { get; } = [];

    public List<NotificationDeliveryView> Deliveries { get; } = [];

    /// <summary>Скільки правил «прибрало» видалення каналу.</summary>
    public int RuleCount { get; set; }

    public Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NotificationChannel>>(Channels);

    public Task<NotificationChannel?> FindChannelAsync(int id, CancellationToken ct)
        => Task.FromResult(Channels.Find(c => c.Id == id));

    public Task<bool> IsChannelNameTakenAsync(string name, int? exceptChannelId, CancellationToken ct)
        => Task.FromResult(Channels.Exists(c => c.Name == name && c.Id != exceptChannelId));

    public void AddChannel(NotificationChannel channel)
    {
        typeof(NotificationChannel).GetProperty(nameof(NotificationChannel.Id))!.SetValue(channel, Channels.Count + 1);
        Channels.Add(channel);
    }

    public Task<int> RemoveChannelWithRulesAsync(NotificationChannel channel, CancellationToken ct)
    {
        Channels.Remove(channel);

        return Task.FromResult(RuleCount);
    }

    public Task<IReadOnlyList<NotificationRule>> ListRulesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NotificationRule>>([.. Rules]);

    public void AddRule(NotificationRule rule) => Rules.Add(rule);

    /// <remarks>
    /// ⚠ Порівняння за ПОСИЛАННЯМ, не через <c>Contains</c>: <c>Entity.Equals</c>
    /// віддає <c>false</c> усьому незбереженому (<c>IsPersisted</c>), тож
    /// подвійник із нульовими ідентифікаторами не видалив би нічого — і тест на
    /// ідемпотентність падав би з вини подвійника, а не коду.
    /// </remarks>
    public void RemoveRules(IEnumerable<NotificationRule> rules)
    {
        var doomed = rules.ToList();

        Rules.RemoveAll(r => doomed.Exists(d => ReferenceEquals(d, r)));
    }

    /// <summary>Сторінка журналу: курсор у подвійнику не потрібен — його стереже тест бази.</summary>
    public Task<PagedResult<NotificationDeliveryView>> ReadDeliveriesAsync(CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        return Task.FromResult(new PagedResult<NotificationDeliveryView>(
            [.. Deliveries.Take(page.Limit)], NextCursor: null, TotalCount: null));
    }
}
