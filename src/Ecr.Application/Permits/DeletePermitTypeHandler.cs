// src/Ecr.Application/Permits/DeletePermitTypeHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Видаляє ніколи не опублікований тип без дозволів разом з порожнім реєстром шапки. Право
/// <c>Permit.EditType</c> (Permits, PM-03).
/// </summary>
/// <remarks>
/// ⛔ Опублікований тип не видаляється ніколи: на нього посилаються реєстри з даними, прив'язки аркушів і документи.
/// Чернетка нової версії відкидається не видаленням типу, а наступним кроком (окремої дії поки немає).
/// </remarks>
public sealed class DeletePermitTypeHandler(
    IPermitTypeStore permits,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну типів дозволів.</summary>
    public const string Permission = "Permit.EditType";

    /// <summary>Видаляє тип.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-4094</c>: опублікований або має дозволи.</exception>
    public async Task HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var type = await permits.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (type is null || RegistryAccess.IsDenied(profile, type.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(code);
        }

        if (type.Status != PermitTypeStatus.Draft)
        {
            throw PermitTypeErrors.InUsePublished(type.Code);
        }

        var counts = await permits.CountPermitsAsync([type.HeaderRegistryDefId], ct).ConfigureAwait(false);
        if (counts.GetValueOrDefault(type.HeaderRegistryDefId) is > 0 and var permitCount)
        {
            throw PermitTypeErrors.InUseByPermits(type.Code, permitCount);
        }

        var typeId = type.Id;
        // Чернетку понад межу (PA-U-01) в аудит не пишемо: це ще десятки МБ в одному рядку журналу.
        var draft = PermitTypeCodec.IsOversized(type.DraftJson) ? null : type.DraftJson;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await permits.RemoveDraftTypeAsync(type, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "cfg.PermitTypeDef",
                    EntityId: typeId,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "DeletePermitType",
                    OldJson: draft,
                    NewJson: null,
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
