using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Reporting;

/// <summary>Чому посилання не резолвиться — від найпевнішого виправлення до «потрібен методолог».</summary>
public enum UnresolvedCategory
{
    /// <summary>Токен не є іменем (порожній, пробіли всередині, недопустимі символи).</summary>
    InvalidToken,

    /// <summary>Ім'я є, але інший регістр — механічне виправлення.</summary>
    CaseMismatch,

    /// <summary>Константа названа значенням <c>CInfo_Parameter</c>, а не іменем константи.</summary>
    ParameterNameUsed,

    /// <summary>Визначення є, але в методології/версії, що не видна з місця посилання (не своя й не бібліотека).</summary>
    DefinedElsewhere,

    /// <summary>Найближче ім'я відрізняється на 1–2 символи — ймовірна описка.</summary>
    LikelyTypo,

    /// <summary>Визначення немає ніде в експорті.</summary>
    Missing,
}

/// <summary>Рекомендація по одному нерезолвному посиланню (рядок звіту для методолога).</summary>
public sealed record UnresolvedRecommendation(
    UnresolvedReference Reference,
    UnresolvedCategory Category,
    string? Candidate,
    string Action,
    bool NeedsMethodologist,
    bool BlocksCalculation);

/// <summary>
/// Звіт «нерезолвні посилання з рекомендацією по кожному» поверх сухого прогону. Лише читає модель і
/// звіт аналізу; нічого не виправляє сам — рішення про заміну за методологом (кандидат лише підказка).
/// Детермінований: той самий вхід — той самий Markdown байт у байт.
/// </summary>
public static partial class UnresolvedReferenceReport
{
    [GeneratedRegex(@"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public static IReadOnlyList<UnresolvedRecommendation> Recommend(MethodologyModel model, AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(report);

        var formulaNames = model.Formulas.Select(f => f.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var constantNames = model.Constants.Select(c => c.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var formulaHomes = Homes(model.Formulas.Select(f => (f.Name, $"{f.Methodology}/{f.MethodologyVersion}")));
        var constantHomes = Homes(model.Constants.Select(c => (c.Name, $"{c.Methodology}/{c.MethodologyVersion}")));
        var parameters = model.Constants
            .Where(c => c.Parameter.Length > 0 && !string.Equals(c.Parameter, c.Name, StringComparison.Ordinal))
            .GroupBy(c => c.Parameter, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Name).Order(StringComparer.Ordinal).First(), StringComparer.Ordinal);

        return report.Unresolved.Select(u => u.Kind == ReferenceKind.Formula
                ? Classify(u, formulaNames, formulaHomes, null)
                : Classify(u, constantNames, constantHomes, parameters))
            .ToList();
    }

    private static UnresolvedRecommendation Classify(
        UnresolvedReference u,
        IReadOnlyList<string> names,
        Dictionary<string, List<string>> homes,
        Dictionary<string, string>? parameters)
    {
        var what = u.Kind == ReferenceKind.Formula ? "формулу" : "константу";
        var prefix = u.Kind == ReferenceKind.Formula ? "!" : "CST.";
        var blocks = u.SourceAvailable;

        if (!IdentifierRegex().IsMatch(u.Token))
        {
            return new(u, UnresolvedCategory.InvalidToken, null,
                $"виправити токен «{u.RawToken}» в FInfo_Arguments: це не ім'я (порожній сегмент між «;», пробіл усередині або недопустимий символ)",
                NeedsMethodologist: true, blocks);
        }

        if (homes.TryGetValue(u.Token, out var where))
        {
            return new(u, UnresolvedCategory.DefinedElsewhere, null,
                $"{what} {prefix}{u.Token} визначено лише в {string.Join(", ", where)}: скопіювати її у {u.Methodology}/{u.MethodologyVersion} або перенести в бібліотеку; методолог обирає, яка версія правильна",
                NeedsMethodologist: true, blocks);
        }

        var caseMatch = names.FirstOrDefault(n => string.Equals(n, u.Token, StringComparison.OrdinalIgnoreCase));
        if (caseMatch is not null)
        {
            return new(u, UnresolvedCategory.CaseMismatch, caseMatch,
                $"замінити {prefix}{u.Token} на {prefix}{caseMatch} (різниця лише в регістрі; CLR порівнює імена з урахуванням регістру)",
                NeedsMethodologist: false, blocks);
        }

        if (parameters is not null && parameters.TryGetValue(u.Token, out var byParameter))
        {
            return new(u, UnresolvedCategory.ParameterNameUsed, byParameter,
                $"замінити CST.{u.Token} на CST.{byParameter}: «{u.Token}» — це CInfo_Parameter константи, а не її ім'я",
                NeedsMethodologist: false, blocks);
        }

        var typo = Nearest(u.Token, names);
        if (typo is not null)
        {
            return new(u, UnresolvedCategory.LikelyTypo, typo,
                $"ймовірна описка: замінити {prefix}{u.Token} на {prefix}{typo} після підтвердження методолога",
                NeedsMethodologist: true, blocks);
        }

        return new(u, UnresolvedCategory.Missing, null,
            blocks
                ? $"{what} {prefix}{u.Token} немає в експорті: методолог надає визначення або прибирає посилання з {u.Formula}"
                : $"{what} {prefix}{u.Token} немає в експорті, але {u.Formula} вимкнена (IsAvailable=False): можна імпортувати без неї, рішення фіксує методолог",
            NeedsMethodologist: true, blocks);
    }

    private static Dictionary<string, List<string>> Homes(IEnumerable<(string Name, string Home)> items)
        => items.GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Home).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

    /// <summary>Найближче ім'я з відстанню Левенштейна 1 (ім'я від 4 символів) або 2 (від 8); нічия — нема кандидата.</summary>
    public static string? Nearest(string token, IReadOnlyList<string> names)
    {
        var limit = token.Length >= 8 ? 2 : token.Length >= 4 ? 1 : 0;
        if (limit == 0)
        {
            return null;
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        var tie = false;
        foreach (var n in names)
        {
            if (Math.Abs(n.Length - token.Length) > limit)
            {
                continue;
            }

            var d = Distance(token, n, limit);
            if (d < bestDistance)
            {
                (best, bestDistance, tie) = (n, d, false);
            }
            else if (d == bestDistance)
            {
                tie = true;
            }
        }

        return bestDistance <= limit && !tie ? best : null;
    }

    private static int Distance(string a, string b, int limit)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    public static string ToMarkdown(IReadOnlyList<UnresolvedRecommendation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Нерезолвні посилання імпорту методологій з AF");
        sb.AppendLine();
        sb.AppendLine(ci, $"Усього: {items.Count}; у формулах з IsAvailable=True: {items.Count(i => i.BlocksCalculation)}; потребують рішення методолога: {items.Count(i => i.NeedsMethodologist)}.");
        sb.AppendLine();
        sb.AppendLine("Згенеровано `Ecr.MethodologyImport unresolved`. Кандидат — лише підказка: інструмент нічого не виправляє сам.");
        sb.AppendLine();
        sb.AppendLine("| Категорія | Кількість |");
        sb.AppendLine("|---|---|");
        foreach (var g in items.GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            sb.AppendLine(ci, $"| {Title(g.Key)} | {g.Count()} |");
        }

        sb.AppendLine();
        sb.AppendLine("| # | Методологія | Формула | Посилання | Категорія | Кандидат | Активна | Рекомендація |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        var n = 0;
        foreach (var i in items.OrderBy(i => i.Category).ThenBy(i => i.Reference.Methodology, StringComparer.Ordinal)
                     .ThenBy(i => i.Reference.Formula, StringComparer.Ordinal).ThenBy(i => i.Reference.Token, StringComparer.Ordinal))
        {
            var r = i.Reference;
            sb.AppendLine(ci,
                $"| {++n} | {Cell(r.Methodology)}/{Cell(r.MethodologyVersion)} | {Cell(r.Formula)}/{Cell(r.FormulaVersion)} | `{Cell(r.RawToken.Trim())}` | {Title(i.Category)} | {(i.Candidate is null ? "—" : "`" + Cell(i.Candidate) + "`")} | {(r.SourceAvailable ? "так" : "ні")} | {Cell(i.Action)} |");
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static string Title(UnresolvedCategory c) => c switch
    {
        UnresolvedCategory.InvalidToken => "некоректний токен",
        UnresolvedCategory.CaseMismatch => "регістр",
        UnresolvedCategory.ParameterNameUsed => "ім'я параметра замість константи",
        UnresolvedCategory.DefinedElsewhere => "в іншій методології/версії",
        UnresolvedCategory.LikelyTypo => "ймовірна описка",
        _ => "немає визначення",
    };
}
