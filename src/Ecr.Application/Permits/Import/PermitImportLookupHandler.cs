// src/Ecr.Application/Permits/Import/PermitImportLookupHandler.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Nae;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits.Import;

/// <summary>
/// Resolves the lookup values of a paste/import into a permit table BEFORE anything is written: a text names an entry by its CODE
/// (for the NAE tree also by the NAE number <c>NaeCode</c>) or by its NAME in any language. Two or more entries with that name —
/// <c>ambiguous</c>, never a guess. Also derives Region/Area of every resolved NAE source from its ancestors in the NAE tree.
/// </summary>
/// <remarks>
/// <para>
/// Columns resolved here: lookups to a shared registry and to the type's own lists. A lookup to another table of the SAME permit
/// (<c>SamePermitTable</c>) is resolved by the dialog from that table's rows (they belong to one permit, not to the registry).
/// A NAE column accepts only nodes of level <c>SRC</c> (the same rule as <c>RegistryBatchHandler.CheckPermitNaeSourceAsync</c>):
/// a text that names only a region/site — <c>notSource</c>.
/// </para>
/// <para>
/// Region/Area (default decision of the coordinator's brief 06.11): the source's REGION / AREA ancestors give the NAE region/area
/// code (the node's <c>RegionCode</c>/<c>AreaCode</c>, else its code without <c>R_</c>/<c>A_</c>; a source outside the group nodes —
/// its own <c>RegionCode</c>/<c>AreaCode</c>), mapped to the Region/Area registry entries by <see cref="AirScopeMatcher"/>
/// (normalised English name or code). Unique match only; none — <c>noMatch</c>, two or more — <c>ambiguous</c>.
/// </para>
/// Right: <c>Registry.View</c> or grant <c>Read</c> on the table. Nothing is written.
/// <para>
/// ✎ D-456 (06.11): candidates follow the ONE import rule <see cref="Groups.LookupTargetEntries.ListAsync"/> with the caller's
/// profile. A target that is a permit-type GROUP (<c>PRMG_*</c>, no own entries) expands to its members, and a member under Deny is
/// skipped; a target under Deny, or one the caller may not choose from (<see cref="Groups.LookupTargetMatcher.MayChoose"/>), has no
/// candidates. A text naming a hidden entry answers <c>unknown</c> — the same shape as a code that does not exist (no existence
/// oracle).
/// </para>
/// </remarks>
public sealed class PermitImportLookupHandler(
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IRegistryHierarchyStore hierarchy,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    NaeTreeSettings? naeTree = null,
    AirLimitsSettings? air = null,
    Groups.IPermitGroupResolver? groups = null)
{
    /// <summary>Right to resolve (or a grant <c>Read</c> on the table).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Most texts in one request (all columns together).</summary>
    public const int MaxValues = 20_000;

    /// <summary>Longest text.</summary>
    public const int MaxTextLength = 1_000;

    private const string Separator = " · ";

    /// <summary>Resolves the texts and derives Region/Area.</summary>
    /// <param name="code">Table registry code.</param>
    /// <param name="request">Distinct texts per column.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PermitImportLookupResultDto> HandleAsync(string code, PermitImportLookupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lookup = new RegistryLookup(registries, code);
        var profile = await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var table = await PermitImportTables.DescribeAsync(
            registries, PermitImportTables.RequireTable(await lookup.RequireAsync(ct).ConfigureAwait(false), code), naeTree, air, ct).ConfigureAwait(false);

        var requested = request.Columns ?? [];
        Validate(requested);

        var lookups = table.Lookups.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<int, Target>();
        var results = new List<PermitImportLookupColumnResultDto>();
        var naeSources = new HashSet<long>();

        foreach (var column in requested)
        {
            if (column?.Column is null || !lookups.TryGetValue(column.Column, out var field))
            {
                continue;
            }

            var refId = field.RefRegistryDefId!.Value;
            if (!targets.TryGetValue(refId, out var target))
            {
                var def = await registries.FindDefinitionByIdAsync(refId, ct).ConfigureAwait(false);
                // Another table of the same permit: resolved by the dialog (rows of this permit only).
                // A target registry the caller does not read (no Registry.View / grant Read, or an explicit Deny, S18/D-312) gives no
                // candidates: every value is `unknown`, the same answer as a code that does not exist - no oracle of existence.
                target = def is null || PermitImportTables.IsPermitTable(def)
                    ? Target.Skip
                    : RegistryAccess.CanAccess(profile, Permission, GrantLevel.Read, refId)
                        // D-456: a readable registry still offers only the entries the caller may choose (PRMG members, Deny).
                        ? await LoadAsync(def, refId == table.NaeDefId, profile, ct).ConfigureAwait(false)
                        : Target.Hidden(def);
                targets[refId] = target;
            }

            if (target.Definition is null)
            {
                continue;
            }

            var values = (column.Values ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .Select(v => target.Resolve(v, Label))
                .ToList();
            if (refId == table.NaeDefId)
            {
                naeSources.UnionWith(values.Where(v => v.EntryId is not null).Select(v => v.EntryId!.Value));
            }

            results.Add(new PermitImportLookupColumnResultDto(field.Code, target.Definition.Code, values));
        }

        var derived = request.Derive && naeSources.Count > 0
            ? await DeriveAsync(table, naeSources, def => RegistryAccess.CanAccess(profile, Permission, GrantLevel.Read, def), ct).ConfigureAwait(false)
            : [];
        return new PermitImportLookupResultDto(results, derived);
    }

    private string Label(RegistryEntry entry) => entry.DisplayL10n.Get(currentUser.Language) ?? entry.Code;

    private static void Validate(IReadOnlyList<PermitImportLookupColumn?> columns)
    {
        var count = columns.Sum(c => c?.Values?.Count ?? 0);
        if (count > MaxValues || columns.Any(c => c?.Values?.Any(v => v?.Length > MaxTextLength) == true))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"The request has {count} values; the most is {MaxValues} of up to {MaxTextLength} characters.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.batchTooLarge",
                    ["max"] = MaxValues.ToString(CultureInfo.InvariantCulture),
                    ["count"] = count.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>Candidates of one registry, indexed by normalised code and name.</summary>
    private async Task<Target> LoadAsync(RegistryDef def, bool isNae, AccessProfile profile, CancellationToken ct)
    {
        var entries = (await CandidatesAsync(def, profile, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).ToList();
        var naeCodes = new Dictionary<long, string>();
        HashSet<long>? sources = null;
        if (isNae)
        {
            if (def.Fields.FirstOrDefault(f => string.Equals(f.Code, NaeTreeSchema.NaeCodeField, StringComparison.OrdinalIgnoreCase)) is { } naeCode)
            {
                foreach (var value in await rows.ListFieldValuesAsync([naeCode.Id], null, ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(value.Text))
                    {
                        naeCodes[value.RegistryEntryId] = value.Text;
                    }
                }
            }

            var levels = await hierarchy.ListLevelsAsync(def.Id, ct).ConfigureAwait(false);
            var source = levels.FirstOrDefault(l => string.Equals(l.Code, NaeTreeSchema.LevelSource, StringComparison.OrdinalIgnoreCase))?.Id;
            sources = [.. entries.Where(e => e.LevelId is not null && e.LevelId == source).Select(e => e.Id)];
        }

        var byCode = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, HashSet<long>> index, string? text, long id)
        {
            var key = Key(text);
            if (key.Length == 0)
            {
                return;
            }

            if (!index.TryGetValue(key, out var ids))
            {
                index[key] = ids = [];
            }

            ids.Add(id);
        }

        foreach (var entry in entries)
        {
            Add(byCode, entry.Code, entry.Id);
            var number = naeCodes.GetValueOrDefault(entry.Id);
            Add(byCode, number, entry.Id);
            foreach (var name in entry.DisplayL10n.Values.Values)
            {
                Add(byName, name, entry.Id);
                // The label the dialog showed so far: «<NAE number or code> · <name>».
                Add(byName, (number ?? entry.Code) + Separator + name, entry.Id);
            }
        }

        return new Target(def, entries.ToDictionary(e => e.Id), byCode, byName, sources);
    }

    /// <summary>
    /// D-456: entries the caller may choose — a group target through <see cref="Groups.LookupTargetEntries.ListAsync"/> (members under
    /// Deny skipped, Deny on the group — none); a direct target the caller may not choose from — none; otherwise the registry rows.
    /// </summary>
    private async Task<IReadOnlyList<RegistryEntry>> CandidatesAsync(RegistryDef def, AccessProfile profile, CancellationToken ct)
    {
        if (groups is not null)
        {
            await groups.EnsureFreshAsync(ct).ConfigureAwait(false);
            if (groups.IsGroup(def.Id))
            {
                return [.. (await Groups.LookupTargetEntries.ListAsync(registries, groups, def.Id, ct, profile).ConfigureAwait(false))
                    .Select(e => e.Entry)];
            }
        }

        return Groups.LookupTargetMatcher.MayChoose(profile, def.Id, viewProjectId: null)
            ? await rows.ListEntriesAsync(def.Id, asOfUtc: null, ct).ConfigureAwait(false)
            : [];
    }

    /// <summary>Region/Area of each NAE source from its ancestors.</summary>
    private async Task<IReadOnlyList<PermitImportDerivedDto>> DeriveAsync(
        PermitImportTable table, IReadOnlySet<long> sources, Func<int, bool> readable, CancellationToken ct)
    {
        var regionColumns = table.RegionColumns;
        var areaColumns = table.AreaColumns;
        // The NAE tree the caller does not read derives nothing (its sources cannot be resolved either: belt and braces).
        if ((regionColumns.Count == 0 && areaColumns.Count == 0) || !readable(table.NaeDefId!.Value))
        {
            return [];
        }

        var naeDef = (await registries.FindDefinitionByIdAsync(table.NaeDefId!.Value, ct).ConfigureAwait(false))!;
        var nodes = (await rows.ListEntriesAsync(naeDef.Id, asOfUtc: null, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).ToDictionary(e => e.Id);
        var regionValues = await FieldTextsAsync(naeDef, NaeTreeSchema.RegionField, ct).ConfigureAwait(false);
        var areaValues = await FieldTextsAsync(naeDef, NaeTreeSchema.AreaField, ct).ConfigureAwait(false);
        var levels = await hierarchy.ListLevelsAsync(naeDef.Id, ct).ConfigureAwait(false);
        int? Level(string code) => levels.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
        var regionLevel = Level(NaeTreeSchema.LevelRegion);
        var areaLevel = Level(NaeTreeSchema.LevelArea);

        static string Strip(string code, string prefix) => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? code[prefix.Length..] : code;
        string RegionOf(RegistryEntry e)
            => NaeTreeSchema.Meaningful(AirLimitsSchema.NormalizeScope(
                regionValues.GetValueOrDefault(e.Id) is { Length: > 0 } r ? r : Strip(e.Code, "R_"), ""));
        string AreaOf(RegistryEntry e, string region)
            => NaeTreeSchema.Meaningful(AirLimitsSchema.NormalizeScope(
                areaValues.GetValueOrDefault(e.Id) is { Length: > 0 } a ? a : Strip(Strip(e.Code, "A_"), region + "_"), AirLimitsSchema.NoArea));
        RegistryEntry? Ancestor(RegistryEntry e, int? level)
        {
            if (level is null)
            {
                return null;
            }

            var guard = 0;
            for (var cursor = e.ParentEntryId; cursor is { } p && nodes.TryGetValue(p, out var parent) && guard++ < 64; cursor = parent.ParentEntryId)
            {
                if (parent.LevelId == level)
                {
                    return parent;
                }
            }

            return null;
        }

        // The tree as AirScopeMatcher sees it: regions (with group names) and their areas.
        var tree = new Dictionary<string, (string? Name, Dictionary<string, string?> Areas)>(StringComparer.Ordinal);
        (string? Name, Dictionary<string, string?> Areas) Slot(string region)
        {
            if (!tree.TryGetValue(region, out var slot))
            {
                tree[region] = slot = (null, new Dictionary<string, string?>(StringComparer.Ordinal));
            }

            return slot;
        }

        foreach (var node in nodes.Values.Where(n => regionLevel is not null && n.LevelId == regionLevel))
        {
            if (RegionOf(node) is { Length: > 0 } code)
            {
                tree[code] = (node.DisplayL10n.Get("en") ?? node.Code, Slot(code).Areas);
            }
        }

        foreach (var node in nodes.Values.Where(n => areaLevel is not null && n.LevelId == areaLevel))
        {
            var region = Ancestor(node, regionLevel) is { } r ? RegionOf(r) : AirLimitsSchema.NormalizeScope(regionValues.GetValueOrDefault(node.Id), "");
            var area = AreaOf(node, region);
            if (region.Length > 0 && area.Length > 0 && area != AirLimitsSchema.NoArea)
            {
                Slot(region).Areas[area] = node.DisplayL10n.Get("en") ?? node.Code;
            }
        }

        var scopes = new Dictionary<long, (string Region, string Area)>();
        foreach (var id in sources)
        {
            if (!nodes.TryGetValue(id, out var source))
            {
                continue;
            }

            var regionNode = Ancestor(source, regionLevel);
            var region = regionNode is not null ? RegionOf(regionNode) : AirLimitsSchema.NormalizeScope(regionValues.GetValueOrDefault(id), "");
            var areaNode = Ancestor(source, areaLevel);
            var area = areaNode is not null
                ? AreaOf(areaNode, region)
                : AirLimitsSchema.NormalizeScope(areaValues.GetValueOrDefault(id), AirLimitsSchema.NoArea);
            if (area == AirLimitsSchema.NoArea)
            {
                area = "";
            }

            if (region.Length > 0)
            {
                var slot = Slot(region);
                if (area.Length > 0)
                {
                    slot.Areas.TryAdd(area, null);
                }
            }

            scopes[id] = (region, area);
        }

        var matcherTree = tree
            .Select(r => new AirRegionCodeDto(r.Key, r.Value.Name, [.. r.Value.Areas.Select(a => new AirAreaCodeDto(a.Key, a.Value))]))
            .ToList();
        // A Region/Area registry the caller does not read offers no candidates (`noMatch`): its entries are neither used nor named.
        var regionEntries = await EntriesAsync(table.RegionDefId is { } regionDef && readable(regionDef) ? regionDef : null, ct).ConfigureAwait(false);
        var areaEntries = await EntriesAsync(table.AreaDefId is { } areaDef && readable(areaDef) ? areaDef : null, ct).ConfigureAwait(false);

        var result = new List<PermitImportDerivedDto>();
        foreach (var (id, scope) in scopes.OrderBy(s => s.Key))
        {
            var cells = new List<PermitImportDerivedCellDto>();
            foreach (var column in regionColumns)
            {
                cells.Add(scope.Region.Length == 0
                    ? new PermitImportDerivedCellDto(column.Code, null, null, PermitImportReasons.NoScope, null)
                    : Pick(column.Code, scope.Region, regionEntries.Where(e =>
                        AirScopeMatcher.Match(AirScopeColumns.NameOf(e), null, matcherTree).NaeRegion == scope.Region
                        || AirScopeMatcher.Match(e.Code, null, matcherTree).NaeRegion == scope.Region)));
            }

            // A source without an area (a region without areas) leaves Area empty: nothing to derive, nothing to report.
            if (scope.Region.Length > 0 && scope.Area.Length > 0)
            {
                foreach (var column in areaColumns)
                {
                    cells.Add(Pick(column.Code, scope.Area, areaEntries.Where(e =>
                        AirScopeMatcher.Match(scope.Region, AirScopeColumns.NameOf(e), matcherTree).NaeArea == scope.Area
                        || AirScopeMatcher.Match(scope.Region, e.Code, matcherTree).NaeArea == scope.Area)));
                }
            }

            result.Add(new PermitImportDerivedDto(id, cells));
        }

        return result;
    }

    private PermitImportDerivedCellDto Pick(string column, string scope, IEnumerable<RegistryEntry> candidates)
    {
        var found = candidates.DistinctBy(e => e.Id).Take(2).ToList();
        return found.Count switch
        {
            1 => new PermitImportDerivedCellDto(column, found[0].Id, Label(found[0]), null, scope),
            0 => new PermitImportDerivedCellDto(column, null, null, PermitImportReasons.NoMatch, scope),
            _ => new PermitImportDerivedCellDto(column, null, null, PermitImportReasons.Ambiguous, scope),
        };
    }

    private async Task<IReadOnlyList<RegistryEntry>> EntriesAsync(int? defId, CancellationToken ct)
        => defId is { } id
            ? [.. (await rows.ListEntriesAsync(id, asOfUtc: null, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted)]
            : [];

    private async Task<Dictionary<long, string>> FieldTextsAsync(RegistryDef def, string field, CancellationToken ct)
    {
        var found = def.Fields.FirstOrDefault(f => string.Equals(f.Code, field, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            return [];
        }

        var result = new Dictionary<long, string>();
        foreach (var value in await rows.ListFieldValuesAsync([found.Id], null, ct).ConfigureAwait(false))
        {
            if (value.Text is { Length: > 0 } text)
            {
                result[value.RegistryEntryId] = text;
            }
        }

        return result;
    }

    /// <summary>Match key: compatibility-normalised, upper case, inner white space collapsed, trimmed.</summary>
    internal static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Normalize(NormalizationForm.FormKC).Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Candidates of one registry.</summary>
    private sealed record Target(
        RegistryDef? Definition,
        IReadOnlyDictionary<long, RegistryEntry> Entries,
        IReadOnlyDictionary<string, HashSet<long>> ByCode,
        IReadOnlyDictionary<string, HashSet<long>> ByName,
        IReadOnlySet<long>? Sources)
    {
        public static Target Skip { get; } = new(null, new Dictionary<long, RegistryEntry>(), new Dictionary<string, HashSet<long>>(), new Dictionary<string, HashSet<long>>(), null);

        /// <summary>A registry the caller does not read: no candidates, every value <c>unknown</c> (never <c>notSource</c>).</summary>
        public static Target Hidden(RegistryDef definition)
            => new(definition, new Dictionary<long, RegistryEntry>(), new Dictionary<string, HashSet<long>>(), new Dictionary<string, HashSet<long>>(), null);

        /// <summary>Code first (unique code wins over names), then name; a NAE column — sources only.</summary>
        public PermitImportLookupValueDto Resolve(string text, Func<RegistryEntry, string> label)
        {
            var key = Key(text);
            var byCode = Allowed(ByCode.GetValueOrDefault(key));
            if (byCode.Count == 1)
            {
                return Found(text, byCode[0], label);
            }

            var byName = Allowed(ByName.GetValueOrDefault(key));
            var all = byCode.Count > 1 ? byCode.Union(byName).ToList() : byName;
            if (all.Count == 1)
            {
                return Found(text, all[0], label);
            }

            if (all.Count > 1)
            {
                return new PermitImportLookupValueDto(text, null, null, PermitImportReasons.Ambiguous, all.Count);
            }

            // Only non-source NAE nodes have this code/name.
            var any = (ByCode.GetValueOrDefault(key)?.Count ?? 0) + (ByName.GetValueOrDefault(key)?.Count ?? 0);
            return Sources is not null && any > 0
                ? new PermitImportLookupValueDto(text, null, null, PermitImportReasons.NotSource, 0)
                : new PermitImportLookupValueDto(text, null, null, PermitImportReasons.Unknown, 0);
        }

        private List<long> Allowed(HashSet<long>? ids)
            => ids is null ? [] : [.. ids.Where(id => Sources is null || Sources.Contains(id)).Order()];

        private PermitImportLookupValueDto Found(string text, long id, Func<RegistryEntry, string> label)
            => new(text, id, label(Entries[id]), null, 1);
    }
}
