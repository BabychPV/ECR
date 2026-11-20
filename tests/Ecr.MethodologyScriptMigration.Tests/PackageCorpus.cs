using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.MethodologyScriptMigration.Conversion;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Будує <see cref="VersionSource"/> з реального пакета AF
/// (<c>docs/delivery/reference-data/land-template/af-land-package.json</c>) за правилами задачі T5.2.
/// </summary>
internal static partial class PackageCorpus
{
    public const string LibraryCode = "Common";

    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(5);

    [GeneratedRegex(@"!([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex FormulaRef();

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Ecr.sln not found above " + AppContext.BaseDirectory);
    }

    public static IReadOnlyList<VersionSource> Load()
    {
        var folder = Path.Combine(RepoRoot(), "docs", "delivery", "reference-data", "land-template");
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "af-land-package.json")));
        using var rules = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "af-land-category-rules.json")));

        var categoryRules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.RootElement.GetProperty("rules").EnumerateArray())
        {
            categoryRules[rule.GetProperty("methodology").GetString()!] = rule.GetProperty("expression").GetString()!;
        }

        var result = new List<VersionSource>();
        List<string>? libraryMembers = null;
        foreach (var methodology in package.RootElement.GetProperty("methodologies").EnumerateArray())
        {
            var name = methodology.GetProperty("name").GetString()!;
            var version = methodology.GetProperty("versions")[0];
            var isLibrary = name == LibraryCode;
            var formulas = LatestFormulas(version.GetProperty("formulas"));
            var ordered = Order(formulas);
            if (isLibrary)
            {
                libraryMembers = [.. ordered.Select(f => f.Name)];
            }

            var own = ordered.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var needsLibrary = !isLibrary && libraryMembers is not null
                               && ordered.SelectMany(f => FormulaRef().Matches(f.Text).Select(m => m.Groups[1].Value))
                                   .Any(r => !own.Contains(r) && libraryMembers.Contains(r, StringComparer.OrdinalIgnoreCase));

            var constants = Constants(version.GetProperty("constants"));
            string? categoryRule = null;
            if (!isLibrary)
            {
                if (categoryRules.TryGetValue(name, out var fromFile))
                {
                    categoryRule = fromFile;
                }
                else if (version.TryGetProperty("categoryRule", out var rule))
                {
                    categoryRule = rule.GetProperty("expression").GetString();
                }
                else if (own.Contains("ECW_Category"))
                {
                    categoryRule = "!ECW_Category";
                }
            }

            var categories = categoryRule is null
                ? []
                : constants.Select(c => c.Category).Where(c => !string.IsNullOrEmpty(c) && c != "Common")
                    .Select(c => c!).Distinct(StringComparer.Ordinal).ToList();

            var sourceFormulas = ordered.Select((f, i) => new SourceFormula(
                f.Name, f.Text, i,
                f.Scope == "Row" ? MethodologyFormulaScope.Row : MethodologyFormulaScope.Substance,
                IsVisible: false,
                f.ResultType == "Text" ? FormulaResultType.Text : FormulaResultType.Number)).ToList();

            var outputs = isLibrary
                ? []
                : ordered
                    .Where(f => f.Name.StartsWith("ECW_EC_tons_", StringComparison.Ordinal)
                                || f.Name.StartsWith("ECW_EC_gsec_", StringComparison.Ordinal))
                    .Select((f, i) => new SourceOutput(
                        f.Name, f.Name.StartsWith("ECW_EC_tons_", StringComparison.Ordinal) ? "t" : "g_per_s", true, i))
                    .ToList();

            result.Add(new VersionSource
            {
                MethodologyCode = name,
                VersionLabel = version.GetProperty("version").GetString()!,
                IsLibrary = isLibrary,
                NumericMode = NumericMode.Legacy,
                EffectiveFrom = new DateOnly(2024, 1, 1),
                Formulas = sourceFormulas,
                Outputs = outputs,
                Constants = constants,
                Categories = categories,
                CategoryRule = categoryRule,
                Imports = needsLibrary ? [new SourceImport(LibraryCode, libraryMembers!)] : [],
                RowsClauses = isLibrary ? [] : [SyntheticRows()],
            });
        }

        return result;
    }

    private static SourceRowsClause SyntheticRows()
        => new(
            ["T3_00"],
            [new SourceRule([new SourceMatchPair("Land_TypeFuel", SourceMatchValue.OfEntry("LAND_FUEL", "Gasoline"))])],
            [new SourceActiveColumn("Land_Status", "LAND_GENERIC_EQUIPSTATUS", "Running")]);

    private sealed record RawFormula(string Name, string Text, string StartDate, string? Scope, string ResultType);

    private static List<RawFormula> LatestFormulas(JsonElement formulas)
    {
        var all = formulas.EnumerateArray().Select(f => new RawFormula(
            f.GetProperty("name").GetString()!,
            f.GetProperty("text").GetString()!,
            f.GetProperty("startDate").GetString()!,
            f.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
            f.TryGetProperty("resultType", out var rt) ? rt.GetString()! : "Number")).ToList();

        var latest = new List<RawFormula>();
        foreach (var group in all.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            latest.Add(group.OrderByDescending(f => f.StartDate, StringComparer.Ordinal).First());
        }

        // Порядок виходу — як у пакеті (за першою появою імені).
        var firstSeen = all.Select((f, i) => (f.Name, i)).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(x => x.i), StringComparer.OrdinalIgnoreCase);
        return [.. latest.OrderBy(f => firstSeen[f.Name])];
    }

    /// <summary>Топологічний порядок із збереженням порядку пакета там, де залежностей немає.</summary>
    private static List<RawFormula> Order(List<RawFormula> formulas)
    {
        var byName = formulas.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RawFormula>();
        var pending = new List<RawFormula>(formulas);
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(f => FormulaRef().Matches(f.Text)
                .Select(m => m.Groups[1].Value)
                .Where(r => byName.ContainsKey(r) && !string.Equals(r, f.Name, StringComparison.OrdinalIgnoreCase))
                .All(done.Contains))
                ?? throw new InvalidOperationException("Formula dependency cycle among " + string.Join(", ", pending.Select(p => p.Name)));
            pending.Remove(next);
            done.Add(next.Name);
            result.Add(next);
        }

        // Row-формули не можуть читати Substance-формули: переносимо їх уперед, якщо вони не залежать від решти.
        return result;
    }

    private static List<SourceConstant> Constants(JsonElement constants)
    {
        var list = new List<SourceConstant>();
        foreach (var constant in constants.EnumerateArray())
        {
            var code = constant.GetProperty("name").GetString()!;
            foreach (var value in constant.GetProperty("values").EnumerateArray())
            {
                var raw = value.GetProperty("value").GetString() ?? string.Empty;
                var category = value.TryGetProperty("category", out var cat) ? cat.GetString() : null;
                var from = ToLocalDate(value.GetProperty("startDate").GetString()!, exclusiveEnd: false);
                var to = ToLocalDate(value.GetProperty("endDate").GetString()!, exclusiveEnd: true);
                if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    list.Add(SourceConstant.OfNumber(code, number, category, null, from, to));
                }
                else if (!string.IsNullOrWhiteSpace(raw))
                {
                    list.Add(SourceConstant.OfText(code, raw, category, null, from, to));
                }
            }
        }

        return list;
    }

    private static DateOnly? ToLocalDate(string iso, bool exclusiveEnd)
    {
        var utc = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if (utc.Year >= 9000)
        {
            return null;
        }

        var local = utc.ToOffset(LocalOffset);
        var date = DateOnly.FromDateTime(local.DateTime);

        // Кінець «31.12 23:59:59» — це виключна межа «наступний день».
        return exclusiveEnd && local.TimeOfDay >= new TimeSpan(23, 59, 0) ? date.AddDays(1) : date;
    }
}
