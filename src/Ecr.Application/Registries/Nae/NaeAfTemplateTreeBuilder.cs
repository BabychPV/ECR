// src/Ecr.Application/Registries/Nae/NaeAfTemplateTreeBuilder.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>
/// The AF load by element TEMPLATE (human 03.11, quiz): <c>Air_Region</c> → REGION, <c>Air_OnShoreOffShore</c> → AREA,
/// <c>Air_Area</c> → SITE (a site may sit inside a site, Q2), <c>Air_Unit</c> → EQUIP, <c>Equipment</c> → SRC. Pure, like
/// <see cref="NaeAfTreeBuilder"/>, which hands over to it when the snapshot carries templates.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The parent of a typed element is its nearest typed ancestor by path; elements without a template (folders, the root) are
/// transparent, an unknown template is reported (<c>unknownTemplate</c>) and skipped with its subtree.</item>
/// <item>The place must follow the level rules (<see cref="NaeTreeSchema.IsValidPlacement"/>): an element in a wrong place is
/// skipped (<c>levelOrder</c>), and so is everything typed below it (<c>parentSkipped</c>).</item>
/// <item>REGION / AREA of a source come from its ancestors (not from the <c>Region</c>/<c>On_Offshore</c> attributes) — the same
/// derivation as the manual path; the technical codes are those of the path load, so both recognise each other's nodes.</item>
/// </list>
/// </remarks>
internal static class NaeAfTemplateTreeBuilder
{
    /// <summary>Builds the plan.</summary>
    /// <param name="snapshot">Elements (some with templates).</param>
    public static NaeTreePlan Build(NaeAfSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var issues = new List<NaeAfIssueDto>();
        if (!snapshot.IsComplete)
        {
            issues.Add(new NaeAfIssueDto("warning", "sourceIncomplete", "", "The AF listing may be truncated."));
        }

        var typed = new Dictionary<string, (NaeAfElement Element, string? Level)>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in snapshot.Elements)
        {
            var path = Norm(element.Path);
            if (string.IsNullOrWhiteSpace(element.Template) || typed.ContainsKey(path))
            {
                continue;
            }

            var level = NaeTreeSchema.LevelOfAfTemplate(element.Template);
            if (level is null)
            {
                issues.Add(new NaeAfIssueDto("skipped", "unknownTemplate", element.Path, $"Template «{element.Template}» is not an NAE tree template."));
            }

            typed[path] = (element, level);
        }

