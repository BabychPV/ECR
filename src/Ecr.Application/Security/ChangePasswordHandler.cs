// src/Ecr.Application/Security/ChangePasswordHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Зміна власного пароля. Знімає <c>MustChangePassword</c> і **обов'язково**
/// крутить <c>SecurityStamp</c> (ФВ-6.7).
/// </summary>
public sealed class ChangePasswordHandler(
    IUserStore users,
    IPasswordHasher hasher,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Тип події аудиту: хибний чинний пароль при зміні пароля (S9).</summary>
    public const string PasswordChangeFailedEvent = "PasswordChangeFailed";

    /// <summary>Змінює пароль поточного користувача.</summary>
    /// <param name="currentPassword">Чинний пароль.</param>
    /// <param name="newPassword">Новий пароль.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(string currentPassword, string newPassword, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може змінювати пароль.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var user = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException(
                       "ECR-AUTH-0401", "Обліковий запис не знайдено.",
                       new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.accountMissing" });

        // Доменний пароль живе в каталозі, і міняти його звідси означало б
        // обіцяти те, чого система не робить.
        if (user.Provider != AuthProvider.Local)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", "Пароль доменного облікового запису змінюється засобами домену.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0403.domainPassword" });
        }

        var now = clock.UtcNow;

        // ⛔ S9: заблокований запис не перевіряє пароль і тут — блокування одне
        // на вхід і на зміну пароля. Інакше сеанс, що лишився відкритим,
        // продовжував би підбір чинного пароля повз блокування (ФВ-6.4a).
        if (user.IsLockedOut(now))
        {
            throw LoginHandler.Locked(user);
        }

        var policy = await users.GetPolicyAsync(user, ct).ConfigureAwait(false);

        // ⚠ Чинний пароль перевіряється НАВІТЬ при MustChangePassword. Інакше
        // будь-хто, хто дістався до сесії з разовим паролем, змінив би його на
        // свій — і законний власник залишився б без доступу.
        if (user.PasswordHash is null || !hasher.Verify(currentPassword, user.PasswordHash))
        {
            // ⛔ S9: хибний чинний пароль — це невдала спроба, як і на вході,
            // і рахується ТИМ САМИМ атомарним лічильником. Без цього відкритий
            // сеанс (залишений браузер, викрадена cookie) давав необмежений
            // підбір пароля, якого блокування входу не бачило.
            var outcome = await users.RegisterFailedAttemptAsync(
                user.Id, policy.MaxFailedAttempts, policy.LockoutMinutes, now, ct).ConfigureAwait(false);

            // ⛔ Подія-СПРОБА (C4): лишається в журналі незалежно від того,
            // чим скінчиться запит. У деталях — лише категорія, без пароля
            // (ФВ-6.11).
            await audit.WriteIndependentSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    PasswordChangeFailedEvent,
                    TargetUserId: user.Id,
                    TargetRoleId: null,
                    DetailsJson: outcome.LockedNow ? """{"reason":"LockedOut"}""" : """{"reason":"BadPassword"}""",
                    ChangedByUserId: user.Id,
                    CorrelationId: currentUser.CorrelationId),
                ct).ConfigureAwait(false);

            throw new AccessDeniedException(
                "ECR-AUTH-0401", "Чинний пароль не підходить.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.currentPasswordWrong" });
        }
        if (newPassword is null || newPassword.Length < policy.MinLength)
        {
            // ⚠ Код ОКРЕМИЙ від «пароль треба змінити» (`P-01`). Спільний
            // код означав два різні стани — «ще не міняв» і «спробував
            // невдало», — і клієнт не міг сказати користувачеві, що саме не
            // так із введеним паролем: він просто показував ту саму форму.
            //
            // ⛔ У деталях — ВИМОГА, а не введене значення: текст помилки йде
            // і в лог, і клієнту, а пароль там не має опинитися ніколи
            // (ФВ-6.11).
            throw new BusinessRuleException(
                "ECR-PWD-0422",
                $"Новий пароль коротший за {policy.MinLength} символів.",
                new Dictionary<string, object?>
                {
                    // ⚠ Рядком: резолвер підставляє в шаблон лише `string`.
                    ["messageKey"] = "err.ECR-PWD-0422.tooShort",
                    ["minLength"] = policy.MinLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // SetPassword знімає прапорець і крутить SecurityStamp, тому всі інші
        // сесії стають недійсними негайно.
        user.SetPassword(hasher.Hash(newPassword));

        // ⚠ S9: чинний пароль підтверджено — лічильник невдалих спроб з нуля,
        // так само як після вдалого входу (`RegisterSuccessfulLogin`). Інакше
        // хибні спроби ДО зміни (спільний із входом лічильник) лишалися б
        // висіти, і одна помилка вже з НОВИМ паролем блокувала б запис.
        // `Unlock`, бо сюди заблокований запис не доходить (423 вище) — тож
        // він лише обнуляє лічильник і нічого не знімає.
        user.Unlock();

        // ⛔ C4: подія й новий хеш — одним комітом.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        now,
                        "PasswordChanged",
                        TargetUserId: user.Id,
                        TargetRoleId: null,

                        // ⛔ В аудит іде ФАКТ зміни без значень: ні старого пароля, ні
                        // нового, ні хеша (ФВ-6.11). Аудит читають ширше коло людей,
                        // ніж базу.
                        DetailsJson: null,
                        ChangedByUserId: user.Id,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);

                await uow.SaveChangesAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }
}
