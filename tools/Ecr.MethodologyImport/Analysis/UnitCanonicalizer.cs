using System.Globalization;

namespace Ecr.MethodologyImport.Analysis;

/// <summary>Результат зіставлення одиниці AF з каталогом ECR: <see cref="Code"/> = null — нерезолвна, <see cref="Reason"/> каже чому.</summary>
public sealed record UnitResolution(string Raw, string? Code, string? Reason);

/// <summary>
/// Одиниця AF (<c>CInfo_Unit</c>, довільний рядок) → код каталогу ECR. Код береться лише з відомої таблиці
/// (<see cref="KnownCodes"/> — коди сіду) або з однозначного псевдоніма; усе інше НЕ вгадується, а віддається
/// як нерезолвне з причиною. <c>m3</c>/<c>нм3</c> газу не зводяться: <c>Sm3</c> і <c>Nm3</c> — різні одиниці.
/// </summary>
public static class UnitCanonicalizer
{
    public const string ReasonEmpty = "порожня одиниця";
    public const string ReasonUnknown = "немає в каталозі ECR і в таблиці псевдонімів";

    /// <summary>Коди каталогу, на які посилаються формули методологій (сід + таблиця рушія).</summary>
    public static IReadOnlyCollection<string> KnownCodes { get; } =
    [
        "kg", "t", "g", "mg", "m3", "l", "J", "GJ", "MWh", "s", "min", "h", "day", "year", "K", "degC", "mol", "one",
        "g_per_s", "t_per_year", "kg_per_t", "g_per_GJ", "mg_per_m3", "kg_per_m3", "kt", "MJ", "TJ", "pct_vol", "pct_wt",
        "t_per_t", "kg_per_TJ", "Sm3", "Sm3_per_s", "Sm3_per_h", "m_per_s", "m2", "kg_per_Sm3", "MJ_per_Sm3", "MJ_per_kg",
        "g_per_mol", "mg_per_Sm3", "Sm3_per_day", "Nm3", "Nm3_per_s", "Nm3_per_h", "Nm3_per_day", "mg_per_Nm3",
    ];

    // Ключ — нормалізована форма (нижній регістр, без пробілів, «/» → «_per_», надрядкові 2/3 → цифри).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["кг"] = "kg", ["т"] = "t", ["г"] = "g", ["мг"] = "mg", ["л"] = "l", ["с"] = "s", ["хв"] = "min", ["год"] = "h",
        ["доба"] = "day", ["добу"] = "day", ["рік"] = "year", ["год."] = "h", ["tonne"] = "t", ["tonnes"] = "t",
        ["ton"] = "t", ["sec"] = "s", ["hour"] = "h", ["hr"] = "h", ["yr"] = "year", ["years"] = "year",
        ["days"] = "day", ["м3"] = "m3", ["м2"] = "m2", ["дж"] = "J", ["гдж"] = "GJ", ["мдж"] = "MJ", ["тдж"] = "TJ",
        ["безрозм."] = "one", ["безразмерная"] = "one",
        ["кг_per_т"] = "kg_per_t", ["т_per_рік"] = "t_per_year", ["т_per_год"] = "t_per_year", ["t_per_yr"] = "t_per_year",
        ["г_per_с"] = "g_per_s", ["мг_per_м3"] = "mg_per_m3", ["кг_per_м3"] = "kg_per_m3",
    };

    private static readonly Dictionary<string, string> ByLowerCode =
        KnownCodes.ToDictionary(c => c.ToLowerInvariant(), c => c, StringComparer.Ordinal);

    public static UnitResolution Resolve(string? raw)
    {
        var text = raw ?? string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new UnitResolution(string.Empty, null, ReasonEmpty);
        }

        if (KnownCodes.Contains(trimmed, StringComparer.Ordinal))
        {
            return new UnitResolution(trimmed, trimmed, null);
        }

        var key = Normalize(trimmed);
        if (Aliases.TryGetValue(key, out var alias))
        {
            return new UnitResolution(trimmed, alias, null);
        }

        // Нормалізована форма збігається з кодом без огляду на регістр лише коли код однозначний (Sm3 ≠ sm3 не існує).
        return ByLowerCode.TryGetValue(key, out var code)
            ? new UnitResolution(trimmed, code, null)
            : new UnitResolution(trimmed, null, ReasonUnknown);
    }

    /// <summary>Нижній регістр, без пробілів (у т.ч. нерозривних), «/» → «_per_», «³» → «3», «²» → «2».</summary>
    internal static string Normalize(string unit)
    {
        var sb = new System.Text.StringBuilder(unit.Length + 4);
        foreach (var ch in unit)
        {
            switch (ch)
            {
                case ' ' or ' ' or '\t':
                    break;
                case '/':
                    sb.Append("_per_");
                    break;
                case '³':
                    sb.Append('3');
                    break;
                case '²':
                    sb.Append('2');
                    break;
                default:
                    sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
                    break;
            }
        }

        return sb.ToString();
    }
}
