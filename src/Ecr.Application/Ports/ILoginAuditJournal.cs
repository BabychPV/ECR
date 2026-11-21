using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Журнал спроб входу в журналі безпеки <c>aud.SecurityEvent</c> (PSS-24).
/// </summary>
/// <remarks>
/// ⛔ Ні пароля, ні його довжини, ні фрагмента, ні секретів: лише ім'я входу, результат, КОД причини, адреса й час.
/// Запис ніколи не кидає: збій журналу не має права ні зірвати вхід, ні змінити відповідь (інакше журнал робить те,
/// від чого мав захищати). Реалізація — <c>Ecr.Api.Security.LoginAuditJournal</c>.
/// </remarks>
public interface ILoginAuditJournal
{
    /// <summary>Записує подію входу; виняток не кидає.</summary>
    /// <param name="entry">Подія.</param>
    /// <param name="ct">Скасування; запис усе одно обмежений власним тайм-аутом.</param>
    public Task RecordAsync(LoginAuditEntry entry, CancellationToken ct);
}

/// <summary>Типи подій входу в <c>aud.SecurityEvent.EventType</c>.</summary>
public static class LoginAuditEventTypes
{
    /// <summary>Успішний вхід (локальний чи Windows).</summary>
    public const string Succeeded = "LoginSucceeded";

    /// <summary>Невдала спроба входу; причина — у <see cref="LoginAuditEntry.Reason"/>.</summary>
    public const string Failed = "LoginFailed";

    /// <summary>Обліковий запис заблоковано ЦІЄЮ спробою (вичерпано бюджет невдалих спроб).</summary>
    public const string AccountLocked = "AccountLocked";

    /// <summary>Підсумок пропущених обмежувачем флуду невдалих спроб (ключі, що більше не повертаються).</summary>
    public const string SuppressedSummary = "LoginFailedSuppressed";

    /// <summary>Таблиця ключів обмежувача флуду переповнена: частину невдалих спроб не записано (кількість — у подробицях).</summary>
    public const string AuditOverflow = "LoginAuditOverflow";

    /// <summary>
    /// <c>aud.SecurityEvent.ChangedByUserId</c> для невдалої спроби, блокування й службових подій журналу входів: людина
    /// не ввійшла, користувача-автора немає (колонка <c>NOT NULL</c>, а Id -1 не буває). Клієнт показує «Вхід не
    /// виконано» (<c>audit.anonymousActor</c>); кого стосується спроба — <c>TargetUserId</c>, ім'я — у подробицях.
    /// </summary>
    public const int AnonymousActorId = -1;
}

/// <summary>Подія входу для журналу безпеки.</summary>
/// <param name="EventType">Один із <see cref="LoginAuditEventTypes"/>.</param>
/// <param name="UserName">Ім'я входу, як його ввели (усічене до довжини колонки).</param>
/// <param name="Provider">Провайдер входу.</param>
/// <param name="Reason">Код причини (<c>UnknownUser</c>, <c>BadPassword</c>, <c>LockedOut</c>, …); для успіху — <c>null</c>.</param>
/// <param name="IpAddress">Адреса клієнта (та сама, що в <c>sec.LoginAttempt</c>); <c>null</c> — невідома.</param>
/// <param name="UserId">Наш <c>UserId</c>, якщо ім'я належить наявному запису; <c>null</c> — невідоме ім'я.</param>
/// <param name="At">Момент у UTC.</param>
/// <param name="SocketIp">Адреса сокета; пишеться в подробиці лише коли відрізняється від <paramref name="IpAddress"/> (за проксі).</param>
public sealed record LoginAuditEntry(
    string EventType,
    string UserName,
    AuthProvider Provider,
    string? Reason,
    string? IpAddress,
    int? UserId,
    DateTime At,
    string? SocketIp = null);
