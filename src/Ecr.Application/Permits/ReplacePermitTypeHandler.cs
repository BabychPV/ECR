// src/Ecr.Application/Permits/ReplacePermitTypeHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Замінює ЧЕРНЕТКУ визначення типу цілком: таблиці, стовпці, списки, перевірки (Permits, PM-03).
/// Право <c>Registry.EditDefinition</c>.
/// </summary>
/// <remarks>
/// ⛔ Реєстри тут не змінюються: чернетка - JSON у типі (<see cref="PermitTypeCodec"/>), реєстри оновить публікація
/// однією транзакцією. Тому «замінити чернетку» - один <c>UPDATE</c> типу, і токен <c>rowVersion</c> ловить
/// правку з двох вкладок. Опублікований тип без відкритої чернетки не змінюється: спершу нова версія.
/// </remarks>
public sealed class ReplacePermitTypeHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну опису довідників.</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Замінює чернетку.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="request">Повний склад і токен, прочитаний разом із визначенням.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4093</c>: токен чужий або немає відкритої чернетки.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>: склад не складається.</exception>
    public async Task<PermitTypeDto> HandleAsync(string code, ReplacePermitTypeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var type = await permits.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (type is null || RegistryAccess.IsDenied(profile, type.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(code);
        }

        if (type.Status == PermitTypeStatus.Published && !type.HasDraft)
        {
            throw PermitTypeErrors.NoDraft(type.Code);
        }

        PermitTypeErrors.RequireRowVersion(type, request.RowVersion);

        var content = PermitTypeAssembler.Normalize(new PermitTypeContent(
            request.NameL10n, request.Category, request.Tables ?? [], request.Rules ?? []));

        // Опублікований склад не можна зламати: стовпець не зникає, не міняє тип, ключ не змінюється.
        var published = type.Status == PermitTypeStatus.Published
            ? await new PermitTypeReader(registries, permits).ReadPublishedAsync(type, ct).ConfigureAwait(false)
            : null;

        PermitTypeDefinitionValidator.Validate(type.Code, content, published);
        await RequireSharedRegistriesAsync(content, ct).ConfigureAwait(false);

        type.SaveDraft(PermitTypeCodec.Serialize(content), userId, clock.UtcNow);

        try
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.CellConflict)
        {
            // Токен рядка розійшовся між перевіркою й записом: та сама відмова, що й при явній розбіжності.
            throw PermitTypeErrors.Changed(type.Code, type.RowVersion);
        }

        return PermitTypeAssembler.ToDto(type, content);
    }

    private async Task RequireSharedRegistriesAsync(PermitTypeContent content, CancellationToken ct)
    {
        foreach (var column in content.Tables.SelectMany(t => t.Columns)
                     .Where(c => c.ListScope == nameof(PermitListScope.SharedRegistry)))
        {
            if (await registries.FindDefinitionAsync(column.ListRegistryCode!, ct).ConfigureAwait(false) is null)
            {
                throw PermitTypeDefinitionValidator.SharedRegistryUnknown(column.Code, column.ListRegistryCode!);
            }
        }
    }
}

/// <summary>
/// Відкриває чернетку нової версії опублікованого типу: вона стартує з його поточного складу. Право
/// <c>Registry.EditDefinition</c> (Permits, PM-03).
/// </summary>
public sealed class OpenPermitTypeVersionHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну опису довідників.</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Відкриває нову версію.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4093</c>: тип не опублікований або чернетка вже є.</exception>
    public async Task<PermitTypeDto> HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var type = await permits.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (type is null || RegistryAccess.IsDenied(profile, type.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(code);
        }

        if (type.Status != PermitTypeStatus.Published)
        {
            throw PermitTypeErrors.NotPublished(type.Code);
        }

        if (type.HasDraft)
        {
            throw PermitTypeErrors.DraftExists(type.Code);
        }

        var content = await new PermitTypeReader(registries, permits).ReadPublishedAsync(type, ct).ConfigureAwait(false);
        type.OpenNewVersion(PermitTypeCodec.Serialize(content), userId, clock.UtcNow);

        try
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.CellConflict)
        {
            // Дві вкладки відкрили нову версію одночасно: друга бачить, що чернетка вже є.
            throw PermitTypeErrors.DraftExists(type.Code);
        }

        return PermitTypeAssembler.ToDto(type, content);
    }
}
