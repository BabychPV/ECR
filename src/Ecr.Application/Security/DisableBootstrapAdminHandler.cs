// src/Ecr.Application/Security/DisableBootstrapAdminHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Вимикає bootstrap-адміністратора, щойно з'явився активний доменний
/// (ФВ-6.18, D-97). **Не видаляє** — запис потрібен в аудиті.
/// </summary>
public sealed class DisableBootstrapAdminHandler(
    IUserStore users, IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Вимикає запис, якщо для цього настали умови.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>true</c> — запис справді вимкнено цим викликом.</returns>
    /// <remarks>
    /// Викликається після кожного призначення ролі, а не за розкладом: вікно
    /// між появою доменного адміністратора і вимкненням має бути якомога
    /// коротшим.
    /// </remarks>
    public async Task<bool> HandleAsync(CancellationToken ct)
    {
        var bootstrap = await users.FindBootstrapAdminAsync(ct).ConfigureAwait(false);
        if (bootstrap is not { IsActive: true })
        {
            return false;
        }

        // ⚠ Умова — АКТИВНИЙ ДОМЕННИЙ користувач із правом керувати
        // користувачами. «Просто є доменний користувач» означало б, що перший
        // рядовий співробітник вимикає адміністратора, і налаштовувати систему
        // стає нікому.
        var hasDomainAdmin = await users
            .HasActiveDomainAdminAsync(BootstrapAdmin.AdminPermission, ct).ConfigureAwait(false);
        if (!hasDomainAdmin)
        {
            return false;
        }

        bootstrap.DisableAsBootstrap();

        var now = clock.UtcNow;
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now,
                "BootstrapAdminDisabled",
                TargetUserId: bootstrap.Id,
                TargetRoleId: null,

                // ⛔ Ні пароля, ні хеша, ні штампа: подія фіксує ФАКТ, а не стан
                // облікового запису.
                DetailsJson: null,
                ChangedByUserId: currentUser.UserId ?? bootstrap.Id,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // IsBootstrapAdmin лишається як є: за ним потім видно, звідки взявся
        // перший адміністратор системи. Видалення стерло б цю відповідь.
        return true;
    }
}
