// src/Ecr.Application/Permits/AirLimits/ProvisionAirLimitsHandler.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.RegistryImport;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// Makes sure the «Air emission permit» type <c>AIR</c> carries the tables <c>AIR_SOURCES</c> and <c>AIR_SOURCE_SUBSTANCES</c> (✎ AIR-ON-AIR:
/// appended to the existing type as a new version; its other tables untouched), the Area registry exists and the built-in import
/// profile exists. Idempotent: a type that already has both tables is not edited by this call. Permissions
/// <c>Permit.EditType</c> and <c>Permit.PublishType</c> (checked by the permit-type handlers it drives).
/// </summary>
public sealed partial class ProvisionAirLimitsHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryImportProfileStore profiles,
    CreatePermitTypeHandler create,
    ReplacePermitTypeHandler replace,
    PublishPermitTypeHandler publish,
    OpenPermitTypeVersionHandler openVersion,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    AirLimitsSettings settings,
    Microsoft.Extensions.Logging.ILogger<ProvisionAirLimitsHandler>? logger = null)
{
    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "AIR provisioning: the Region registry {RegistryCode} does not exist; the REGION column is published as Text (free text). Create the registry and switch the column in a new version of the AIR type if needed.")]
    private static partial void LogRegionAsText(Microsoft.Extensions.Logging.ILogger logger, string registryCode);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "AIR provisioning skipped: permit type {TypeCode} has an open draft; the sources tables are added once the draft is published or discarded (POST /api/v1/air-limits/provision).")]
    private static partial void LogDraftOpen(Microsoft.Extensions.Logging.ILogger logger, string typeCode);

    /// <summary>Provisions the type and the profile.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsProvisionReport> HandleAsync(CancellationToken ct)
    {
        // Both permissions up front: an already provisioned type must not answer an unprivileged caller.
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, CreatePermitTypeHandler.Permission, ct).ConfigureAwait(false);
        await Security.PermissionCheck.RequireAsync(access, currentUser, PublishPermitTypeHandler.Permission, ct).ConfigureAwait(false);
        var userId = currentUser.UserId ?? throw new InvalidOperationException("No user.");
        return await ProvisionAsync(new PermitTypeActor(userId, profile, currentUser.CorrelationId), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ✎ AIRSYS01: the same provisioning for an author already checked by the caller, or for the SYSTEM (deploy-time, like the
    /// seed — only <see cref="AirTypeSystemProvisioner"/>, which only the startup step calls).
    /// </summary>
    internal async Task<AirLimitsProvisionReport> ProvisionAsync(PermitTypeActor actor, CancellationToken ct)
    {
        // Prerequisites FIRST (Permites, fresh stand): the shared registries the tables point at. Before this check a missing
        // registry failed inside the replace step and left a draft type behind. The Area registry AIR_AREA is ours (created below
        // when absent).
        foreach (var (column, registryCode) in new[]
                 {
                     (AirLimitsSchema.Nae, settings.NaeRegistryCode),
                     (AirLimitsSchema.Substance, settings.SubstanceRegistryCode),
                 })
        {
            if (await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false) is null)
            {
                throw PermitTypeDefinitionValidator.SharedRegistryUnknown(column, registryCode);
            }
        }

        // ✎ AIR-ON-AIR (coordinator 02.11): the Region registry belongs to the Land bootstrap and is NOT created here. Missing →
        // the REGION column is published as Text (free text, still editable later through a new type version) with a warning,
        // not a refusal.
        var regionAsText = await registries.FindDefinitionAsync(settings.RegionRegistryCode, ct).ConfigureAwait(false) is null;
        if (regionAsText && logger is not null)
        {
            LogRegionAsText(logger, settings.RegionRegistryCode);
        }

        var content = AirLimitsSchema.Content(settings, regionAsText);
        var created = false;
        var profileCreated = false;
        PermitTypeDef? type = null;

        // ONE transaction: the Area registry, create / new version, replace, publish and the profile commit together or not at all.
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                created = false;
                profileCreated = false;
                await EnsureAreaRegistryAsync(actor, token).ConfigureAwait(false);
                type = await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, token).ConfigureAwait(false);

                if (type is null)
                {
                    // A stand without the Land «Air emission permit» type: AIR is created with the sources tables only.
                    _ = await create.ApplyAsync(
                        new CreatePermitTypeRequest(AirLimitsSchema.TypeCode, content.NameL10n, content.Category), actor, token).ConfigureAwait(false);
                    created = true;
                    type = (await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, token).ConfigureAwait(false))!;
                }

                if (type!.Status != PermitTypeStatus.Published)
                {
                    // Never published: its draft (if any) is the base; the sources tables are appended, nothing else changes.
                    var draft = type.DraftJson is { Length: > 0 } json ? PermitTypeCodec.Parse(json) : null;
                    var merged = draft is null ? content : AirLimitsSchema.WithSourcesTables(draft, settings, regionAsText) ?? draft;
                    await ReplaceAndPublishAsync(type, merged, actor, token).ConfigureAwait(false);
                }
                else
                {
                    // ✎ AIR-ON-AIR: the live AIR type (with «Emission limits») gets a NEW VERSION with the sources tables appended;
                    // its other tables and rules are untouched. Already there → nothing to do (an admin may have reconfigured
                    // REGION / AREA; that is respected).
                    var published = await new PermitTypeReader(registries, permits).ReadPublishedAsync(type, token).ConfigureAwait(false);
                    if (AirLimitsSchema.WithSourcesTables(published, settings, regionAsText) is { } merged)
                    {
                        if (type.HasDraft)
                        {
                            // An open draft of someone else is neither published under them nor overwritten: skipped with a clear
                            // log line (coordinator 02.11); the call is repeated once the draft is published or discarded.
                            if (logger is not null)
                            {
                                LogDraftOpen(logger, type.Code);
                            }

                            return;
                        }

                        _ = await openVersion.ApplyAsync(AirLimitsSchema.TypeCode, actor, token).ConfigureAwait(false);
                        type = (await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, token).ConfigureAwait(false))!;
                        await ReplaceAndPublishAsync(type, merged, actor, token).ConfigureAwait(false);
                    }
                }

                type = (await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, token).ConfigureAwait(false))!;

                var sources = await registries.FindDefinitionAsync(AirLimitsSchema.SourcesRegistry, token).ConfigureAwait(false)
                              ?? throw new InvalidOperationException("The sources registry is missing after publication.");

                if (await profiles.FindAsync(sources.Id, AirLimitsSchema.ProfileCode, token).ConfigureAwait(false) is null)
                {
                    var userId = actor.AuditUserId;
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
                    await uow.SaveChangesAsync(token).ConfigureAwait(false);
                    profileCreated = true;
                }
            },
            ct).ConfigureAwait(false);

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

    private async Task ReplaceAndPublishAsync(PermitTypeDef type, PermitTypeContent content, PermitTypeActor actor, CancellationToken ct)
    {
        var saved = await replace.ApplyAsync(
            AirLimitsSchema.TypeCode,
            new ReplacePermitTypeRequest(Convert.ToBase64String(type.RowVersion), content.NameL10n, content.Category, content.Tables, content.Rules),
            actor,
            ct).ConfigureAwait(false);
        _ = await publish.ApplyAsync(
            AirLimitsSchema.TypeCode, new PublishPermitTypeRequest("Air emission parameters: provisioning", saved.RowVersion), actor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ✎ AIR-ON-AIR (human 02.11: «тільки Offshore/Onshore створи новий дикшионари»): the Area registry with OFFSHORE / ONSHORE when
    /// it does not exist. Idempotent: an existing registry (and whatever an admin added to it) is left as it is.
    /// </summary>
    /// <remarks>
    /// ⚠ Here and not in <c>seed/features/*.sql</c>: a feature seed file may hold only UI strings (<c>#I18N</c>/<c>#UiChanged</c>/
    /// <c>#UiRemoved</c>), and registry data of a permit type is created by its provisioning, like the type's own registries.
    /// </remarks>
    private async Task EnsureAreaRegistryAsync(PermitTypeActor actor, CancellationToken ct)
    {
        if (settings.AreaRegistryCode != AirLimitsSchema.DefaultAreaRegistry
            || await registries.FindDefinitionAsync(settings.AreaRegistryCode, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        var userId = actor.AuditUserId;
        var def = new RegistryDef(
            EcrCode.Create(settings.AreaRegistryCode),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Air permit areas", ["ru"] = "Районы разрешений на выбросы", ["kz"] = "Шығарынды рұқсаттарының аудандары" }),
            isTemporal: false);
        registries.AddDefinition(def);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        var ordinal = 1;
        foreach (var (code, en, ru, kz) in AirLimitsSchema.AreaEntries)
        {
            var entry = new RegistryEntry(
                def.Id,
                EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["en"] = en, ["ru"] = ru, ["kz"] = kz }),
                userId,
                clock.UtcNow);
            entry.SetOrdinal(ordinal++);
            registries.Add(entry);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}