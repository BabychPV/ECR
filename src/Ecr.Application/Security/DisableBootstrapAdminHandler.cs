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
    /// Викликається після кожного призначення ролі і після кожного Windows-входу,
    /// а не за розкладом: вікно між появою доменного адміністратора і
    /// вимкненням має бути якомога коротшим.
    /// </remarks>
    public Task<bool> HandleAsync(CancellationToken ct) => HandleAsync(actorUserId: null, ct);

    /// <summary>Те саме, з явним автором події (Windows-вхід: поточного користувача ще немає).</summary>
    /// <param name="actorUserId">Хто спричинив вимкнення; <c>null</c> — поточний користувач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>true</c> — запис справді вимкнено цим викликом.</returns>
    /// <remarks>
    /// ⛔ S1-01 (аудит 5): адміністратором рахується лише доменний запис, що вже
    /// входив (`IUserStore.HasActiveDomainAdminAsync`). Тож призначення ролі
    /// записові, який ще не входив, bootstrap НЕ вимикає — його вимикає перший
    /// Windows-вхід такого адміністратора (`AuthController.LoginWindows`).
    /// </remarks>
    public async Task<bool> HandleAsync(int? actorUserId, CancellationToken ct)
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
        var now = clock.UtcNow;
        var hasDomainAdmin = await users
            .HasActiveDomainAdminAsync(BootstrapAdmin.AdminPermission, now, ct).ConfigureAwait(false);
        if (!hasDomainAdmin)
        {
            return false;
        }

        bootstrap.DisableAsBootstrap();

        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                now,
                "BootstrapAdminDisabled",
                TargetUserId: bootstrap.Id,
                TargetRoleId: null,

                // ⛔ Ні пароля, ні хеша, ні штампа: подія фіксує ФАКТ, а не стан
                // облікового запису.
                DetailsJson: null,
                ChangedByUserId: actorUserId ?? currentUser.UserId ?? bootstrap.Id,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // IsBootstrapAdmin лишається як є: за ним потім видно, звідки взявся
        // перший адміністратор системи. Видалення стерло б цю відповідь.
        return true;
    }
}
