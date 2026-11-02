// src/Ecr.Application/Registries/Nae/NaeSheetTreeBuilder.cs
using System.Globalization;
using System.Text;

namespace Ecr.Application.Registries.Nae;

/// <summary>Result of <see cref="NaeSheetTreeBuilder.Build"/>.</summary>
/// <param name="Nodes">Nodes, parents before children (the same shape and technical codes as the AF load).</param>
/// <param name="Issues">File findings (errors and warnings), in row order.</param>
/// <param name="RowsRead">Non-empty data rows.</param>
/// <param name="RowOf">First Excel row of every node, by node key.</param>
/// <param name="HeaderFound">The header row was found.</param>
public sealed record NaeSheetPlan(
    IReadOnlyList<NaeTreeNodePlan> Nodes,
    IReadOnlyList<NaeTreeImportIssueDto> Issues,
    int RowsRead,
    IReadOnlyDictionary<string, int> RowOf,
    bool HeaderFound);

/// <summary>
/// Turns the template sheet into the NAE tree plan. Pure: no I/O. One row is one path Region → Area → Site → Equipment → NAE;
/// a row without an NAE code creates only its group nodes, and its names go to the deepest of them.
/// </summary>
/// <remarks>
/// Technical codes and the business key are the AF load's (<see cref="NaeAfTreeBuilder"/>), so a tree loaded from AF and one
/// imported from Excel recognise each other's nodes. Area: the <c>Area</c> column wins, <c>On_Offshore</c> is the fallback, both
/// filled and different is an error (coordinator 02.11).
/// </remarks>
public static class NaeSheetTreeBuilder
{
    /// <summary>Rows searched for the header.</summary>
    public const int HeaderSearchRows = 10;

    /// <summary>Builds the plan.</summary>
    /// <param name="sheet">The sheet.</param>
    public static NaeSheetPlan Build(NaeTreeSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        var issues = new List<NaeTreeImportIssueDto>();
        var (headerRow, columns) = FindHeader(sheet.Rows);
        if (headerRow == 0)
        {
            issues.Add(new NaeTreeImportIssueDto(
                "error", "headerNotFound", 0, null, null, $"No header row with «{NaeTreeImportLayout.Region}» and «{NaeTreeImportLayout.NaeCode}» in the first {HeaderSearchRows} rows."));
            return new NaeSheetPlan([], issues, 0, new Dictionary<string, int>(), false);
        }

        var nodes = new Dictionary<string, NaeTreeNodePlan>(StringComparer.Ordinal);
        var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBusiness = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowsRead = 0;

        for (var index = headerRow; index < sheet.Rows.Count; index++)
        {
            var cells = sheet.Rows[index];
            var row = index + 1;
            string? Cell(string column)
                => columns.TryGetValue(column, out var c) && c < cells.Count && !string.IsNullOrWhiteSpace(cells[c]) ? cells[c]!.Trim() : null;

            if (NaeTreeImportLayout.Columns.All(c => Cell(c) is null))
            {
                continue;
            }

            rowsRead++;
            var regionText = Cell(NaeTreeImportLayout.Region);
            var codeText = Cell(NaeTreeImportLayout.NaeCode);
            if (regionText is null)
            {
                issues.Add(Error("regionRequired", row, NaeTreeImportLayout.Region, codeText, "The row has no region."));
                continue;
            }

            var areaText = Cell(NaeTreeImportLayout.Area);
            var onOffText = Cell(NaeTreeImportLayout.OnOffshore);
            if (areaText is not null && onOffText is not null
                && !string.Equals(NaeAfTreeBuilder.Upper(areaText), NaeAfTreeBuilder.Upper(onOffText), StringComparison.Ordinal))
            {
                issues.Add(Error("areaMismatch", row, NaeTreeImportLayout.OnOffshore, codeText, $"Area «{areaText}» and On_Offshore «{onOffText}» differ."));
                continue;
            }

            var siteText = Cell(NaeTreeImportLayout.Site);
            var equipText = Cell(NaeTreeImportLayout.Equipment);
            if (siteText is null && (equipText is not null || codeText is not null))
            {
                issues.Add(Error("siteRequired", row, NaeTreeImportLayout.Site, codeText, "Equipment and NAE need a site (a required level)."));
                continue;
            }

            string? naeCode = null;
            if (codeText is not null)
            {
                // Letters, digits, '_' and '-' only: a fractional number ("1.5") or a sentence in the code column is a typo, not a
                // code (AF element names are never that).
                naeCode = NaeCodeNormalizer.Normalize(codeText);
                if (naeCode is null || !naeCode.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                {
                    issues.Add(Error("badCode", row, NaeTreeImportLayout.NaeCode, null, $"«{codeText}» is not an NAE code."));
                    continue;
                }
            }

            var region = NaeAfTreeBuilder.Upper(regionText);
            var areaRaw = areaText ?? onOffText;
            var area = areaRaw is null ? NaeAfTreeBuilder.NoArea : NaeAfTreeBuilder.Upper(areaRaw);
            var areaTag = area == NaeAfTreeBuilder.NoArea ? "NA" : area;
            var businessKey = naeCode is null ? null : $"{naeCode}|{region}|{area}";
            if (businessKey is not null && seenBusiness.TryGetValue(businessKey, out var firstRow))
            {
                issues.Add(Error("duplicateInFile", row, NaeTreeImportLayout.NaeCode, naeCode, $"NAE {naeCode} in {region}/{area} is already on row {firstRow.ToString(CultureInfo.InvariantCulture)}."));
                continue;
            }

            var names = Names(Cell(NaeTreeImportLayout.NameEn), Cell(NaeTreeImportLayout.NameRu), Cell(NaeTreeImportLayout.NameKz));

            var regionKey = $"R|{region}";
            var deepest = Ensure(nodes, rowOf, usedCodes, regionKey, null, NaeTreeSchema.LevelRegion, "R_" + region, regionText, row);
            if (area != NaeAfTreeBuilder.NoArea)
            {
                deepest = Ensure(nodes, rowOf, usedCodes, $"A|{region}|{area}", deepest, NaeTreeSchema.LevelArea, $"A_{region}_{area}", areaRaw!, row);
            }

            if (siteText is not null)
            {
                var site = NaeAfTreeBuilder.Upper(siteText);
                var siteKey = $"S|{region}|{area}|{site}";
                deepest = Ensure(nodes, rowOf, usedCodes, siteKey, deepest, NaeTreeSchema.LevelSite, $"S_{region}_{areaTag}_{site}", siteText, row);
                if (equipText is not null)
                {
                    var equip = NaeAfTreeBuilder.Upper(equipText);
                    deepest = Ensure(nodes, rowOf, usedCodes, $"{siteKey}|G|{equip}", deepest, NaeTreeSchema.LevelEquipment, $"G_{region}_{areaTag}_{site}_{equip}", equipText, row);
                }
            }

            if (naeCode is null)
            {
                // A group-only row names its deepest group (the first row that names it wins).
                if (names.Count > 0 && named.Add(deepest))
                {
                    var node = nodes[deepest];
                    var display = new Dictionary<string, string>(node.Display, StringComparer.OrdinalIgnoreCase);
                    foreach (var (lang, text) in names)
                    {
                        display[lang] = text;
                    }

                    nodes[deepest] = node with { Display = display };
                }

                continue;
            }

            seenBusiness[businessKey!] = row;
            var srcDisplay = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
            srcDisplay.TryAdd("en", naeCode);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NaeTreeSchema.NaeCodeField] = naeCode,
                [NaeTreeSchema.RegionField] = region,
                [NaeTreeSchema.AreaField] = area,
            };
            var srcKey = $"N|{businessKey}";
            var code = NaeAfTreeBuilder.UniqueCode($"N_{region}_{areaTag}_{NaeAfTreeBuilder.Upper(naeCode)}", srcKey, usedCodes);
            nodes[srcKey] = new NaeTreeNodePlan(srcKey, deepest, NaeTreeSchema.LevelSource, code, srcDisplay, fields, 0);
            rowOf[srcKey] = row;
        }

