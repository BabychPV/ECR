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
    /// <summary>Змінює пароль поточного користувача.</summary>
    /// <param name="currentPassword">Чинний пароль.</param>
    /// <param name="newPassword">Новий пароль.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(string currentPassword, string newPassword, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може змінювати пароль.");

        var user = await users.FindByIdAsync(userId, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException("ECR-AUTH-0401", "Обліковий запис не знайдено.");

        // Доменний пароль живе в каталозі, і міняти його звідси означало б
        // обіцяти те, чого система не робить.
        if (user.Provider != AuthProvider.Local)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", "Пароль доменного облікового запису змінюється засобами домену.");
        }

        // ⚠ Чинний пароль перевіряється НАВІТЬ при MustChangePassword. Інакше
        // будь-хто, хто дістався до сесії з разовим паролем, змінив би його на
        // свій — і законний власник залишився б без доступу.
        if (user.PasswordHash is null || !hasher.Verify(currentPassword, user.PasswordHash))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0401", "Чинний пароль не підходить.");
        }

        var policy = await users.GetPolicyAsync(user, ct).ConfigureAwait(false);
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
                new Dictionary<string, object?> { ["minLength"] = policy.MinLength });
        }

        // SetPassword знімає прапорець і крутить SecurityStamp, тому всі інші
        // сесії стають недійсними негайно.
        user.SetPassword(hasher.Hash(newPassword));

        var now = clock.UtcNow;
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
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
