// src/Ecr.Domain/Entities/Notifications/NotificationChannel.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Notifications;

/// <summary>Транспорт каналу сповіщень. Числа зберігаються в базі — не перенумеровувати.</summary>
public enum NotificationChannelKind : byte
{
    /// <summary>Електронна пошта через SMTP.</summary>
    Smtp = 1,

    /// <summary>Вебхук Teams (Workflows / Power Automate, тіло — Adaptive Card).</summary>
    TeamsWebhook = 2,
}

/// <summary>
/// Канал сповіщень, налаштований в інтерфейсі (<c>sys_ecr.NotificationChannel</c>, <c>BE-32</c>).
/// </summary>
/// <remarks>
/// ⛔ Секрет (пароль SMTP, URL вебхука Teams) живе ЛИШЕ в
/// <see cref="SecretProtected"/> — уже зашифрованим блобом. Сутність його не
/// шифрує й не розшифровує: це робить шар, що володіє DataProtection
/// (<c>BE-33</c>). У <see cref="SettingsJson"/> секретів не буває — це поле
/// повертається API як є.
/// </remarks>
public sealed class NotificationChannel : Entity<int>
{
    /// <summary>Найбільша довжина назви; та сама, що в стовпці.</summary>
    public const int NameMaxLength = 100;

    private NotificationChannel() { }

    /// <summary>Створює ввімкнений канал без секрету.</summary>
    /// <param name="kind">Транспорт.</param>
    /// <param name="name">Назва, унікальна серед каналів.</param>
    /// <param name="settingsJson">Несекретні параметри (host, port, recipients… / заголовок картки).</param>
    /// <param name="utcNow">Момент створення, UTC.</param>
    /// <param name="byUserId">Хто створив.</param>
    public NotificationChannel(
        NotificationChannelKind kind, string name, string settingsJson, DateTime utcNow, int? byUserId)
    {
        Kind = kind;
        IsEnabled = true;
        Update(name, settingsJson, IsEnabled, utcNow, byUserId);
    }

    public NotificationChannelKind Kind { get; private set; }
    public string Name { get; private set; } = null!;
    public bool IsEnabled { get; private set; }

    /// <summary>Несекретні параметри каналу; форма залежить від <see cref="Kind"/>.</summary>
    public string SettingsJson { get; private set; } = null!;

    /// <summary>Захищений блоб секрету; <c>null</c> — секрет не задано.</summary>
    public byte[]? SecretProtected { get; private set; }

    /// <summary>Чи задано секрет — єдине, що про нього дозволено показувати.</summary>
    public bool HasSecret => SecretProtected is { Length: > 0 };

    public byte[] RowVersion { get; private set; } = [];
    public DateTime ModifiedAt { get; private set; }
    public int? ModifiedByUserId { get; private set; }

    /// <summary>Змінює несекретну частину каналу. Транспорт не змінюється: інший транспорт — інший канал.</summary>
    public void Update(string name, string settingsJson, bool isEnabled, DateTime utcNow, int? byUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameMaxLength);

        Name = name;
        SettingsJson = settingsJson;
        IsEnabled = isEnabled;
        Touch(utcNow, byUserId);
    }

    /// <summary>Замінює секрет уже ЗАХИЩЕНИМ блобом; <c>null</c> або порожній — прибирає.</summary>
    public void ReplaceSecret(byte[]? protectedSecret, DateTime utcNow, int? byUserId)
    {
        SecretProtected = protectedSecret is { Length: > 0 } ? protectedSecret : null;
        Touch(utcNow, byUserId);
    }

    private void Touch(DateTime utcNow, int? byUserId)
    {
        ModifiedAt = utcNow;
        ModifiedByUserId = byUserId;
    }
}