        // Ordinals among siblings: by English display name, then code (as the AF load).
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

        return new NaeSheetPlan(
            [.. ordered.OrderBy(n => rank[n.LevelCode]).ThenBy(n => n.ParentKey, StringComparer.Ordinal).ThenBy(n => n.Ordinal)],
            issues,
            rowsRead,
            rowOf,
            true);
    }

    /// <summary>Header text folded for matching: lower case, letters and digits only (<c>NAE code</c> = <c>nae_code</c>).</summary>
    internal static string Fold(string? text)
    {
        var sb = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static (int HeaderRow, Dictionary<string, int> Columns) FindHeader(IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var wanted = NaeTreeImportLayout.Columns.ToDictionary(Fold, c => c, StringComparer.Ordinal);
        for (var r = 0; r < Math.Min(HeaderSearchRows, rows.Count); r++)
        {
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (wanted.TryGetValue(Fold(rows[r][c]), out var name))
                {
                    columns.TryAdd(name, c);
                }
            }

            if (columns.ContainsKey(NaeTreeImportLayout.Region) && columns.ContainsKey(NaeTreeImportLayout.NaeCode))
            {
                return (r + 1, columns);
            }
        }

        return (0, []);
    }

    private static Dictionary<string, string> Names(string? en, string? ru, string? kz)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (en is not null)
        {
            names["en"] = NaeCodeNormalizer.Clean(en);
        }

        if (ru is not null)
        {
            names["ru"] = NaeCodeNormalizer.Clean(ru);
        }

        if (kz is not null)
        {
            names["kz"] = NaeCodeNormalizer.Clean(kz);
        }

        return names;
    }

    private static string Ensure(
        Dictionary<string, NaeTreeNodePlan> nodes,
        Dictionary<string, int> rowOf,
        HashSet<string> usedCodes,
        string key,
        string? parentKey,
        string level,
        string codeBase,
        string display,
        int row)
    {
        if (!nodes.ContainsKey(key))
        {
            nodes[key] = new NaeTreeNodePlan(
                key,
                parentKey,
                level,
                NaeAfTreeBuilder.UniqueCode(codeBase, key, usedCodes),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = NaeCodeNormalizer.Clean(display) },
                new Dictionary<string, string>(StringComparer.Ordinal),
                0);
            rowOf[key] = row;
        }

        return key;
    }

    private static NaeTreeImportIssueDto Error(string code, int row, string? column, string? naeCode, string detail)
        => new("error", code, row, column, naeCode, detail);
}
