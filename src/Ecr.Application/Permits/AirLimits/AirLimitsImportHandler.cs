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
    ICurrentUser currentUser,
    AirScopeColumns columns,
    GetAirScopeCodesHandler naeScopes)
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
        return new AirLimitsPreviewDto(
            permitId, ctx.Number, ctx.Region, ctx.Area, ctx.ProfileCode, preview,
            ctx.Scope.Region.EntryId, ctx.Scope.Area?.EntryId, ScopeInNaeTree: ctx.Scope.InTree);
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
            plan.FileSha256 ?? string.Empty,
            ctx.Scope.Region.EntryId,
            ctx.Scope.Area?.EntryId);
    }

    private sealed record Ctx(RegistryImportSpec Spec, string? ProfileCode, ImportRequest Request, string Number, AirImportScope Scope)
    {
        /// <summary>Region shown in the answer: the NAE tree region code of the scope.</summary>
        public string Region => Scope.NaeRegion;

        /// <summary>Area shown in the answer: the NAE tree area, else the value's code, else «no area».</summary>
        public string Area => Scope.NaeArea ?? Scope.Area?.Code ?? AirLimitsSchema.NoArea;
    }

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

        // ✎ AIR-ON-AIR: the scope values per the kind configured on the type (Registry → entry id, Text / List → string), mapped to
        // the NAE tree through the ONE shared matcher.
        var (regionColumn, areaColumn) = await columns.ReadAsync(ct).ConfigureAwait(false);
        var region = await ScopeValueAsync(regionColumn, options.RegionEntryId, options.Region, ct).ConfigureAwait(false)
                     ?? throw ScopeInvalid("The region of the import is required.", AirLimitsSchema.Region);
        var area = await ScopeValueAsync(areaColumn, options.AreaEntryId, options.Area, ct).ConfigureAwait(false);
        var match = AirScopeMatcher.Match(region.Label, area?.Label, (await naeScopes.ReadAsync(ct).ConfigureAwait(false)).Regions);
        var air = AirImportScope.From(match, region, area);

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
            new ImportScope(permitId.ToString(CultureInfo.InvariantCulture), air.Key()),
            resolutions,
            options.ConfirmedOtherOwners,
            options.ExpectedFingerprint,
            null,
            profileCode);

        return new Ctx(spec, profileCode, request, number, air);
    }

    /// <summary>
    /// The value of one scope column: Registry → an entry of that registry (by id); List → one of the type's values (by text or
    /// code); Text → the text. Nothing given → <c>null</c>. A value outside the registry / list → <c>422 airScopeInvalid</c>.
    /// </summary>
    private async Task<AirScopeValue?> ScopeValueAsync(AirScopeColumn column, long? entryId, string? text, CancellationToken ct)
    {
        if (column.Kind == AirLimitsSchema.KindText)
        {
            var clean = text?.Trim();
            return string.IsNullOrEmpty(clean) ? null : new AirScopeValue(null, clean, clean, clean);
        }

        if (entryId is null && string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var def = column.RegistryCode is null ? null : await registries.FindDefinitionAsync(column.RegistryCode, ct).ConfigureAwait(false);
        if (def is null)
        {
            throw ScopeInvalid($"The registry {column.RegistryCode} of the {column.ColumnCode} column does not exist.", column.ColumnCode);
        }

        Domain.Entities.Dictionaries.RegistryEntry? entry;
        if (entryId is { } id)
        {
            entry = await registries.FindEntryAsync(id, ct).ConfigureAwait(false);
            if (entry is null || entry.IsDeleted || entry.RegistryDefId != def.Id)
            {
                entry = null;
            }
        }
        else
        {
            var wanted = text!.Trim();
            entry = (await registries.ListEntriesAsync(def.Id, ct).ConfigureAwait(false))
                .Where(e => !e.IsDeleted && e.IsActive)
                .FirstOrDefault(e => string.Equals(AirScopeColumns.NameOf(e), wanted, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(e.Code, wanted, StringComparison.OrdinalIgnoreCase));
        }

        return entry is null
            ? throw ScopeInvalid($"The {column.ColumnCode} value is not in {column.RegistryCode}.", column.ColumnCode)
            : new AirScopeValue(entry.Id, null, AirScopeColumns.NameOf(entry), entry.Code);
    }

    private static BusinessRuleException ScopeInvalid(string message, string column)
        => new(
            "ECR-IMP-0422",
            message,
            new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.ScopeInvalid, ["column"] = column });

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
            // An unsaved specification may carry a field the user has not pointed at a column yet: reported, not refused.
            return (inline.ValidatedForReading(), null);
        }

        var code = string.IsNullOrWhiteSpace(options.ProfileCode) ? AirLimitsSchema.ProfileCode : options.ProfileCode.Trim();
        var stored = AirImportProfileHandler.IsPasteMapping(code) ? null : await profiles.FindAsync(tables.Sources.Id, code, ct).ConfigureAwait(false);
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