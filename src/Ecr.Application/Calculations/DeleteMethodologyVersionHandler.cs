// src/Ecr.Application/Calculations/DeleteMethodologyVersionHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Calculations;

/// <summary>
/// Видаляє версію-чернетку методології (<c>BE-25</c>, макет <c>mv-delete-draft</c>).
/// </summary>
/// <remarks>
/// Що можна видалити, вирішує домен (<c>MethodologyVersion.EnsureDeletable</c>);
/// обробник лише збирає факти під блокуванням і пише слід у журнал безпеки.
/// </remarks>
public sealed class DeleteMethodologyVersionHandler(
    IMethodologyVersionDeletionStore deletion,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// Право — те саме, що створює чернетку (<c>CreateMethodologyVersionHandler</c>):
    /// прибрати власну незавершену роботу не небезпечніше, ніж її почати.
    /// </summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string DeletedEventType = "MethodologyVersionDeleted";

    /// <summary>Видаляє чернетку.</summary>
    /// <param name="methodologyId">Методологія з адреси.</param>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-CALC-0404</c> — версії немає або вона чужа.</exception>
    /// <exception cref="DomainException"><c>ECR-CALC-0409</c> — не чернетка або нею вже рахували.</exception>
    public async Task HandleAsync(int methodologyId, int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized, "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var version = string.Empty;
        var children = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var locked = await deletion.LockAsync(methodologyVersionId, innerCt).ConfigureAwait(false);

            if (locked is not { } found || found.Version.MethodologyId != methodologyId)
            {
                throw VersionNotFound(methodologyVersionId);
            }

            found.Version.EnsureDeletable(found.UsedInCalculations);

            version = found.Version.Version;
            children = await deletion.DeleteAsync(methodologyVersionId, innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Журнал — ПІСЛЯ коміту: `IAuditWriter` пише власним підключенням
        // (той самий вибір, що в `DeleteDocumentHandler`).
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                DeletedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new { methodologyId, methodologyVersionId, version, children }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }

    /// <summary>404 версії методології з ключем повідомлення.</summary>
    /// <param name="methodologyVersionId">Версія, якої немає в цій методології.</param>
    /// <returns>Виняток для кидка.</returns>
    internal static NotFoundException VersionNotFound(int methodologyVersionId)
        => new(
            ErrorCodes.MethodologyVersionNotFound,
            $"Версії методології {methodologyVersionId} у цій методології немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0404.version",
                ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
            });
}
