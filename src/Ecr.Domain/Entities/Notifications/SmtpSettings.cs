// src/Ecr.Domain/Entities/Notifications/SmtpSettings.cs
using System.Net.Mail;
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Notifications;

/// <summary>Шифрування з'єднання з поштовим сервером. Числа лежать у базі — не перенумеровувати.</summary>
public enum SmtpEncryptionMode : byte
{
    /// <summary>Без шифрування.</summary>
    None = 0,

    /// <summary>STARTTLS на звичайному порту (587).</summary>
    /// <remarks>⚠ Неявного TLS (порт 465) тут немає свідомо: <c>System.Net.Mail.SmtpClient</c> його не вміє, а пакет поштового клієнта додає лише інтегратор.</remarks>
    StartTls = 1,
}

/// <summary>Автентифікація на поштовому сервері.</summary>
public enum SmtpAuthMode : byte
{
    /// <summary>Без облікових даних (інтегрована або анонімна відправка).</summary>
    None = 0,

    /// <summary>Логін і пароль.</summary>
    Password = 1,
}

/// <summary>
/// Налаштування SMTP, задані адміністратором у системі (<c>sys_ecr.SmtpSettings</c>, <c>D-263</c>).
/// Єдиний рядок (<see cref="SingletonId"/>).
/// </summary>
/// <remarks>
/// ⛔ Пароль живе ЛИШЕ в <see cref="PasswordProtected"/> — уже зашифрованому блобі (шифрує шар, що
/// володіє DataProtection). Сутність не віддає його нікуди: назовні — лише <see cref="HasPassword"/>.
/// </remarks>
public sealed class SmtpSettings : Entity<int>
{
    /// <summary>Ключ єдиного рядка.</summary>
    public const int SingletonId = 1;

    /// <summary>Межі довжини полів; ті самі, що в стовпцях.</summary>
    public const int HostMaxLength = 255;
    public const int AddressMaxLength = 254;
    public const int NameMaxLength = 100;

    private SmtpSettings() { }

    /// <summary>Створює порожній (вимкнений) рядок налаштувань.</summary>
    /// <param name="utcNow">Момент створення, UTC.</param>
    /// <param name="byUserId">Хто створив.</param>
    public SmtpSettings(DateTime utcNow, int? byUserId)
    {
        Id = SingletonId;
        Host = string.Empty;
        Port = 587;
        EncryptionMode = SmtpEncryptionMode.StartTls;
        FromAddress = string.Empty;
        Touch(utcNow, byUserId);
    }

    public string Host { get; private set; } = null!;
    public int Port { get; private set; }
    public SmtpEncryptionMode EncryptionMode { get; private set; }
    public string FromAddress { get; private set; } = null!;
    public string? FromName { get; private set; }
    public SmtpAuthMode AuthMode { get; private set; }
    public string? UserName { get; private set; }
    public byte[]? PasswordProtected { get; private set; }

    /// <summary>Чи вимкнено. Вимкнені налаштування не діють: транспорт береться з конфігурації процесу.</summary>
    public bool IsEnabled { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
    public DateTime UpdatedAt { get; private set; }
    public int? UpdatedByUserId { get; private set; }

    /// <summary>Чи заданий пароль — єдине, що про нього дозволено показувати.</summary>
    public bool HasPassword => PasswordProtected is { Length: > 0 };

    /// <summary>Чи достатньо налаштувань, щоб слати пошту.</summary>
    public bool IsComplete => IsEnabled && Host.Length > 0 && FromAddress.Length > 0;

    /// <summary>Чи є адреса коректною поштовою адресою.</summary>
    /// <param name="address">Адреса.</param>
    public static bool IsValidAddress(string? address)
        => !string.IsNullOrWhiteSpace(address) && address.Length <= AddressMaxLength
           && MailAddress.TryCreate(address, out var parsed) && parsed.Address == address.Trim();

    /// <summary>Замінює все, крім пароля.</summary>
    public void Update(
        string host, int port, SmtpEncryptionMode encryption, string fromAddress, string? fromName,
        SmtpAuthMode auth, string? userName, bool isEnabled, DateTime utcNow, int? byUserId)
    {
        Host = host;
        Port = port;
        EncryptionMode = encryption;
        FromAddress = fromAddress;
        FromName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim();
        AuthMode = auth;
        UserName = auth == SmtpAuthMode.Password && !string.IsNullOrWhiteSpace(userName) ? userName.Trim() : null;
        IsEnabled = isEnabled;

        if (auth == SmtpAuthMode.None)
        {
            PasswordProtected = null;
        }

        Touch(utcNow, byUserId);
    }

    /// <summary>Замінює пароль УЖЕ зашифрованим блобом; <c>null</c> — прибирає.</summary>
    public void ReplacePassword(byte[]? protectedPassword, DateTime utcNow, int? byUserId)
    {
        PasswordProtected = protectedPassword is { Length: > 0 } ? protectedPassword : null;
        Touch(utcNow, byUserId);
    }

    private void Touch(DateTime utcNow, int? byUserId)
    {
        UpdatedAt = utcNow;
        UpdatedByUserId = byUserId;
    }
}
