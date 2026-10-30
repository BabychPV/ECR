// src/Ecr.Application/Permits/AirLimits/AirLimitsImportHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Hierarchy;
using Ecr.Application.Registries.Nae;
using Ecr.Application.RegistryImport;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// Wizard backend: analyse, preview and apply of an Excel with emission parameters into an "Air Limits Import" permit. The preview
/// and the apply build the SAME plan (fingerprint); the apply is all or nothing in one transaction.
/// </summary>
public sealed class AirLimitsImportHandler(
    AirLimitsSettingsAccessor accessor,
    IRegistryFileImporter importer,
    IRegistryImportProfileStore profiles,
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Permission to analyse, preview and apply an import.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Layout of the file (header, mapping, counts, findings). Permission <c>Registry.EditData</c>.</summary>
    /// <param name="file">The workbook.</param>
    /// <param name="options">Profile or specification.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsAnalysisDto> AnalyseAsync(Stream file, AirLimitsImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var tables = await accessor.Data.TablesAsync(ct).ConfigureAwait(false);
        var (spec, code) = await SpecAsync(options, tables, ct).ConfigureAwait(false);
        return new AirLimitsAnalysisDto(code, await importer.AnalyseAsync(file, spec, ct).ConfigureAwait(false));
    }

    /// <summary>Preview: nothing is written. Permission <c>Registry.EditData</c> on the permit's tables.</summary>
    /// <param name="permitId">Permit.</param>
    /// <param name="file">The workbook.</param>
    /// <param name="options">Profile, region, area, decisions.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsPreviewDto> PreviewAsync(long permitId, Stream file, AirLimitsImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var ctx = await PrepareAsync(permitId, options, ct).ConfigureAwait(false);
        var preview = await importer.PreviewAsync(file, ctx.Spec, ctx.Request with { ExpectedFingerprint = null }, ct).ConfigureAwait(false);
        return new AirLimitsPreviewDto(permitId, ctx.Number, ctx.Region, ctx.Area, ctx.ProfileCode, preview);
    }

    /// <summary>Apply: the same plan as the preview, all or nothing, one transaction.</summary>
    /// <param name="permitId">Permit.</param>
    /// <param name="file">The workbook.</param>
    /// <param name="options">Profile, region, area, decisions, confirmed owners, expected fingerprint.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsImportResultDto> ApplyAsync(long permitId, Stream file, AirLimitsImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var ctx = await PrepareAsync(permitId, options, ct).ConfigureAwait(false);
        var result = await importer.CommitAsync(file, ctx.Spec, ctx.Request, ct).ConfigureAwait(false);
        var plan = result.Plan;
        var own = permitId.ToString(CultureInfo.InvariantCulture);

        return new AirLimitsImportResultDto(
            permitId,
            ctx.Number,
            ctx.Region,
            ctx.Area,
            plan.Insert.Count,
            plan.Insert.Sum(p => p.Children.Count),
            plan.CreateReferences.Count,
            result.Replace.ThisOwner.OwnerKey == own ? result.Replace.ThisOwner.Parents : 0,
            result.Replace.OtherOwners,
            result.Diff,
            plan.Fingerprint,
            plan.FileSha256 ?? string.Empty);
    }

    private sealed record Ctx(
        RegistryImportSpec Spec, string? ProfileCode, ImportRequest Request, string Number, string Region, string Area);

    private async Task<Ctx> PrepareAsync(long permitId, AirLimitsImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var locator = new PermitLocator(permits, registries, access, currentUser);
        var (type, _) = await locator.FindAsync(permitId, Permission, GrantLevel.Write, ct).ConfigureAwait(false);
        if (!string.Equals(type.Code, AirLimitsSchema.TypeCode, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"Permit {permitId} is not an Air Limits Import permit.",
                new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.NotAirPermit, ["typeCode"] = type.Code });
        }

        var tables = await accessor.Data.TablesAsync(ct).ConfigureAwait(false);
        foreach (var def in new[] { tables.Sources, tables.Substances })
        {
            await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, def.Id, ct).ConfigureAwait(false);
        }

        var region = AirLimitsSchema.NormalizeScope(options.Region, string.Empty);
        if (region.Length == 0)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "The region of the import is required.",
                new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.ScopeInvalid });
        }

        var area = AirLimitsSchema.NormalizeScope(options.Area, AirLimitsSchema.NoArea);
        var (spec, profileCode) = await SpecAsync(options, tables, ct).ConfigureAwait(false);

        var resolutions = options.Resolutions ?? [];
        if (resolutions.Any(r => r.Action == MissingAction.Create))
        {
            await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Write, tables.Nae.Id, ct).ConfigureAwait(false);
            await EnsureCreateUnderAsync(tables, resolutions, ct).ConfigureAwait(false);
        }

        var header = await accessor.Data.ReadAsync(tables.Header, [permitId], ct).ConfigureAwait(false);
        var number = header.TryGetValue(permitId, out var row) ? row.Text("Permit_Number") ?? string.Empty : string.Empty;

        var request = new ImportRequest(
            new ImportScope(permitId.ToString(CultureInfo.InvariantCulture), AirScope.Key(region, area)),
            resolutions,
            options.ConfirmedOtherOwners,
            options.ExpectedFingerprint,
            null,
            profileCode);

        return new Ctx(spec, profileCode, request, number, region, area);
    }

    private async Task EnsureCreateUnderAsync(AirTables tables, IReadOnlyList<MissingResolution> resolutions, CancellationToken ct)
    {
        var levels = await hierarchy.ListLevelsAsync(tables.Nae.Id, ct).ConfigureAwait(false);
        var source = RegistryHierarchyPlacement.LevelByCode(levels, NaeTreeSchema.LevelSource);

        foreach (var r in resolutions.Where(r => r.Action == MissingAction.Create))
        {
            var valid = long.TryParse(r.CreateUnder, NumberStyles.None, CultureInfo.InvariantCulture, out var parentId)
                        && await registries.FindEntryAsync(parentId, ct).ConfigureAwait(false) is { IsDeleted: false } parent
                        && parent.RegistryDefId == tables.Nae.Id;
            if (valid)
            {
                try
                {
                    await RegistryHierarchyPlacement.EnsureAsync(hierarchy, tables.Nae.Id, levels, null, parentId, source, ct).ConfigureAwait(false);
                }
                catch (BusinessRuleException)
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                throw new BusinessRuleException(
                    "ECR-IMP-0422",
                    $"NAE {r.Code} cannot be created under the chosen node.",
                    new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.CreateUnderInvalid, ["key"] = r.Code });
            }
        }
    }
    private async Task<(RegistryImportSpec Spec, string? Code)> SpecAsync(AirLimitsImportOptions options, AirTables tables, CancellationToken ct)
    {
        if (options.Spec is { } inline)
        {
            return (inline.Validated(), null);
        }

        var code = string.IsNullOrWhiteSpace(options.ProfileCode) ? AirLimitsSchema.ProfileCode : options.ProfileCode.Trim();
        var stored = await profiles.FindAsync(tables.Sources.Id, code, ct).ConfigureAwait(false);
        if (stored is not null)
        {
            return (RegistryImportSpec.Parse(stored.SpecJson), code);
        }

        if (code == AirLimitsSchema.ProfileCode)
        {
            return (BuiltInImportProfiles.EraTable33, code);
        }

        throw new BusinessRuleException(
            "ECR-IMP-0422",
            $"Import profile {code} does not exist.",
            new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.ProfileUnknown, ["code"] = code });
    }
}