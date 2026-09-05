// src/Ecr.Infrastructure/Integration/NotificationOutboxStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Integration;

/// <summary>Черга сповіщень поверх <c>itg.NotificationOutbox</c>.</summary>
/// <remarks>
/// ⚠ Власного <c>SaveChanges</c> тут НЕМАЄ навмисно: подія має лягти тим самим
/// комітом, що й зміна, яка її породила. Інакше система пише лист про
/// подання, якого не відбулося.
/// </remarks>
public sealed class NotificationOutboxStore(EcrDbContext db, IClock clock) : INotificationOutbox
{
    /// <inheritdoc />
    public Task EnqueueAsync(
        string eventCode, string subject, string body, string? recipients, CancellationToken ct)
    {
        db.NotificationOutbox.Add(
            new NotificationOutboxItem(eventCode, subject, body, recipients, clock.UtcNow));

        return Task.CompletedTask;
    }
}
