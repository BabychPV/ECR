// src/Ecr.Application/Registries/Nae/NaeAfTreeBuilder.cs
using System.Security.Cryptography;
using System.Text;

namespace Ecr.Application.Registries.Nae;

/// <summary>One AF element under the equipment root, with the attributes the NAE load needs.</summary>
/// <param name="Path">Full AF path, segments separated by <c>\</c>.</param>
/// <param name="Name">Element name (for an NAE — its code, e.g. <c>1085</c>, <c>0036_pod</c>).</param>
/// <param name="NameEn">Attribute <c>Name_En</c>.</param>
/// <param name="NameRu">Attribute <c>Name_Ru</c>.</param>
/// <param name="NameKz">Attribute <c>Name_Kz</c>.</param>
/// <param name="Region">Attribute <c>Region</c>.</param>
/// <param name="OnOffshore">Attribute <c>On_Offshore</c>.</param>
public sealed record NaeAfElement(
    string Path,
    string Name,
    string? NameEn,
    string? NameRu,
    string? NameKz,
    string? Region,
    string? OnOffshore);

/// <summary>Elements under the root.</summary>
/// <param name="Elements">All elements (groups and NAE alike).</param>
/// <param name="IsComplete"><c>false</c> — the listing may have been truncated.</param>
public sealed record NaeAfSnapshot(IReadOnlyList<NaeAfElement> Elements, bool IsComplete);

/// <summary>One node the load wants to exist.</summary>
/// <param name="Key">Identity inside the plan (unique).</param>
/// <param name="ParentKey">Parent node key; <c>null</c> for a root.</param>
/// <param name="LevelCode">One of <see cref="NaeTreeSchema.LevelCodes"/>.</param>
/// <param name="Code">Technical entry code (<c>EcrCode</c>-valid, at most 64 characters).</param>
/// <param name="Display">Names by language (<c>en</c> always present).</param>
/// <param name="Fields">Field values by field code (only on <c>SRC</c> nodes and <c>AfPath</c>).</param>
/// <param name="Ordinal">Position among siblings (by display name).</param>
public sealed record NaeTreeNodePlan(
    string Key,
    string? ParentKey,
    string LevelCode,
    string Code,
    IReadOnlyDictionary<string, string> Display,
    IReadOnlyDictionary<string, string> Fields,
    int Ordinal);

/// <summary>Result of <see cref="NaeAfTreeBuilder.Build"/>.</summary>
/// <param name="Nodes">Nodes, parents before children.</param>
/// <param name="Issues">Skipped elements and warnings.</param>
public sealed record NaeTreePlan(IReadOnlyList<NaeTreeNodePlan> Nodes, IReadOnlyList<NaeAfIssueDto> Issues);

/// <summary>
/// Turns the flat AF element list into the NAE tree plan. Pure: no I/O, so every rule has a table-driven test.
/// </summary>
/// <remarks>
/// Rules (assumptions to be confirmed by the first dry run on the real AF, the report lists everything that does not fit):
/// <list type="bullet">
/// <item>an element is an NAE when it carries a <c>Region</c> attribute and has no child element;</item>
/// <item>REGION / AREA come from the <c>Region</c> / <c>On_Offshore</c> attributes, not from path names;</item>
/// <item>SITE is the first path segment between the root (after the leading region / area / "Air" segments) and the NAE, EQUIP is
/// the last one when there are two or more (more than two: warning <c>pathFlattened</c>);</item>
/// <item>an NAE with no SITE segment is skipped (<c>noSite</c>): SITE is a required level, a synthetic site would be invented data.</item>
/// </list>
/// </remarks>
public static class NaeAfTreeBuilder
{
    /// <summary>Area code of a region without areas (a composite-key part cannot be empty).</summary>
    public const string NoArea = "-";

