// src/Ecr.Domain/Entities/Notifications/NotificationRule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Notifications;

/// <summary>Подія, про яку сповіщають. Числа зберігаються в базі — не перенумеровувати.</summary>
public enum NotificationEventKind : byte
{
    /// <summary>Фонова задача завершилась провалом.</summary>
    JobFailed = 1,

    /// <summary>Перевірка узгодженості знайшла розбіжності.</summary>
    ConsistencyIssuesFound = 2,

    /// <summary>Закінчуються наперед створені партиції.</summary>
    PartitionsRunningOut = 3,

    /// <summary>Збір із зовнішнього джерела не вдався.</summary>
    CollectionFailed = 4,

    /// <summary>Вивантаження не вдалося.</summary>
    ExportFailed = 5,
}

/// <summary>Серйозність події; правило пропускає події не нижчі за свою межу.</summary>
public enum NotificationSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Правило «подія → канал» (<c>sys_ecr.NotificationRule</c>, <c>BE-32</c>):
/// клітинка матриці «подія × канал», тому пара унікальна.
/// </summary>
public sealed class NotificationRule : Entity<int>
{
    private NotificationRule() { }

    /// <summary>Створює правило.</summary>
    public NotificationRule(
        NotificationEventKind eventKind, int channelId, NotificationSeverity minSeverity, bool isEnabled = true)
    {
        EventKind = eventKind;
        ChannelId = channelId;
        Update(minSeverity, isEnabled);
    }

    public NotificationEventKind EventKind { get; private set; }
    public int ChannelId { get; private set; }
    public NotificationSeverity MinSeverity { get; private set; }
    public bool IsEnabled { get; private set; }

    /// <summary>Змінює межу серйозності й стан; подія і канал — ідентичність правила, вони не змінюються.</summary>
    public void Update(NotificationSeverity minSeverity, bool isEnabled)
    {
        MinSeverity = minSeverity;
        IsEnabled = isEnabled;
    }

    /// <summary>Чи спрацьовує правило на подію такої серйозності.</summary>
    public bool Matches(NotificationSeverity severity) => IsEnabled && severity >= MinSeverity;
}
