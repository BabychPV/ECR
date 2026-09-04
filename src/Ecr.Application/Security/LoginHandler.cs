using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>Результат успішного входу — усе, що потрібно для cookie.</summary>
/// <param name="UserId">Ідентифікатор у нашій базі (<b>не</b> SID, D-86).</param>
/// <param name="UserName">Ім'я входу.</param>
/// <param name="DisplayName">Ім'я для показу.</param>
/// <param name="SecurityStamp">Штамп; перевіряється на кожен запит.</param>
/// <param name="MustChangePassword">Пароль виданий разово (ФВ-6.18).</param>
public sealed record LoginResult(
    int UserId, string UserName, string DisplayName, string SecurityStamp, bool MustChangePassword);

/// <summary>
/// Вхід локального користувача (ФВ-6.1, ФВ-6.4a).
/// </summary>
/// <remarks>
/// ⚠ Відповідь на «немає такого користувача» і «невірний пароль» **однакова**,
/// і однакова не лише за текстом, а й за часом: інакше ендпоінт стає засобом
/// перебору імен, а перебір імен — половина роботи зловмисника.
/// </remarks>
public sealed class LoginHandler(
    IUserStore users, IPasswordHasher hasher, IUnitOfWork uow, IClock clock)
{
    /// <summary>
    /// Хеш, об який «перевіряється» пароль неіснуючого користувача.
    /// </summary>
    /// <remarks>
    /// Однакове повідомлення без однакового часу — напівзахист: PBKDF2 з
    /// 210 000 ітерацій добре видно на графіку затримок, і невідповідь у
    /// 200 мс сама каже, що імені немає.
    /// </remarks>
    private string? _decoyHash;

    /// <summary>Виконує вхід.</summary>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="password">Пароль.</param>
    /// <param name="ipAddress">Адреса клієнта для журналу спроб.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Невірні дані — <c>ECR-AUTH-0401</c>.</exception>
    /// <exception cref="BusinessRuleException">Запис заблоковано — <c>ECR-AUTH-0423</c>.</exception>
    public async Task<LoginResult> HandleAsync(
        string userName, string password, string? ipAddress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var now = clock.UtcNow;
        var user = await users.FindByUserNameAsync(userName, ct).ConfigureAwait(false);

        if (user is null || user.Provider != AuthProvider.Local || !user.IsActive)
        {
            // Пароль однаково «перевіряється»: без цього відповідь на невідоме
            // ім'я приходила б помітно швидше.
            Decoy(password);
            await FailAsync(userName, "UnknownUser", ipAddress, now, ct).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        if (user.IsLockedOut(now))
        {
            await FailAsync(userName, "LockedOut", ipAddress, now, ct).ConfigureAwait(false);

            // ⚠ Тут відповідь НАВМИСНО відрізняється від невірного пароля:
            // законний власник має дізнатися, що запис заблоковано, інакше він
            // підбиратиме пароль, який давно правильний. Ціна відома — стан
            // «запис існує» стає видимим після вичерпання спроб (ФВ-6.4a).
            throw new BusinessRuleException(
                "ECR-AUTH-0423",
                "Обліковий запис тимчасово заблоковано після невдалих спроб входу.");
        }

        if (user.PasswordHash is null || !hasher.Verify(password, user.PasswordHash))
        {
            var policy = await users.GetPolicyAsync(user, ct).ConfigureAwait(false);
            var locked = user.RegisterFailedAttempt(policy.MaxFailedAttempts, policy.LockoutMinutes, now);

            await FailAsync(userName, locked ? "LockedOut" : "BadPassword", ipAddress, now, ct)
                .ConfigureAwait(false);

            throw InvalidCredentials();
        }

        user.RegisterSuccessfulLogin();
        users.RecordAttempt(new LoginAttempt(userName, AuthProvider.Local, true, now, ipAddress));
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new LoginResult(
            user.Id, user.UserName, user.DisplayName, user.SecurityStamp, user.MustChangePassword);
    }

    /// <summary>Знаходить або заводить доменного користувача за SID (ФВ-6.2).</summary>
    /// <param name="sid">SID із токена Windows.</param>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="displayName">Ім'я для показу.</param>
    /// <param name="ipAddress">Адреса клієнта.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Нижче рівня входу різниці між провайдерами немає ніде: обидва дають ту
    /// саму cookie і той самий <c>ICurrentUser</c> (ФВ-6.2).
    /// </remarks>
    public async Task<LoginResult> HandleWindowsAsync(
        string sid, string userName, string displayName, string? ipAddress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);

        var now = clock.UtcNow;
        var user = await users.FindByWindowsSidAsync(sid, ct).ConfigureAwait(false);

        if (user is null)
        {
            user = User.CreateDomain(userName, displayName, sid, now);
            users.Add(user);
        }

        if (!user.IsActive)
        {
            await FailAsync(userName, "Disabled", ipAddress, now, ct).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        user.RegisterSuccessfulLogin();
        users.RecordAttempt(new LoginAttempt(userName, AuthProvider.Windows, true, now, ipAddress));
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return new LoginResult(
            user.Id, user.UserName, user.DisplayName, user.SecurityStamp, MustChangePassword: false);
    }

    /// <summary>Записує невдалу спробу і зберігає зміни.</summary>
    private async Task FailAsync(
        string userName, string reason, string? ipAddress, DateTime now, CancellationToken ct)
    {
        // ⛔ У журнал іде КАТЕГОРІЯ відмови, а не подробиці: ні введеного
        // пароля, ні його довжини, ні фрагмента (ФВ-6.11).
        users.RecordAttempt(
            new LoginAttempt(userName, AuthProvider.Local, false, now, ipAddress, reason));

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>«Перевірка» пароля неіснуючого користувача — заради часу відповіді.</summary>
    private void Decoy(string password)
    {
        _decoyHash ??= hasher.Hash("decoy-for-constant-time-comparison");
        hasher.Verify(password ?? string.Empty, _decoyHash);
    }

    private static AccessDeniedException InvalidCredentials()
        => new("ECR-AUTH-0401", "Невірне ім'я користувача або пароль.");
}
