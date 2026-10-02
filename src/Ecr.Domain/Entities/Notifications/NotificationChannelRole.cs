// src/Ecr.Domain/Entities/Notifications/NotificationChannelRole.cs
namespace Ecr.Domain.Entities.Notifications;

/// <summary>
/// Роль як адресат каналу сповіщень (<c>sys_ecr.NotificationChannelRole</c>, <c>D-263</c>): лист іде
/// активним користувачам цієї ролі, що мають адресу пошти, — не іменним особам.
/// </summary>
public sealed class NotificationChannelRole
{
    private NotificationChannelRole() { }

    /// <summary>Створює зв'язок «канал — роль».</summary>
    /// <param name="channelId">Канал.</param>
    /// <param name="roleId">Роль.</param>
    public NotificationChannelRole(int channelId, int roleId)
    {
        ChannelId = channelId;
        RoleId = roleId;
    }

    public int ChannelId { get; private set; }
    public int RoleId { get; private set; }
}
