using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Спроба входу — вдала чи ні (ФВ-6.4a).
/// </summary>
/// <remarks>
/// ⚠ Пишеться і для **неіснуючого** користувача теж, під тим іменем, яке
/// ввели. Інакше підбір імен не лишав би сліду взагалі: журнал показував би
/// лише спроби проти справжніх записів, тобто рівно ту частину атаки, яка вже
/// вдалася наполовину.
///
/// ⛔ Ні пароля, ні його фрагмента, ні довжини тут немає і бути не може
/// (ФВ-6.11): журнал спроб читає ширше коло людей, ніж таблицю користувачів.
/// </remarks>
public sealed class LoginAttempt : Entity<long>
{
    private LoginAttempt() { }

    /// <summary>Фіксує спробу входу.</summary>
    /// <param name="userName">Ім'я, яке ввели.</param>
    /// <param name="provider">Провайдер входу.</param>
    /// <param name="isSuccess">Чи вдала спроба.</param>
    /// <param name="attemptedAt">Момент у UTC.</param>
    /// <param name="ipAddress">Адреса клієнта.</param>
    /// <param name="failReason">Причина відмови — <b>категорія</b>, не подробиці.</param>
    public LoginAttempt(
        string userName,
        AuthProvider provider,
        bool isSuccess,
        DateTime attemptedAt,
        string? ipAddress = null,
        string? failReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        UserName = userName;
        Provider = provider;
        IsSuccess = isSuccess;
        AttemptedAt = attemptedAt;
        IpAddress = ipAddress;
        FailReason = failReason;
    }

    /// <summary>Ім'я входу, як його ввели.</summary>
    public string UserName { get; private set; } = null!;

    /// <summary>Провайдер входу.</summary>
    public AuthProvider Provider { get; private set; }

    /// <summary>Чи вдала спроба.</summary>
    public bool IsSuccess { get; private set; }

    /// <summary>Адреса клієнта; <c>null</c>, якщо невідома.</summary>
    public string? IpAddress { get; private set; }

    /// <summary>Момент спроби в UTC.</summary>
    public DateTime AttemptedAt { get; private set; }

    /// <summary>Категорія відмови: <c>UnknownUser</c>, <c>BadPassword</c>, <c>LockedOut</c>.</summary>
    public string? FailReason { get; private set; }
}
