using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Analysis;

/// <summary>Одиниця, яку не вдалося звести до коду каталогу ECR: де, що було в AF і чому не зведено.</summary>
public sealed record UnitIssue(string Methodology, string MethodologyVersion, string Constant, string RawUnit, string Reason);

/// <summary>Підсумок по одиницях констант: скільки зведено (у т.ч. псевдонімом) і що лишилось нерезолвним.</summary>
public sealed record UnitSummary(
    int Constants,
    int Resolved,
    int ResolvedByAlias,
    int Unresolved,
    int WithoutUnit,
    IReadOnlyList<TokenCount> UnresolvedUnits,
    IReadOnlyList<UnitIssue> Issues);

/// <summary>Скільки формул дають текст і скільки з них склеюють його оператором <c>+</c> (у ECR склейка — <c>&amp;</c>).</summary>
public sealed record FormulaTypeSummary(int Text, int Number, int TextPlusFormulas, int TextPlusOperators, IReadOnlyList<string> TextPlusSamples);

/// <summary>Потреба в колонках/джерелі, яку формули мають, а імпорт сам не закриває: звітується, не мовчки.</summary>
public sealed record ColumnNeed(string Kind, string Methodology, string MethodologyVersion, int Formulas, IReadOnlyList<string> Samples);

public static partial class ImportDiagnostics
{
    public const string CalendarMonth = "calendar:Month";
    public const string CalendarDays = "calendar:Days";
    public const string Hse400 = "hse400";

    [GeneratedRegex(@"@(Month|Days)(?!\w)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CalendarInTextRegex();

    public static FormulaTypeSummary FormulaTypes(MethodologyModel model, IReadOnlyDictionary<string, FormulaShape> shapes)
    {
        var text = 0;
        var plusFormulas = 0;
        var plusOperators = 0;
        var samples = new List<string>();
        foreach (var f in model.Formulas)
        {
            var shape = shapes[f.Key];
            if (shape.Kind == FormulaResultKind.Text)
            {
                text++;
            }

            if (shape.TextPlus > 0)
            {
                plusFormulas++;
                plusOperators += shape.TextPlus;
                samples.Add(f.Key);
            }
        }

        return new FormulaTypeSummary(
            text, model.Formulas.Count - text, plusFormulas, plusOperators,
            [.. samples.Order(StringComparer.Ordinal).Take(20)]);
    }

    public static UnitSummary Units(MethodologyModel model)
    {
        var issues = new List<UnitIssue>();
        var constants = 0;
        var resolved = 0;
        var byAlias = 0;
        var withoutUnit = 0;
        foreach (var g in model.Constants.GroupBy(c => (c.Methodology, c.MethodologyVersion, c.Name)))
        {
            constants++;
            var raw = g.Select(c => c.Unit).FirstOrDefault(u => u.Length > 0) ?? string.Empty;
            var r = UnitCanonicalizer.Resolve(raw);
            if (r.Code is not null)
            {
                resolved++;
                if (!string.Equals(r.Code, r.Raw, StringComparison.Ordinal))
                {
                    byAlias++;
                }
            }
            else if (raw.Length == 0)
            {
                withoutUnit++;
            }
            else
            {
                issues.Add(new UnitIssue(g.Key.Methodology, g.Key.MethodologyVersion, g.Key.Name, raw, r.Reason!));
            }
        }

        issues.Sort((a, b) => string.CompareOrdinal(
            $"{a.Methodology}\u001f{a.MethodologyVersion}\u001f{a.Constant}", $"{b.Methodology}\u001f{b.MethodologyVersion}\u001f{b.Constant}"));

        var tokens = issues.GroupBy(i => i.RawUnit, StringComparer.Ordinal)
            .Select(g => new TokenCount(g.Key, g.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Token, StringComparer.Ordinal)
            .ToList();

        return new UnitSummary(constants, resolved, byAlias, issues.Count, withoutUnit, tokens, issues);
    }

    /// <summary>
    /// Формули з аргументом/посиланням <c>@Month</c>/<c>@Days</c> потребують календарних колонок періоду, а методології
    /// <c>HSE400*</c> — окремого джерела колонок: імпорт їх не створює, тож вони виходять у звіт.
    /// </summary>
    public static IReadOnlyList<ColumnNeed> ColumnNeeds(MethodologyModel model)
    {
        var buckets = new SortedDictionary<(string Kind, string M, string V), List<string>>(
            Comparer<(string Kind, string M, string V)>.Create((a, b) =>
            {
                var c = string.CompareOrdinal(a.Kind, b.Kind);
                c = c != 0 ? c : string.CompareOrdinal(a.M, b.M);
                return c != 0 ? c : string.CompareOrdinal(a.V, b.V);
            }));

        void Put(string kind, FormulaDef f)
        {
            var key = (kind, f.Methodology, f.MethodologyVersion);
            if (!buckets.TryGetValue(key, out var list))
            {
                buckets[key] = list = [];
            }

            list.Add(f.Key);
        }

        foreach (var f in model.Formulas)
        {
            var named = new HashSet<string>(
                f.Arguments.Split(';').Select(a => a.Trim().TrimStart('@')), StringComparer.OrdinalIgnoreCase);
            foreach (Match m in CalendarInTextRegex().Matches(f.Text))
            {
                named.Add(m.Groups[1].Value);
            }

            if (named.Contains("Month"))
            {
                Put(CalendarMonth, f);
            }

            if (named.Contains("Days"))
            {
                Put(CalendarDays, f);
            }

            if (f.Methodology.StartsWith("HSE400", StringComparison.OrdinalIgnoreCase)
                || f.Name.StartsWith("HSE400", StringComparison.OrdinalIgnoreCase))
            {
                Put(Hse400, f);
            }
        }

        return [.. buckets.Select(kv => new ColumnNeed(
            kv.Key.Kind, kv.Key.M, kv.Key.V, kv.Value.Count, [.. kv.Value.Order(StringComparer.Ordinal).Take(5)]))];
    }
}
