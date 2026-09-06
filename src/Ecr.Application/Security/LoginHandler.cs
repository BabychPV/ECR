using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Microsoft.Extensions.Logging;

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
public sealed partial class LoginHandler(
    IUserStore users, IPasswordHasher hasher, IUnitOfWork uow, IClock clock, ILogger<LoginHandler> logger)
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
    /// <param name="groupSids">SID груп безпеки з квитка; для діагностики доступу.</param>
    /// <param name="ipAddress">Адреса клієнта.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Нижче рівня входу різниці між провайдерами немає ніде: обидва дають ту
    /// саму cookie і той самий <c>ICurrentUser</c> (ФВ-6.2).
    ///
    /// ⚠ <paramref name="groupSids"/> — параметр, а не читання
    /// <c>ICurrentUser</c> зсередини: на цьому шляху «поточний користувач» ще
    /// не наш, а Negotiate-принципал, і приховане читання зробило б вхід
    /// залежним від того, що стоїть у конвеєрі вище.
    /// </remarks>
    public async Task<LoginResult> HandleWindowsAsync(
        string sid,
        string userName,
        string displayName,
        IReadOnlyList<string> groupSids,
        string? ipAddress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ArgumentNullException.ThrowIfNull(groupSids);

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

        await WarnOnEmptyGroupMatchAsync(user.Id, userName, groupSids, now, ct).ConfigureAwait(false);

        return new LoginResult(
            user.Id, user.UserName, user.DisplayName, user.SecurityStamp, MustChangePassword: false);
    }

    /// <summary>
    /// Пише <c>Warning</c>, коли жоден SID групи з квитка не дав ролі.
    /// </summary>
    /// <param name="userId">Обліковий запис, який щойно увійшов.</param>
    /// <param name="userName">Ім'я входу — щоб рядок журналу був адресний.</param>
    /// <param name="groupSids">SID груп із квитка.</param>
    /// <param name="now">Момент входу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Саме <c>Warning</c>, а не помилка. Нуль збігів — <b>законний</b> стан
    /// для нового співробітника, якого ще не додали в жодну групу, і робити з
    /// нього відмову означало б не пускати в систему тих, кого вона має
    /// зустріти порожнім, але робочим екраном.
    ///
    /// ⛔ Але й не мовчання. `ФВ-6.15` призначає ролі доменних користувачів
    /// НА ГРУПУ; на живому домені без налаштованих груп нуль збігів дістанеться
    /// більшості, і виглядатиме це точнісінько як справна система без даних.
    /// Рядок у журналі — перше місце, де ця тиша стає видимою.
    ///
    /// ⚠ У рядку — <b>перелік SID, які не збіглися</b>, а не сам факт. Без
    /// переліку адміністраторові нема з чим іти до відділу AD: «у когось немає
    /// прав» — не запит, «група S-1-5-21-… нічого не дає» — запит.
    ///
    /// ⚠ Збігом вважається лише ЧИННЕ призначення: строкова підміна, яка
    /// скінчилася, доступу не дає, і рахувати її збігом означало б мовчати
    /// саме тоді, коли людина щойно втратила права.
    ///
    /// ⚠ Ціна — один індексований запит на ВХІД (не на запит). Профіль доступу
    /// будується тим самим набором рядків одразу після цього, тож ідеться про
    /// подвоєння того, що й так робиться раз на сесію.
    /// </remarks>
    private async Task WarnOnEmptyGroupMatchAsync(
        int userId, string userName, IReadOnlyList<string> groupSids, DateTime now, CancellationToken ct)
    {
        var assignments = await users
            .ListAssignmentsAsync(userId, groupSids, DateOnly.FromDateTime(now), ct)
            .ConfigureAwait(false);

        if (assignments.Any(a => a.IsEffective && a.PrincipalSid is not null))
        {
            return;
        }

        // ⚠ Порожній квиток пишеться окремим словом, а не порожнім переліком:
        // «SID: —» означає, що Negotiate не поклав у квиток жодної групи, і це
        // дефект налаштування контуру, а не адміністрування ролей.
        var sids = groupSids.Count == 0
            ? "—"
            : string.Join(", ", groupSids.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));

        LogNoGroupMatch(
            logger,
            userName,
            groupSids.Count,
            sids,
            assignments.Count(a => a.IsEffective && a.PrincipalSid is null));
    }

    /// <summary>Рядок журналу про вхід без жодного збігу за групами.</summary>
    /// <param name="logger">Журнал.</param>
    /// <param name="userName">Ім'я входу.</param>
    /// <param name="sidCount">Скільки SID груп було в квитку.</param>
    /// <param name="sids">Перелік SID, які не збіглися.</param>
    /// <param name="personalRoles">Скільки ролей призначено особисто.</param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Вхід {UserName}: жоден SID групи з квитка не дав ролі. "
                  + "SID у квитку: {SidCount} ({Sids}). Особистих призначень: {PersonalRoles}.")]
    private static partial void LogNoGroupMatch(
        ILogger logger, string userName, int sidCount, string sids, int personalRoles);

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
