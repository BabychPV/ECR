using System.Text.RegularExpressions;

namespace Ecr.Bootstrap.Excel;

/// <summary>Результат розбору заголовка колонки.</summary>
/// <param name="Header">Заголовок без позначення одиниці.</param>
/// <param name="UnitText">Позначення одиниці, як його написано в книзі; <c>null</c> — немає.</param>
/// <param name="UnitCode">Код одиниці каталогу; <c>null</c> — позначення немає або не розпізнано.</param>
/// <param name="Problem">Чому позначення не розпізнано; <c>null</c> — розпізнано або його немає.</param>
public sealed record HeaderUnit(string Header, string? UnitText, string? UnitCode, string? Problem);

/// <summary>
/// Витягує одиницю із заголовка колонки (<c>«Викид, т/рік»</c>, <c>«Витрата (м³)»</c>,
/// <c>«Маса [кг]»</c>) і зіставляє її з кодом каталогу <c>uom.Unit</c>.
/// </summary>
/// <remarks>
/// ⛔ ФВ-16.12: кожна нерозпізнана одиниця йде у звіт, а не заводиться
/// здогадкою. Тому неоднозначні позначення розпізнаються як «проблема», а не
/// як найімовірніший варіант: <c>год</c> — це «година» українською і «рік»
/// російською (<c>т/год</c> розходиться у 8760 разів), <c>%</c> — масовий чи
/// об'ємний відсоток (різні розмірності каталогу).
///
/// ⚠ Похідна одиниця складається з чисельника і знаменника й приймається,
/// лише якщо такий код є в каталозі (<see cref="KnownDerived"/> — дзеркало
/// похідних одиниць <c>09-seed.sql</c>). Нового коду інструмент не вигадує:
/// одиниця, якої немає в каталозі, — пункт звіту.
/// </remarks>
public static partial class UnitRecognizer
{
    private const string NotRecognized = "позначення не розпізнано";

    /// <summary>Похідні одиниці каталогу (<c>09-seed.sql</c>, <c>uom.Unit</c> з чисельником і знаменником).</summary>
    public static readonly IReadOnlySet<string> KnownDerived = new HashSet<string>(StringComparer.Ordinal)
    {
        "g_per_s", "t_per_year", "kg_per_t", "g_per_GJ", "mg_per_m3", "kg_per_m3",
        "Sm3_per_s", "Sm3_per_h", "kg_per_Sm3", "MJ_per_Sm3", "MJ_per_kg", "t_per_t", "kg_per_TJ", "g_per_mol",
        "mg_per_Sm3", "Sm3_per_day",
    };

    /// <summary>Позначення простих одиниць (нормалізовані: нижній регістр, без пробілів і крапок).</summary>
    private static readonly Dictionary<string, string> Simple = new(StringComparer.Ordinal)
    {
        ["т"] = "t", ["t"] = "t", ["тонн"] = "t", ["тонна"] = "t", ["тонни"] = "t",
        ["tonne"] = "t", ["tonnes"] = "t",
        ["кг"] = "kg", ["kg"] = "kg",
        ["г"] = "g", ["g"] = "g", ["гр"] = "g",
        ["мг"] = "mg", ["mg"] = "mg",
        ["тист"] = "kt", ["тыст"] = "kt", ["kt"] = "kt",
        ["м3"] = "m3", ["m3"] = "m3",
        ["нм3"] = "Sm3", ["стм3"] = "Sm3", ["sm3"] = "Sm3", ["nm3"] = "Sm3",
        ["л"] = "l", ["l"] = "l",
        ["гдж"] = "GJ", ["gj"] = "GJ",
        ["мдж"] = "MJ", ["mj"] = "MJ",
        ["тдж"] = "TJ", ["tj"] = "TJ",
        ["мвт·год"] = "MWh", ["мвтгод"] = "MWh", ["мвт*год"] = "MWh", ["мвтч"] = "MWh", ["мвт·ч"] = "MWh", ["mwh"] = "MWh",
        ["с"] = "s", ["сек"] = "s", ["s"] = "s",
        ["хв"] = "min", ["мин"] = "min", ["min"] = "min",
        ["ч"] = "h", ["h"] = "h",
        ["доба"] = "day", ["сут"] = "day", ["day"] = "day",
        ["рік"] = "year", ["yr"] = "year", ["year"] = "year",
        ["°c"] = "degC", ["°с"] = "degC", ["ºc"] = "degC", ["ºс"] = "degC",
        ["моль"] = "mol", ["mol"] = "mol",
        ["м2"] = "m2", ["m2"] = "m2",
        ["м/с"] = "m_per_s", ["m/s"] = "m_per_s",
    };

