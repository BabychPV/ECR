// src/Ecr.Application/Security/ChangePasswordHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Security;

/// <summary>
/// Зміна власного пароля. Знімає <c>MustChangePassword</c> і **обов'язково**
/// крутить <c>SecurityStamp</c> (ФВ-6.7).
/// </summary>
public sealed class ChangePasswordHandler(
    IPasswordHasher hasher, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(string currentPassword, string newPassword, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) лише локальні облікові записи: доменний пароль не наш;\n" +
            "2) перевірити поточний пароль — навіть при MustChangePassword;\n" +
            "3) новий пароль за PasswordPolicy (довжина, блокування — ФВ-6.4a);\n" +
            "4) user.SetPassword: знімає прапорець і крутить SecurityStamp, тому " +
            "   всі інші сесії стають недійсними негайно;\n" +
            "5) ⛔ ні поточний, ні новий пароль не логувати і не класти в " +
            "   повідомлення помилки (ФВ-6.11) — це перевіряється тестом;\n" +
            "6) в аудит — факт зміни без значень.");
}
