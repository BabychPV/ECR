// src/Ecr.Application/Permits/CreatePermitTypeHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Заводить тип дозволу в стані чернетки: реєстр шапки (порожній, темпоральний) і рядок типу з чернеткою складу.
/// Право <c>Registry.EditDefinition</c> (Permits, PM-03).
/// </summary>
/// <remarks>
/// ⚠ Поля шапки й таблиці з'являються в реєстрах лише при публікації (одна транзакція): до того реєстр шапки порожній
/// і з переліку «Довідники» прихований (<see cref="IPermitTypeStore.ListHiddenRegistryIdsAsync"/>).
/// </remarks>
public sealed class CreatePermitTypeHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну опису довідників (типи — надбудова над ними).</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Заводить тип.</summary>
    /// <param name="request">Код, назва, категорія, за потреби — тип, склад якого копіюється.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-4091</c> код зайнятий; <c>ECR-PRM-0422</c> назва, категорія, довжина коду.</exception>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>: типу-джерела копії немає.</exception>
    public async Task<PermitTypeDto> HandleAsync(CreatePermitTypeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var code = EcrCode.Create(request.Code);
        var category = PermitTypeDefinitionValidator.ValidateDetails(request.NameL10n, request.Category);
        PermitTypeDefinitionValidator.ValidateTypeCode(code.Value);

        if (await permits.FindByCodeAsync(code.Value, ct).ConfigureAwait(false) is not null)
        {
            throw PermitTypeErrors.Duplicate(code.Value);
        }

        var headerCode = PermitHeaderFields.HeaderRegistryCode(code.Value);
        if (await registries.FindDefinitionAsync(headerCode, ct).ConfigureAwait(false) is not null)
        {
            throw PermitTypeErrors.RegistryCodeTaken(headerCode);
        }

        var content = await CopiedOrEmptyAsync(request, profile, ct).ConfigureAwait(false);
        var name = new LocalizedText(new Dictionary<string, string>(request.NameL10n, StringComparer.OrdinalIgnoreCase));
        PermitTypeDef? type = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // Шапка існує з першого дня: на неї посилається рядок типу. Поля — при публікації.
            var header = new RegistryDef(EcrCode.Create(headerCode), name, isTemporal: true);
            registries.AddDefinition(header);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            type = new PermitTypeDef(code, name, category, header.Id);
            type.SaveDraft(PermitTypeCodec.Serialize(content with { NameL10n = request.NameL10n, Category = request.Category }), userId, clock.UtcNow);
            permits.Add(type);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "cfg.PermitTypeDef",
                    EntityId: type.Id,
                    ChangeClass: ChangeClass.Safe,
                    Operation: "CreatePermitType",
                    OldJson: null,
                    NewJson: type.DraftJson,
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return PermitTypeAssembler.ToDto(type!, PermitTypeCodec.Parse(type!.DraftJson!));
    }

    /// <summary>Склад копії іншого типу (без прихованих реєстрів його списків) або порожній.</summary>
    private async Task<PermitTypeContent> CopiedOrEmptyAsync(
        CreatePermitTypeRequest request, Security.AccessProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CopyFromCode))
        {
            return new PermitTypeContent(request.NameL10n, request.Category, [], []);
        }

        var source = await permits.FindByCodeAsync(request.CopyFromCode, ct).ConfigureAwait(false);
        if (source is null || RegistryAccess.IsDenied(profile, source.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(request.CopyFromCode);
        }

        var shown = await PermitTypeAssembler
            .ShownAsync(source, new PermitTypeReader(registries, permits), ct).ConfigureAwait(false);

        // ⚠ Список «лише для типу» копіюється значеннями, а не посиланням на прихований реєстр джерела:
        // у копії він свій, код реєстру дасть публікація.
        var tables = shown.Tables.Select(t => t with
        {
            Columns = [.. t.Columns.Select(c => c.ListScope == nameof(PermitListScope.TypeOnly) ? c with { ListRegistryCode = null } : c)],
        }).ToList();

        return new PermitTypeContent(request.NameL10n, request.Category, tables, shown.Rules);
    }
}