    /// <summary>Позначення, які читаються більш ніж одним способом.</summary>
    private static readonly Dictionary<string, string> Ambiguous = new(StringComparer.Ordinal)
    {
        ["год"] = "«год» — це «година» (укр.) або «рік» (рос.)",
        ["%"] = "«%» — масовий (pct_wt) чи об'ємний (pct_vol) відсоток",
        ["мвт"] = "«МВт» — потужність; у каталозі є лише енергія (MWh)",
    };

    /// <summary>Розбирає заголовок.</summary>
    /// <param name="rawHeader">Текст заголовка з книги.</param>
    /// <returns>Заголовок, позначення і код одиниці.</returns>
    public static HeaderUnit Parse(string rawHeader)
    {
        ArgumentNullException.ThrowIfNull(rawHeader);
        var header = WhiteSpace().Replace(rawHeader, " ").Trim();

        var match = Bracketed().Match(header);
        var bracketed = match.Success;
        if (!bracketed)
        {
            match = AfterComma().Match(header);
        }

        if (!match.Success)
        {
            return new HeaderUnit(header, null, null, null);
        }

        var unitText = match.Groups["unit"].Value.Trim();
        var rest = header[..match.Index].TrimEnd(' ', ',', ';');
        var (code, problem) = Resolve(unitText);

        // Текст у дужках, що не схожий на одиницю («(факт)», «(для довідки)»),
        // лишається частиною заголовка і у звіт не йде: це не одиниця.
        // Після коми вимога суворіша: «Назва, опис» — звичайний заголовок, а
        // «Частка, %» чи «Витрата, т/год» — позначення, яке треба назвати у звіті.
        if (code is null && (bracketed ? !LooksLikeUnit(unitText) : problem == NotRecognized && !unitText.Contains('/', StringComparison.Ordinal)))
        {
            return new HeaderUnit(header, null, null, null);
        }

        return new HeaderUnit(rest.Length == 0 ? header : rest, unitText, code, problem);
    }

    /// <summary>Зіставляє позначення з кодом каталогу.</summary>
    /// <param name="unitText">Позначення, як у книзі.</param>
    /// <returns>Код або причина, чому його немає.</returns>
    public static (string? Code, string? Problem) Resolve(string unitText)
    {
        ArgumentNullException.ThrowIfNull(unitText);
        var n = Normalize(unitText);

        if (KnownDerived.Contains(unitText.Trim()) || Simple.ContainsValue(unitText.Trim()))
        {
            return (unitText.Trim(), null);
        }

        if (Ambiguous.TryGetValue(n, out var why))
        {
            return (null, why);
        }

        if (Simple.TryGetValue(n, out var simple))
        {
            return (simple, null);
        }

        var slash = n.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && slash == n.LastIndexOf('/'))
        {
            var numText = n[..slash];
            var denText = n[(slash + 1)..];
            foreach (var part in new[] { numText, denText })
            {
                if (Ambiguous.TryGetValue(part, out var partWhy))
                {
                    return (null, partWhy);
                }
            }

            if (Simple.TryGetValue(numText, out var num) && Simple.TryGetValue(denText, out var den))
            {
                var derived = $"{num}_per_{den}";
                return KnownDerived.Contains(derived)
                    ? (derived, null)
                    : (null, $"похідної одиниці {derived} немає в каталозі");
            }
        }

        return (null, NotRecognized);
    }

    private static string Normalize(string unitText) => unitText.Trim().ToLowerInvariant()
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace(".", string.Empty, StringComparison.Ordinal)
        .Replace("³", "3", StringComparison.Ordinal)
        .Replace("²", "2", StringComparison.Ordinal);

    /// <summary>Схоже на позначення одиниці: дріб або коротке слово без пробілів.</summary>
    private static bool LooksLikeUnit(string text)
        => text.Contains('/', StringComparison.Ordinal)
           || text.Contains('%', StringComparison.Ordinal)
           || text.Contains('°', StringComparison.Ordinal)
           || (text.Length <= 6 && !text.Contains(' ', StringComparison.Ordinal) && text.Any(char.IsLetter));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    /// <summary>Одиниця в кінці заголовка в круглих чи квадратних дужках.</summary>
    [GeneratedRegex(@"\s*[\(\[](?<unit>[^\(\)\[\]]{1,24})[\)\]]\s*$")]
    private static partial Regex Bracketed();

    /// <summary>Одиниця після останньої коми: «Викид, т/рік».</summary>
    [GeneratedRegex(@",\s*(?<unit>[^,]{1,16})$")]
    private static partial Regex AfterComma();
}
