// src/Ecr.Application/Registries/Nae/NaeTreeManualHandler.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.RegistryImport;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Registries.Nae;

/// <summary>
/// NAE tree without AF (human 02.11: «якщо AF немає як створити або імпортувати?»): a manual region root, region roots from
/// <c>LAND_REGION</c> for an empty tree, and the Excel import of the whole tree (analyse → preview → apply, all or nothing).
/// </summary>
/// <remarks>
/// Create-only, like the AF load: an existing node is never renamed, moved or changed — a differing stored name is reported
/// as a <c>nameDiffers</c> warning. Technical codes and the business key are the AF load's, so the three ways of filling the
/// tree recognise each other's nodes, and a second import of the same file is a no-op.
/// </remarks>
public sealed class NaeTreeManualHandler(
    NaeTreeSettings settings,
    IRegistryStore registries,
    NaeTreeProvisioner provisioner,
    NaeTreeNodeApplier applier,
    INaeTreeWorkbook workbook,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    Permits.AirLimits.AirLimitsSettings airSettings)
{
    /// <summary>Permission to write the tree.</summary>
    public const string EditPermission = "Registry.EditData";

    /// <summary>Permission to download the template.</summary>
    public const string ViewPermission = "Registry.View";

    /// <summary>Most issues returned in a report.</summary>
    public const int MaxIssues = 200;

    internal const string RegionRequiredKey = "err.ECR-REG-0422.naeRegionRequired";
    internal const string TreeNotEmptyKey = "err.ECR-REG-0422.naeTreeNotEmpty";
    internal const string SeedSourceMissingKey = "err.ECR-REG-0422.naeSeedSourceMissing";
    internal const string HasErrorsKey = "err.ECR-IMP-0422.naeImportHasErrors";

    /// <summary>Creates a region root (and an optional area under it); existing nodes are returned as they are.</summary>
    /// <param name="request">Region, area, names.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NaeRootResultDto> CreateRootAsync(CreateNaeRootRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);

        var regionText = NaeCodeNormalizer.Clean(request.RegionCode);
        var region = NaeAfTreeBuilder.Upper(regionText);
        if (region.Length == 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422", "A region code is required.", new Dictionary<string, object?> { ["messageKey"] = RegionRequiredKey });
        }

        // The area is CHOSEN from the Area list (human 04.11): free text is refused, the list entry gives the code.
        var (chosenArea, chosenName) = await NaeAreaChoice.ResolveAsync(registries, airSettings, request.AreaCode, ct).ConfigureAwait(false);
        var areaText = chosenName is { Length: > 0 } listed ? NaeCodeNormalizer.Clean(listed) : chosenArea;
        var area = chosenArea;
        var names = Names(request.NameEn, request.NameRu, request.NameKz);

        // AN-158: the technical code allows ASCII only, so a non-ASCII part becomes its 8-hex hash (A_ATYRAU_3F2A91C0), not "_____";
        // the node also stores its OWN code (RegionCode / AreaCode), which sources below inherit.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regionKey = $"R|{region}";
        var regionPart = NaeAfTreeBuilder.ReadablePart(region);
        var nodes = new List<NaeTreeNodePlan>
        {
            Group(
                regionKey, null, NaeTreeSchema.LevelRegion, NaeAfTreeBuilder.UniqueCode("R_" + regionPart, regionKey, used), regionText,
                area.Length == 0 ? names : null, (NaeTreeSchema.RegionField, region)),
        };
        string? areaKey = null;
        if (area.Length > 0)
        {
            areaKey = $"A|{region}|{area}";
            nodes.Add(Group(areaKey, regionKey, NaeTreeSchema.LevelArea, NaeAfTreeBuilder.UniqueCode($"A_{regionPart}_{NaeAfTreeBuilder.ReadablePart(area)}", areaKey, used), areaText, names, (NaeTreeSchema.AreaField, area)));
        }

        var state = await LoadAsync(ct).ConfigureAwait(false);
        var existingIdByKey = nodes.Where(n => state.ByCode.ContainsKey(n.Code))
            .ToDictionary(n => n.Key, n => state.ByCode[n.Code].Id, StringComparer.Ordinal);
        var toCreate = nodes.Where(n => !existingIdByKey.ContainsKey(n.Key)).ToList();
        if (toCreate.Count == 0)
        {
            return new NaeRootResultDto(
                settings.RegistryCode, existingIdByKey[regionKey], areaKey is null ? null : existingIdByKey[areaKey], false, false);
        }

        var (ids, provisioned) = await WriteAsync(
            state, toCreate, existingIdByKey, new NaeWriteAudit("NaeRootCreate", "registryAudit.naeRootCreated", new { created = toCreate.Count }), ct)
            .ConfigureAwait(false);
        return new NaeRootResultDto(settings.RegistryCode, ids[regionKey], areaKey is null ? null : ids[areaKey], true, provisioned);
    }

    /// <summary>One region root per live <c>LAND_REGION</c> entry; only for an empty tree.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NaeSeedRootsResultDto> SeedRootsAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);

        var state = await LoadAsync(ct).ConfigureAwait(false);
        if (state.Entries.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"The NAE tree already has {state.Entries.Count} nodes: roots are seeded only into an empty tree.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = TreeNotEmptyKey,
                    ["count"] = state.Entries.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        var source = await registries.FindDefinitionAsync(settings.RegionSourceRegistryCode, ct).ConfigureAwait(false);
        var regions = source is null
            ? []
            : (await registries.ListEntriesAsync(source.Id, ct).ConfigureAwait(false))
                .Where(e => !e.IsDeleted)
                .OrderBy(e => e.Ordinal)
                .ThenBy(e => e.Code, StringComparer.Ordinal)
                .ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<NaeTreeNodePlan>();
        foreach (var entry in regions)
        {
            var en = NaeCodeNormalizer.Clean(entry.DisplayL10n.Get("en") ?? entry.Code);
            var region = NaeAfTreeBuilder.Upper(en);
            var key = $"R|{region}";
            if (region.Length == 0 || nodes.Any(n => n.Key == key))
            {
                continue;
            }

            var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = en };
            foreach (var lang in new[] { "ru", "kz" })
            {
                if (entry.DisplayL10n.Get(lang) is { Length: > 0 } text)
                {
                    display[lang] = NaeCodeNormalizer.Clean(text);
                }
            }

            nodes.Add(new NaeTreeNodePlan(
                key, null, NaeTreeSchema.LevelRegion, NaeAfTreeBuilder.UniqueCode("R_" + region, key, used), display,
                new Dictionary<string, string>(StringComparer.Ordinal) { [NaeTreeSchema.RegionField] = region }, nodes.Count + 1));
        }

        if (nodes.Count == 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Registry «{settings.RegionSourceRegistryCode}» has no regions to seed from.",
                new Dictionary<string, object?> { ["messageKey"] = SeedSourceMissingKey, ["registryCode"] = settings.RegionSourceRegistryCode });
        }

        var (_, provisioned) = await WriteAsync(
            state,
            nodes,
            new Dictionary<string, long>(StringComparer.Ordinal),
            new NaeWriteAudit("NaeSeedRoots", "registryAudit.naeRootCreated", new { created = nodes.Count, source = settings.RegionSourceRegistryCode }),
            ct).ConfigureAwait(false);
        return new NaeSeedRootsResultDto(settings.RegistryCode, settings.RegionSourceRegistryCode, [.. nodes.Select(n => n.Key[2..])], provisioned);
    }

    /// <summary>The empty template.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<byte[]> TemplateAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, ViewPermission, ct).ConfigureAwait(false);
        return workbook.BuildTemplate();
    }

    /// <summary>File-only analysis: the database is not consulted.</summary>
    /// <param name="file">Workbook.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NaeTreeImportReportDto> AnalyseAsync(Stream file, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);
        var sheet = await workbook.ReadAsync(file, ct).ConfigureAwait(false);
        var plan = NaeSheetTreeBuilder.Build(sheet);
        return Report(sheet, plan, [.. plan.Issues], plan.Nodes, new Dictionary<string, long>(), false, false);
    }

    /// <summary>Preview over the stored tree; nothing is written.</summary>
    /// <param name="file">Workbook.</param>
    /// <param name="options">Options (the preview ignores the fingerprint).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NaeTreeImportReportDto> PreviewAsync(Stream file, NaeTreeImportOptions options, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);
        var planned = await PlanAsync(file, ct).ConfigureAwait(false);
        return Report(planned.Sheet, planned.Plan, planned.Issues, planned.ToCreate, planned.ExistingIdByKey, false, false);
    }

    /// <summary>All-or-nothing apply.</summary>
    /// <param name="file">Workbook (the same as in the preview).</param>
    /// <param name="options">Options with the previewed fingerprint.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NaeTreeImportReportDto> ApplyAsync(Stream file, NaeTreeImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        await PermissionCheck.RequireAsync(access, currentUser, EditPermission, ct).ConfigureAwait(false);
        var planned = await PlanAsync(file, ct).ConfigureAwait(false);

        var errors = planned.Issues.Count(i => i.Severity == "error");
        if (errors > 0 || !planned.Plan.HeaderFound)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"The file has {errors} error(s). Nothing was imported.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = HasErrorsKey,
                    ["count"] = Math.Max(errors, 1).ToString(CultureInfo.InvariantCulture),
                });
        }

        var fingerprint = Fingerprint(planned.ToCreate);
        if (options.ExpectedFingerprint is { Length: > 0 } expected && !string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "The plan differs from the previewed one. Nothing was imported.",
                new Dictionary<string, object?> { ["messageKey"] = RegistryImportKeys.PlanChanged });
        }

        if (planned.ToCreate.Count == 0)
        {
            return Report(planned.Sheet, planned.Plan, planned.Issues, planned.ToCreate, planned.ExistingIdByKey, false, false);
        }

        var (_, provisioned) = await WriteAsync(
            planned.State,
            planned.ToCreate,
            planned.ExistingIdByKey,
            new NaeWriteAudit(
                "NaeExcelImport",
                "registryAudit.naeExcelImport",
                new { created = planned.ToCreate.Count, existing = planned.ExistingIdByKey.Count, file = planned.Sheet.FileSha256 }),
            ct).ConfigureAwait(false);
        return Report(planned.Sheet, planned.Plan, planned.Issues, planned.ToCreate, planned.ExistingIdByKey, true, provisioned);
    }

    private async Task<Planned> PlanAsync(Stream file, CancellationToken ct)
    {
        var sheet = await workbook.ReadAsync(file, ct).ConfigureAwait(false);
        var plan = NaeSheetTreeBuilder.Build(sheet);
        var issues = plan.Issues.ToList();
        var state = await LoadAsync(ct).ConfigureAwait(false);

        var existingIdByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var toCreate = new List<NaeTreeNodePlan>();
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in plan.Nodes)
        {
            var row = plan.RowOf.GetValueOrDefault(node.Key);
            var naeCode = node.Fields.GetValueOrDefault(NaeTreeSchema.NaeCodeField);
            long? parentId = node.ParentKey is { } pk && existingIdByKey.TryGetValue(pk, out var pid) ? pid : null;
            if (node.ParentKey is { } parentKey && blocked.Contains(parentKey))
            {
                blocked.Add(node.Key);
                continue;
            }

            RegistryEntry? match = null;
            if (node.LevelCode == NaeTreeSchema.LevelSource)
            {
                var business = $"{node.Fields[NaeTreeSchema.NaeCodeField]}|{node.Fields[NaeTreeSchema.RegionField]}|{node.Fields[NaeTreeSchema.AreaField]}";
                state.ByCode.TryGetValue(node.Code, out var byCode);
                state.ByBusiness.TryGetValue(business, out var byBusiness);
                if (byCode is not null && (byBusiness is null || byBusiness.Id != byCode.Id))
                {
                    issues.Add(new NaeTreeImportIssueDto(
                        "error", "codeTaken", row, NaeTreeImportLayout.NaeCode, naeCode, $"Technical code {node.Code} belongs to another node."));
                    blocked.Add(node.Key);
                    continue;
                }

                match = byBusiness;
                if (match is not null && (parentId is null || match.ParentEntryId != parentId))
                {
                    issues.Add(new NaeTreeImportIssueDto(
                        "error", "existingElsewhere", row, NaeTreeImportLayout.NaeCode, naeCode,
                        $"NAE {naeCode} ({node.Fields[NaeTreeSchema.RegionField]}/{node.Fields[NaeTreeSchema.AreaField]}) already sits under another parent."));
                    blocked.Add(node.Key);
                    continue;
                }
            }
            else
            {
                state.ByCode.TryGetValue(node.Code, out match);
            }

            if (match is null)
            {
                toCreate.Add(node);
                continue;
            }

            existingIdByKey[node.Key] = match.Id;
            foreach (var (lang, text) in node.Display)
            {
                // An NAE row without Name_En carries its code as the English name: that is a default, not a name to compare.
                if (lang == "en" && naeCode is not null && text == naeCode)
                {
                    continue;
                }

                var stored = match.DisplayL10n.Get(lang);
                if (!string.Equals(stored?.Trim(), text, StringComparison.Ordinal))
                {
                    issues.Add(new NaeTreeImportIssueDto(
                        "warning", "nameDiffers", row, NameColumn(lang), naeCode,
                        $"{node.LevelCode} {match.Code}: stored {lang} name «{stored}», file «{text}»; the stored name is kept."));
                }
            }
        }

        return new Planned(sheet, plan, issues, toCreate, existingIdByKey, state);
    }

    private async Task<(IReadOnlyDictionary<string, long> Ids, bool Provisioned)> WriteAsync(
        TreeState state,
        IReadOnlyList<NaeTreeNodePlan> toCreate,
        IReadOnlyDictionary<string, long> existingIdByKey,
        NaeWriteAudit record,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401",
                         "Anonymous requests do not change registries.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        if (state.Definition is null)
        {
            // The first write provisions the registry: that is a definition change.
            await PermissionCheck.RequireAsync(access, currentUser, Hierarchy.SaveRegistryHierarchyHandler.Permission, ct).ConfigureAwait(false);
        }

        var (definition, levels, provisioned) = await provisioner.EnsureAsync(settings.RegistryCode, ct).ConfigureAwait(false);
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);
        var ids = await applier.WriteAsync(definition, levels, toCreate, existingIdByKey, state.MaxOrdinalByParent, record, userId, ct)
            .ConfigureAwait(false);
        return (ids, provisioned);
    }

    private async Task<TreeState> LoadAsync(CancellationToken ct)
    {
        var definition = await registries.FindDefinitionAsync(settings.RegistryCode, ct).ConfigureAwait(false);
        if (definition is null)
        {
            return new TreeState(null, [], new Dictionary<string, RegistryEntry>(), new Dictionary<string, RegistryEntry>(), new Dictionary<long, int>());
        }

        var entries = (await registries.ListEntriesAsync(definition.Id, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).ToList();
        var byCode = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            byCode[e.Code] = e;
        }

        var maxOrdinal = new Dictionary<long, int>();
        foreach (var group in entries.GroupBy(e => e.ParentEntryId ?? 0))
        {
            maxOrdinal[group.Key] = group.Max(e => e.Ordinal);
        }

        var byBusiness = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var wanted = new[] { NaeTreeSchema.NaeCodeField, NaeTreeSchema.RegionField, NaeTreeSchema.AreaField };
        var fieldIds = definition.Fields.Where(f => wanted.Contains(f.Code, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(f => f.Id, f => f.Code);
        if (fieldIds.Count == wanted.Length && entries.Count > 0)
        {
            var byId = entries.ToDictionary(e => e.Id);
            var values = await registries.ListValuesForEntriesAsync([.. entries.Select(e => e.Id)], ct).ConfigureAwait(false);
            foreach (var group in values.GroupBy(v => v.RegistryEntryId))
            {
                var fields = group.Where(v => fieldIds.ContainsKey(v.RegistryFieldDefId))
                    .ToDictionary(v => fieldIds[v.RegistryFieldDefId], v => v.ValueString ?? "", StringComparer.OrdinalIgnoreCase);
                if (wanted.All(w => fields.TryGetValue(w, out var s) && s.Length > 0) && byId.TryGetValue(group.Key, out var entry))
                {
                    byBusiness.TryAdd($"{fields[wanted[0]]}|{fields[wanted[1]]}|{fields[wanted[2]]}", entry);
                }
            }
        }

        return new TreeState(definition, entries, byCode, byBusiness, maxOrdinal);
    }

    private NaeTreeImportReportDto Report(
        NaeTreeSheet sheet,
        NaeSheetPlan plan,
        IReadOnlyList<NaeTreeImportIssueDto> issues,
        IReadOnlyList<NaeTreeNodePlan> toCreate,
        IReadOnlyDictionary<string, long> existingIdByKey,
        bool applied,
        bool provisioned)
    {
        var existingKeys = existingIdByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var levels = NaeTreeSchema.LevelCodes
            .Select(l => new NaeAfLevelCountDto(
                l,
                toCreate.Count(n => n.LevelCode == l),
                plan.Nodes.Count(n => n.LevelCode == l && existingKeys.Contains(n.Key))))
            .ToList();
        var ordered = issues.OrderBy(i => i.Row).ThenBy(i => i.Severity == "error" ? 0 : 1).ToList();
        return new NaeTreeImportReportDto(
            settings.RegistryCode,
            sheet.FileSha256,
            sheet.SheetName,
            plan.RowsRead,
            levels,
            [.. ordered.Take(MaxIssues)],
            Math.Max(0, ordered.Count - MaxIssues),
            Fingerprint(toCreate),
            plan.HeaderFound && ordered.All(i => i.Severity != "error"),
            applied,
            provisioned);
    }

    /// <summary>Hash of what the apply would write (code, parent, level, names, values), order-independent.</summary>
    internal static string Fingerprint(IEnumerable<NaeTreeNodePlan> toCreate)
    {
        var lines = toCreate
            .Select(n => string.Join(
                '\u001f',
                n.Code,
                n.ParentKey ?? "",
                n.LevelCode,
                string.Join('\u001e', n.Display.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}={d.Value}")),
                string.Join('\u001e', n.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}"))))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static string NameColumn(string lang) => lang switch
    {
        "ru" => NaeTreeImportLayout.NameRu,
        "kz" => NaeTreeImportLayout.NameKz,
        _ => NaeTreeImportLayout.NameEn,
    };

    private static Dictionary<string, string> Names(string? en, string? ru, string? kz)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, text) in new[] { ("en", en), ("ru", ru), ("kz", kz) })
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                names[lang] = NaeCodeNormalizer.Clean(text);
            }
        }

        return names;
    }

    private static NaeTreeNodePlan Group(
        string key, string? parentKey, string level, string code, string codeText, Dictionary<string, string>? names,
        (string Field, string Value)? own = null)
    {
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = codeText };
        foreach (var (lang, text) in names ?? [])
        {
            display[lang] = text;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (own is { Value.Length: > 0 } o)
        {
            fields[o.Field] = o.Value;
        }

        return new NaeTreeNodePlan(key, parentKey, level, code, display, fields, 1);
    }

    private sealed record TreeState(
        RegistryDef? Definition,
        IReadOnlyList<RegistryEntry> Entries,
        IReadOnlyDictionary<string, RegistryEntry> ByCode,
        IReadOnlyDictionary<string, RegistryEntry> ByBusiness,
        IReadOnlyDictionary<long, int> MaxOrdinalByParent);

    private sealed record Planned(
        NaeTreeSheet Sheet,
        NaeSheetPlan Plan,
        List<NaeTreeImportIssueDto> Issues,
        List<NaeTreeNodePlan> ToCreate,
        Dictionary<string, long> ExistingIdByKey,
        TreeState State);
}