    /// <summary>Builds the plan.</summary>
    /// <param name="snapshot">Elements under the root.</param>
    /// <param name="rootPath">The root element path (its last segment is used to cut the relative path).</param>
    public static NaeTreePlan Build(NaeAfSnapshot snapshot, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rootName = Segments(rootPath).LastOrDefault() ?? "";

        var issues = new List<NaeAfIssueDto>();
        if (!snapshot.IsComplete)
        {
            issues.Add(new NaeAfIssueDto("warning", "sourceIncomplete", "", "The AF listing may be truncated."));
        }

        var elements = snapshot.Elements
            .Select(e => (Element: e, Segments: Segments(e.Path)))
            .ToList();
        // Every proper ancestor path of every element (not only the direct parent: the listing may skip intermediate groups).
        var parentPaths = elements
            .SelectMany(e => Enumerable.Range(1, Math.Max(0, e.Segments.Count - 1)).Select(n => string.Join('\\', e.Segments.Take(n))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nodes = new Dictionary<string, NaeTreeNodePlan>(StringComparer.Ordinal);
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBusiness = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (element, segments) in elements.OrderBy(e => e.Element.Path, StringComparer.OrdinalIgnoreCase))
        {
            var ownPath = string.Join('\\', segments);
            if (string.IsNullOrWhiteSpace(element.Region) || parentPaths.Contains(ownPath))
            {
                if (string.IsNullOrWhiteSpace(element.Region) && !parentPaths.Contains(ownPath))
                {
                    issues.Add(new NaeAfIssueDto("skipped", "noRegion", element.Path, "Leaf element has no Region attribute."));
                }

                continue;
            }

            var naeCode = NaeCodeNormalizer.Normalize(element.Name);
            if (naeCode is null)
            {
                issues.Add(new NaeAfIssueDto("skipped", "noCode", element.Path, "Element name is not an NAE code."));
                continue;
            }

            var region = Upper(element.Region);
            var areaRaw = NaeCodeNormalizer.Clean(element.OnOffshore);
            var area = areaRaw.Length == 0 ? NoArea : Upper(areaRaw);
            var businessKey = $"{naeCode}|{region}|{area}";
            if (!seenBusiness.Add(businessKey))
            {
                issues.Add(new NaeAfIssueDto(
                    "skipped", "duplicateBusinessKey", element.Path, $"NAE {naeCode} appears twice in {region}/{area}."));
                continue;
            }

            var middle = RelativeMiddle(segments, rootName, region, area);
            if (middle.Count == 0)
            {
                issues.Add(new NaeAfIssueDto("skipped", "noSite", element.Path, "No site element between the root and the NAE."));
                continue;
            }

            if (middle.Count > 2)
            {
                issues.Add(new NaeAfIssueDto(
                    "warning", "pathFlattened", element.Path, $"{middle.Count} grouping levels; only the first (site) and last (equipment) are kept."));
            }

            var regionKey = $"R|{region}";
            Ensure(nodes, usedCodes, regionKey, null, NaeTreeSchema.LevelRegion, "R_" + region, NaeCodeNormalizer.Clean(element.Region));

            var parentKey = regionKey;
            var areaTag = area == NoArea ? "NA" : area;
            if (area != NoArea)
            {
                var areaKey = $"A|{region}|{area}";
                Ensure(nodes, usedCodes, areaKey, parentKey, NaeTreeSchema.LevelArea, $"A_{region}_{area}", areaRaw);
                parentKey = areaKey;
            }

            var siteKey = $"S|{region}|{area}|{Upper(middle[0])}";
            Ensure(nodes, usedCodes, siteKey, parentKey, NaeTreeSchema.LevelSite, $"S_{region}_{areaTag}_{Upper(middle[0])}", middle[0]);
            parentKey = siteKey;

            if (middle.Count > 1)
            {
                var equipKey = $"{siteKey}|G|{Upper(middle[^1])}";
                Ensure(nodes, usedCodes, equipKey, parentKey, NaeTreeSchema.LevelEquipment, $"G_{region}_{areaTag}_{Upper(middle[0])}_{Upper(middle[^1])}", middle[^1]);
                parentKey = equipKey;
            }

            var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = First(element.NameEn, naeCode),
            };
            AddIfAny(display, "ru", element.NameRu);
            AddIfAny(display, "kz", element.NameKz);

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NaeTreeSchema.NaeCodeField] = naeCode,
                [NaeTreeSchema.RegionField] = region,
                [NaeTreeSchema.AreaField] = area,
                [NaeTreeSchema.AfPathField] = element.Path,
            };
            var srcKey = $"N|{businessKey}";
            var code = UniqueCode($"N_{region}_{areaTag}_{Upper(naeCode)}", srcKey, usedCodes);
            nodes[srcKey] = new NaeTreeNodePlan(srcKey, parentKey, NaeTreeSchema.LevelSource, code, display, fields, 0);
        }

        // Ordinals among siblings: by English display name, then code.
        var ordered = new List<NaeTreeNodePlan>(nodes.Count);
        var rank = NaeTreeSchema.LevelCodes.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        foreach (var group in nodes.Values.GroupBy(n => n.ParentKey ?? ""))
        {
            var i = 1;
            foreach (var n in group.OrderBy(n => n.Display["en"], StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Code, StringComparer.Ordinal))
            {
                ordered.Add(n with { Ordinal = i++ });
            }
        }

        return new NaeTreePlan(
            [.. ordered.OrderBy(n => rank[n.LevelCode]).ThenBy(n => n.ParentKey, StringComparer.Ordinal).ThenBy(n => n.Ordinal)],
            issues);
    }

    private static List<string> RelativeMiddle(List<string> segments, string rootName, string region, string area)
    {
        var start = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i], rootName, StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }

        var relative = segments.Skip(start).ToList();
        if (relative.Count > 0)
        {
            relative.RemoveAt(relative.Count - 1);
        }

        var dropped = new HashSet<string>(StringComparer.Ordinal)
        {
            "AIR", region, region + "_AIR", "ONSHORE", "OFFSHORE", "ON", "OFF", area,
        };
        while (relative.Count > 0 && dropped.Contains(Upper(relative[0])))
        {
            relative.RemoveAt(0);
        }

        return relative;
    }

    private static void Ensure(
        Dictionary<string, NaeTreeNodePlan> nodes,
        HashSet<string> usedCodes,
        string key,
        string? parentKey,
        string level,
        string codeBase,
        string display)
    {
        if (nodes.ContainsKey(key))
        {
            return;
        }

        nodes[key] = new NaeTreeNodePlan(
            key,
            parentKey,
            level,
            UniqueCode(codeBase, key, usedCodes),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = display },
            new Dictionary<string, string>(StringComparer.Ordinal),
            0);
}

    /// <summary>Deterministic technical code: sanitised, at most 64 characters, unique in the plan.</summary>
    internal static string UniqueCode(string codeBase, string key, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var ch in codeBase)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }

        var code = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        if (code.Length > 64)
        {
            code = code[..55] + "_" + hash;
        }

        if (!used.Add(code))
        {
            code = code[..Math.Min(code.Length, 55)] + "_" + hash;
            used.Add(code);
        }

        return code;
    }

    private static List<string> Segments(string? path)
        => (path ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Upper(string? text)
    {
        var sb = new StringBuilder();
        foreach (var ch in NaeCodeNormalizer.Clean(text).ToUpperInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return sb.ToString();
    }

    private static string First(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : NaeCodeNormalizer.Clean(value);

    private static void AddIfAny(Dictionary<string, string> target, string lang, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[lang] = NaeCodeNormalizer.Clean(value);
        }
    }
}
