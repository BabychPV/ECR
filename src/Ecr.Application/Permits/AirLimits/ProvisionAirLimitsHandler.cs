// src/Ecr.Application/Permits/AirLimits/ProvisionAirLimitsHandler.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.RegistryImport;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// Makes sure the "Air Limits Import" permit type (<c>AIR_LIMITS</c>, tables <c>AIR_SOURCES</c> and <c>AIR_SOURCE_SUBSTANCES</c>) is
/// published and the built-in import profile exists. Idempotent: a published type is never edited by this call. Permissions
/// <c>Permit.EditType</c> and <c>Permit.PublishType</c> (checked by the permit-type handlers it drives).
/// </summary>
public sealed class ProvisionAirLimitsHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryImportProfileStore profiles,
    CreatePermitTypeHandler create,
    ReplacePermitTypeHandler replace,
    PublishPermitTypeHandler publish,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    AirLimitsSettings settings)
{
    /// <summary>Provisions the type and the profile.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsProvisionReport> HandleAsync(CancellationToken ct)
    {
        // Both permissions up front: an already provisioned type must not answer an unprivileged caller.
        await Security.PermissionCheck.RequireAsync(access, currentUser, CreatePermitTypeHandler.Permission, ct).ConfigureAwait(false);
        await Security.PermissionCheck.RequireAsync(access, currentUser, PublishPermitTypeHandler.Permission, ct).ConfigureAwait(false);

        var content = AirLimitsSchema.Content(settings);
        var type = await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, ct).ConfigureAwait(false);
        var created = false;

        if (type is null)
        {
            _ = await create.HandleAsync(
                new CreatePermitTypeRequest(AirLimitsSchema.TypeCode, content.NameL10n, content.Category), ct).ConfigureAwait(false);
            created = true;
            type = (await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, ct).ConfigureAwait(false))!;
        }

        if (type!.Status != PermitTypeStatus.Published)
        {
            var token = Convert.ToBase64String(type.RowVersion);
            var saved = await replace.HandleAsync(
                AirLimitsSchema.TypeCode,
                new ReplacePermitTypeRequest(token, content.NameL10n, content.Category, content.Tables, content.Rules), ct).ConfigureAwait(false);
            _ = await publish.HandleAsync(
                AirLimitsSchema.TypeCode, new PublishPermitTypeRequest("Air Limits Import: provisioning", saved.RowVersion), ct).ConfigureAwait(false);
            type = (await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, ct).ConfigureAwait(false))!;
        }

        var sources = await registries.FindDefinitionAsync(AirLimitsSchema.SourcesRegistry, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The sources registry is missing after publication.");

        var profileCreated = false;
        if (await profiles.FindAsync(sources.Id, AirLimitsSchema.ProfileCode, ct).ConfigureAwait(false) is null)
        {
            var userId = currentUser.UserId ?? throw new InvalidOperationException("No user.");
            profiles.Add(new RegistryImportProfile(
                sources.Id,
                EcrCode.Create(AirLimitsSchema.ProfileCode),
                new LocalizedText(new Dictionary<string, string>
                {
                    ["en"] = "ERA table 3.3 - emission parameters",
                    ["ru"] = "ЭРА, таблица 3.3 - параметры выбросов",
                    ["kz"] = "ЭРА, 3.3-кесте - шығарынды параметрлері",
                }),
                BuiltInImportProfiles.EraTable33.ToJson(),
                userId,
                clock.UtcNow));
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            profileCreated = true;
        }

        return new AirLimitsProvisionReport(
            AirLimitsSchema.TypeCode,
            created,
            type!.Status == PermitTypeStatus.Published,
            type.Version,
            AirLimitsSchema.SourcesRegistry,
            AirLimitsSchema.SubstancesRegistry,
            AirLimitsSchema.ProfileCode,
            profileCreated);
    }
}