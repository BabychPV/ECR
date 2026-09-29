using System.Globalization;

namespace Ecr.Application.Localization;

/// <summary>Як прочитано текст числа.</summary>
public enum NumberTextKind
{
    /// <summary>Однозначне число.</summary>
    Number = 0,

    /// <summary>Читається двома способами, і культура неоднозначності не знімає — відмова.</summary>
    Ambiguous = 1,

    /// <summary>Не число (зокрема змішані роздільники, що не відповідають культурі).</summary>
    NotNumber = 2,
}

/// <summary>Результат розбору: вид, число і обидва прочитання неоднозначного.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Value">Число; лише для <see cref="NumberTextKind.Number"/>.</param>
/// <param name="AsGroup">Прочитання «роздільник — розряди» (інваріантний запис); лише для неоднозначного.</param>
/// <param name="AsDecimal">Прочитання «роздільник — десятковий»; лише для неоднозначного.</param>
public readonly record struct NumberTextReading(
    NumberTextKind Kind, decimal Value, string? AsGroup = null, string? AsDecimal = null);

/// <summary>
/// Розбір числа, набраного ЛЮДИНОЮ текстом, за роздільниками культури користувача.
/// </summary>
/// <remarks>
/// ⚠ Правила — ті самі, що в клієнтській вставці (<c>features/grid/clipboard.ts</c>,
/// <c>readNumber</c>), щоб сервер і сітка читали той самий рядок однаково. Пробіли
/// (зокрема нерозривні) — завжди розряди й відкидаються.
/// <list type="number">
/// <item>Є і крапка, і кома — десятковий той, що стоїть ОСТАННІМ і лише раз; він
/// мусить бути десятковим роздільником КУЛЬТУРИ, а другий — її роздільником розрядів
/// і стояти коректними групами. ⛔ Тут сервер суворіший за клієнта (рішення
/// 2026-09-29): «1.234,5» у en-US — відмова, а не 1234.5; у ru/kk (розряди —
/// пробіл) змішані роздільники — відмова завжди.</item>
/// <item>Один роздільник кілька разів — лише розряди (<c>1,234,567</c>).</item>
/// <item>Один роздільник один раз, і він НЕ може бути розрядом (після нього не рівно
/// три цифри, або перед ним понад три цифри чи ведучий нуль) — десятковий
/// (<c>12,5</c>, <c>1234,5</c>, <c>0,125</c>).</item>
/// <item>Один роздільник між 1–3 цифрами і рівно трьома цифрами — вирішує культура:
/// це її десятковий — десятковий (<c>1,234</c> у ru → 1.234); крапка, яка в культурі
/// не є розрядами, — інваріантний запис, тобто десяткова (<c>1.234</c> у ru → 1.234);
/// інакше — <b>неоднозначно</b>, відмова (<c>1,234</c> у en-US).</item>
/// </list>
/// ⚠ Канонічний рядок клієнта (<c>1234.5</c>, <c>1E-05</c>) читається однаково в
/// будь-якій культурі — крапку без розрядів правило 3/4 завжди бере десятковою
/// (якщо культура не тримає крапку розрядами).
/// </remarks>
public static class CultureNumberReader
{
    /// <summary>Читає текст числа за роздільниками <paramref name="culture"/>.</summary>
    /// <param name="raw">Текст.</param>
    /// <param name="culture">Культура користувача (<see cref="NumberCulture.ForLanguage"/>).</param>
    public static NumberTextReading Read(string? raw, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (string.IsNullOrEmpty(raw))
        {
            return NotNumber;
        }

        var stripped = string.Concat(raw.Where(c => !char.IsWhiteSpace(c)));
        if (stripped.Length == 0)
        {
            return NotNumber;
        }

        // Знак, мантиса з цифр/крапок/ком, необов'язкова експонента.
        var sign = stripped[0] is '+' or '-' ? stripped[..1] : string.Empty;
        var rest = stripped[sign.Length..];
        var exponentAt = rest.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt >= 0 ? rest[..exponentAt] : rest;
        var exponent = string.Empty;

        if (exponentAt >= 0)
        {
            var digits = rest[(exponentAt + 1)..];
            var body = digits.Length > 0 && digits[0] is '+' or '-' ? digits[1..] : digits;
            if (!IsDigits(body))
            {
                return NotNumber;
            }

            exponent = "e" + digits;
        }

        // `.5`, `1.`, `1,` — не числа, як і на клієнті.
        if (mantissa.Length == 0
            || !mantissa.All(c => c is (>= '0' and <= '9') or '.' or ',')
            || !char.IsAsciiDigit(mantissa[0])
            || !char.IsAsciiDigit(mantissa[^1]))
        {
            return NotNumber;
        }

        var commas = mantissa.Count(c => c == ',');
        var dots = mantissa.Count(c => c == '.');
        var format = culture.NumberFormat;
        var cultureDecimal = format.NumberDecimalSeparator;
        var cultureGroup = format.NumberGroupSeparator;

        if (commas == 0 && dots == 0)
        {
            return Number(sign, mantissa, string.Empty, exponent);
        }

        if (commas > 0 && dots > 0)
        {
            var last = Math.Max(mantissa.LastIndexOf(','), mantissa.LastIndexOf('.'));
            var decimalChar = mantissa[last];
            var groupChar = decimalChar == ',' ? '.' : ',';

            if ((decimalChar == ',' ? commas : dots) != 1
                || !string.Equals(decimalChar.ToString(), cultureDecimal, StringComparison.Ordinal)
                || !string.Equals(groupChar.ToString(), cultureGroup, StringComparison.Ordinal))
            {
                return NotNumber;
            }

            var integer = mantissa[..last];

            return IsGrouped(integer, groupChar)
                ? Number(sign, integer.Replace(groupChar.ToString(), string.Empty, StringComparison.Ordinal), mantissa[(last + 1)..], exponent)
                : NotNumber;
        }

        var separator = commas > 0 ? ',' : '.';

        if (commas + dots > 1)
        {
            return IsGrouped(mantissa, separator)
                ? Number(sign, mantissa.Replace(separator.ToString(), string.Empty, StringComparison.Ordinal), string.Empty, exponent)
                : NotNumber;
        }

        var at = mantissa.IndexOf(separator, StringComparison.Ordinal);
        var before = mantissa[..at];
        var after = mantissa[(at + 1)..];

        var canBeGroup = after.Length == 3 && before.Length is >= 1 and <= 3 && before[0] != '0';
        if (!canBeGroup)
        {
            return Number(sign, before, after, exponent);
        }

        if (string.Equals(separator.ToString(), cultureDecimal, StringComparison.Ordinal)
            || (separator == '.' && !string.Equals(cultureGroup, ".", StringComparison.Ordinal)))
        {
            return Number(sign, before, after, exponent);
        }

        return new NumberTextReading(
            NumberTextKind.Ambiguous,
            0m,
            $"{sign}{before}{after}{exponent}",
            $"{sign}{before}.{after}{exponent}");
    }

    private static readonly NumberTextReading NotNumber = new(NumberTextKind.NotNumber, 0m);

    private static NumberTextReading Number(string sign, string integer, string fraction, string exponent)
    {
        var text = $"{sign}{integer}{(fraction.Length > 0 ? "." + fraction : string.Empty)}{exponent}";

        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? new NumberTextReading(NumberTextKind.Number, value)
            : NotNumber;
    }

    /// <summary>Коректний запис цілого з розрядами <paramref name="separator"/> (<c>1,234,567</c>).</summary>
    private static bool IsGrouped(string text, char separator)
    {
        var groups = text.Split(separator);

        return groups.Length > 1
               && groups[0].Length is >= 1 and <= 3
               && IsDigits(groups[0])
               && groups.Skip(1).All(group => group.Length == 3 && IsDigits(group));
    }

    private static bool IsDigits(string part) => part.Length > 0 && part.All(char.IsAsciiDigit);
}
