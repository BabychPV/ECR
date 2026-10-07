using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Analysis;

public enum FormulaResultKind
{
    Number,
    Text,
}

/// <summary>Висновок про формулу: тип результату й скільки операторів <c>+</c> склеюють текст (у ECR склейка — <c>&amp;</c>).</summary>
public sealed record FormulaShape(FormulaResultKind Kind, int TextPlus);

/// <summary>
/// Тип результату формули AF з її ТЕКСТУ (AF типу не зберігає: усі формули читались як Number). Формула — Text, якщо:
/// рядковий літерал <c>'…'</c>; склейка <c>&amp;</c>; <c>+</c>, де хоч один доданок текстовий; <c>if(у;А;Б)</c>, де А і Б текстові;
/// <c>!Формула</c>, що сама Text (фіксована точка по посиланнях); <c>CST.Константа</c> з нечисловими значеннями без одиниці.
/// Усе інше — Number («не знаємо» = Number, як і було; здогадуватись про текст дорожче, ніж пропустити).
/// </summary>
public static partial class FormulaTypeInference
{
    [GeneratedRegex(@"^if\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IfCallRegex();

    [GeneratedRegex(@"^!([A-Za-z_]\w*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaRefRegex();

    [GeneratedRegex(@"^CST\.([A-Za-z_]\w*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConstantRefRegex();

    /// <summary>Ключ — <see cref="FormulaDef.Key"/>.</summary>
    public static IReadOnlyDictionary<string, FormulaShape> Infer(MethodologyModel model, string library)
    {
        ArgumentNullException.ThrowIfNull(model);

        var textConstants = TextConstants(model);
        var formulaKeys = new Dictionary<(string, string), Dictionary<string, List<string>>>();
        var libraryKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in model.Formulas)
        {
            var scope = (f.Methodology, f.MethodologyVersion);
            if (!formulaKeys.TryGetValue(scope, out var names))
            {
                formulaKeys[scope] = names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }

            Add(names, f.Name, f.Key);
            if (string.Equals(f.Methodology, library, StringComparison.Ordinal))
            {
                Add(libraryKeys, f.Name, f.Key);
            }
        }

        var shapes = model.Formulas
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, _ => new FormulaShape(FormulaResultKind.Number, 0), StringComparer.Ordinal);

        // Фіксована точка: Number → Text лише зростає, тож цикл скінченний.
        for (var pass = 0; pass < 64; pass++)
        {
            var changed = false;
            foreach (var f in model.Formulas)
            {
                var scope = (f.Methodology, f.MethodologyVersion);
                bool FormulaIsText(string name)
                {
                    var own = formulaKeys.TryGetValue(scope, out var n) && n.TryGetValue(name, out var l) ? l : null;
                    var keys = own ?? (libraryKeys.TryGetValue(name, out var lib) ? lib : null);
                    return keys is not null && keys.Any(k => shapes[k].Kind == FormulaResultKind.Text);
                }

                bool ConstantIsText(string name)
                    => textConstants.Contains((f.Methodology, f.MethodologyVersion, name))
                       || textConstants.Contains((library, string.Empty, name));

                var (isText, plus) = Evaluate(f.Text, FormulaIsText, ConstantIsText);
                var next = new FormulaShape(isText ? FormulaResultKind.Text : FormulaResultKind.Number, plus);
                if (next != shapes[f.Key])
                {
                    shapes[f.Key] = next;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return shapes;
    }

    /// <summary>Текст вираз → (чи текст, скільки <c>+</c> склеюють текст).</summary>
    public static (bool IsText, int TextPlus) Evaluate(
        string expression, Func<string, bool> formulaIsText, Func<string, bool> constantIsText)
    {
        var s = StripParens((expression ?? string.Empty).Trim());
        if (s.Length == 0)
        {
            return (false, 0);
        }

        if (SplitTop(s, ['&']).Count > 1)
        {
            return (true, 0);
        }

        var parts = SplitTop(s, ['+']);
        if (parts.Count > 1)
        {
            var results = parts.Select(p => Evaluate(p, formulaIsText, constantIsText)).ToList();
            var inner = results.Sum(r => r.TextPlus);
            return results.Any(r => r.IsText) ? (true, inner + parts.Count - 1) : (false, inner);
        }

        if (IsStringLiteral(s))
        {
            return (true, 0);
        }

        if (IfCallRegex().IsMatch(s))
        {
            var open = s.IndexOf('(');
            if (MatchClose(s, open) == s.Length - 1)
            {
                var args = SplitTop(s[(open + 1)..^1], [',', ';']);
                if (args.Count == 3)
                {
                    var a = Evaluate(args[1], formulaIsText, constantIsText);
                    var b = Evaluate(args[2], formulaIsText, constantIsText);
                    return a.IsText && b.IsText ? (true, a.TextPlus + b.TextPlus) : (false, 0);
                }
            }

            return (false, 0);
        }

        var formula = FormulaRefRegex().Match(s);
        if (formula.Success)
        {
            return (formulaIsText(formula.Groups[1].Value), 0);
        }

        var constant = ConstantRefRegex().Match(s);
        return constant.Success ? (constantIsText(constant.Groups[1].Value), 0) : (false, 0);
    }

    /// <summary>Константа текстова: є значення, усі непорожні, жодне не число, одиниці немає.</summary>
    private static HashSet<(string, string, string)> TextConstants(MethodologyModel model)
    {
        var result = new HashSet<(string, string, string)>();
        foreach (var g in model.Constants.GroupBy(c => (c.Methodology, c.MethodologyVersion, c.Name)))
        {
            var rows = g.Where(c => c.HasValue).ToList();
            var unit = g.Any(c => c.Unit.Length > 0);
            if (rows.Count > 0 && !unit && rows.All(c => c.Value.Length > 0 && !IsNumber(c.Value)))
            {
                result.Add(g.Key);
                result.Add((g.Key.Methodology, string.Empty, g.Key.Name)); // для пошуку в бібліотеці без версії
            }
        }

        return result;
    }

    private static bool IsNumber(string value)
        => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    private static bool IsStringLiteral(string s)
    {
        if (s.Length < 2 || s[0] != '\'' || s[^1] != '\'')
        {
            return false;
        }

        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '\'')
            {
                if (s[i + 1] != '\'' || i + 1 >= s.Length - 1)
                {
                    return false;
                }

                i++;
            }
        }

        return true;
    }

    /// <summary>Знімає зовнішні дужки, поки відкривна парна останній.</summary>
    private static string StripParens(string s)
    {
        while (s.Length >= 2 && s[0] == '(' && MatchClose(s, 0) == s.Length - 1)
        {
            s = s[1..^1].Trim();
        }

        return s;
    }

    private static int MatchClose(string s, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\'')
            {
                inString = !inString;
            }
            else if (!inString)
            {
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')' && --depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Ділить за операторами на нульовій глибині дужок поза рядковими літералами; унарний знак на початку — не роздільник.</summary>
    private static List<string> SplitTop(string s, char[] separators)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\'')
            {
                inString = !inString;
            }
            else if (!inString)
            {
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }
                else if (depth == 0 && Array.IndexOf(separators, ch) >= 0 && s[start..i].Trim().Length > 0)
                {
                    parts.Add(s[start..i]);
                    start = i + 1;
                }
            }
        }

        parts.Add(s[start..]);
        return parts;
    }
}