        var nodes = new Dictionary<string, NaeTreeNodePlan>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var accepted = new Dictionary<string, Place>(StringComparer.OrdinalIgnoreCase);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBusiness = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, (element, level)) in typed.OrderBy(t => Segments(t.Key).Count).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (level is null)
            {
                rejected.Add(path);
                continue;
            }

            var parentPath = NearestTypedAncestor(path, typed);
            if (parentPath is not null && rejected.Contains(parentPath))
            {
                issues.Add(new NaeAfIssueDto("skipped", "parentSkipped", element.Path, "Its typed parent element was skipped."));
                rejected.Add(path);
                continue;
            }

            var parent = parentPath is null ? null : accepted[parentPath];
            if (!NaeTreeSchema.IsValidPlacement(parent?.Level, level))
            {
                issues.Add(new NaeAfIssueDto(
                    "skipped", "levelOrder", element.Path, $"{level} ({element.Template}) cannot sit under {parent?.Level ?? "the root"}."));
                rejected.Add(path);
                continue;
            }

            var place = Plan(element, level, parent, nodes, usedCodes, seenBusiness, issues, depth);
            if (place is null)
            {
                rejected.Add(path);
                continue;
            }

            accepted[path] = place;
        }

        var rank = NaeTreeSchema.LevelCodes.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var ordered = new List<NaeTreeNodePlan>(nodes.Count);
        foreach (var group in nodes.Values.GroupBy(n => n.ParentKey ?? ""))
        {
            var i = 1;
            foreach (var n in group.OrderBy(n => n.Display["en"], StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Code, StringComparer.Ordinal))
            {
                ordered.Add(n with { Ordinal = i++ });
            }
        }

        // Parents before children also inside one level (a site inside a site): level rank, then depth.
        return new NaeTreePlan(
            [.. ordered
                .OrderBy(n => rank[n.LevelCode])
                .ThenBy(n => depth[n.Key])
                .ThenBy(n => n.ParentKey, StringComparer.Ordinal)
                .ThenBy(n => n.Ordinal)],
            issues);
    }

    private static Place? Plan(
        NaeAfElement element,
        string level,
        Place? parent,
        Dictionary<string, NaeTreeNodePlan> nodes,
        HashSet<string> usedCodes,
        HashSet<string> seenBusiness,
        List<NaeAfIssueDto> issues,
        Dictionary<string, int> depth)
    {
        var name = NaeCodeNormalizer.Clean(element.Name);
        var region = parent?.Region ?? "";
        var area = parent?.Area ?? NaeAfTreeBuilder.NoArea;
        var areaTag = area == NaeAfTreeBuilder.NoArea ? "NA" : area;
        var site = parent?.Site;
        string key;
        string codeBase;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { [NaeTreeSchema.AfPathField] = element.Path };
        var fallbackName = name;

        switch (level)
        {
            case NaeTreeSchema.LevelRegion:
                region = NaeAfTreeBuilder.Upper(name);
                key = $"R|{region}";
                codeBase = "R_" + region;
                break;
            case NaeTreeSchema.LevelArea:
                area = NaeAfTreeBuilder.Upper(name);
                key = $"A|{region}|{area}";
                codeBase = $"A_{region}_{area}";
                break;
            case NaeTreeSchema.LevelSite:
                site = parent?.Level == NaeTreeSchema.LevelSite ? $"{parent.Site}_{NaeAfTreeBuilder.Upper(name)}" : NaeAfTreeBuilder.Upper(name);
                key = $"S|{region}|{area}|{site}";
                codeBase = $"S_{region}_{areaTag}_{site}";
                break;
            case NaeTreeSchema.LevelEquipment:
                key = $"S|{region}|{area}|{site}|G|{NaeAfTreeBuilder.Upper(name)}";
                codeBase = $"G_{region}_{areaTag}_{site}_{NaeAfTreeBuilder.Upper(name)}";
                break;
            default:
                var naeCode = NaeCodeNormalizer.Normalize(element.Name);
                if (naeCode is null)
                {
                    issues.Add(new NaeAfIssueDto("skipped", "noCode", element.Path, "Element name is not an NAE code."));
                    return null;
                }

                var business = $"{naeCode}|{region}|{area}";
                if (!seenBusiness.Add(business))
                {
                    issues.Add(new NaeAfIssueDto("skipped", "duplicateBusinessKey", element.Path, $"NAE {naeCode} appears twice in {region}/{area}."));
                    return null;
                }

                key = $"N|{business}";
                codeBase = $"N_{region}_{areaTag}_{NaeAfTreeBuilder.Upper(naeCode)}";
                fields[NaeTreeSchema.NaeCodeField] = naeCode;
                fields[NaeTreeSchema.RegionField] = region;
                fields[NaeTreeSchema.AreaField] = area;
                fallbackName = naeCode;
                break;
        }

        // Two AF elements for one region/area (or one site path) are one node.
        if (!nodes.ContainsKey(key))
        {
            var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = string.IsNullOrWhiteSpace(element.NameEn) ? fallbackName : NaeCodeNormalizer.Clean(element.NameEn),
            };
            foreach (var (lang, text) in new[] { ("ru", element.NameRu), ("kz", element.NameKz) })
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    display[lang] = NaeCodeNormalizer.Clean(text);
                }
            }

            nodes[key] = new NaeTreeNodePlan(
                key, parent?.Key, level, NaeAfTreeBuilder.UniqueCode(codeBase, key, usedCodes), display, fields, 0);
            depth[key] = (parent is null ? 0 : depth[parent.Key]) + 1;
        }

        return new Place(key, level, region, area, site);
    }

    private static string? NearestTypedAncestor(string path, Dictionary<string, (NaeAfElement Element, string? Level)> typed)
    {
        var segments = Segments(path);
        for (var n = segments.Count - 1; n >= 1; n--)
        {
            var candidate = string.Join('\\', segments.Take(n));
            if (typed.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Norm(string? path) => string.Join('\\', Segments(path));

    private static List<string> Segments(string? path)
        => (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private sealed record Place(string Key, string Level, string Region, string Area, string? Site);
}
