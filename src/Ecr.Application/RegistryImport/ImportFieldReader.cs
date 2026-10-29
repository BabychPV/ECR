using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Domain.Services;

namespace Ecr.Application.RegistryImport;

/// <summary>Result of reading one cell.</summary>
/// <param name="Value">Parsed value: <c>decimal</c>, <c>long</c>, <c>string</c>, <c>DateTime</c> or <c>null</c>.</param>
/// <param name="Empty">The cell had no value (blank or a "no value" marker).</param>
/// <param name="ErrorKey">Message key of a read failure.</param>
/// <param name="WarningKey">Message key of a warning (value accepted).</param>
public readonly record struct FieldReadResult(object? Value, bool Empty, string? ErrorKey = null, string? WarningKey = null)
{
    /// <summary>No value.</summary>
    public static FieldReadResult None => new(null, true);

    /// <summary>A value.</summary>
    public static FieldReadResult Ok(object value, string? warning = null) => new(value, false, null, warning);

    /// <summary>A failure.</summary>
    public static FieldReadResult Fail(string key) => new(null, false, key);
}

/// <summary>Reads raw cells of one field with the field's parser (table-driven in tests, no workbook needed).</summary>
public sealed partial class ImportFieldReader
{
    private readonly ImportFieldSpec _field;
    private readonly HashSet<string> _naMarkers;
    private readonly Dictionary<string, string> _choices;
    private readonly Dictionary<string, string> _units;

    /// <summary>Creates a reader.</summary>
    public ImportFieldReader(ImportFieldSpec field, IReadOnlyList<string>? naMarkers = null)
    {
        ArgumentNullException.ThrowIfNull(field);

        _field = field;
        _naMarkers = (naMarkers ?? RegistryImportSpec.DefaultNaMarkers)
            .Select(m => ImportText.Clean(m).ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        _choices = field.Choices.ToDictionary(p => ImportText.Fold(p.Key), p => p.Value, StringComparer.Ordinal);
        _units = field.Units.ToDictionary(p => ImportText.Fold(p.Key), p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>Reads a raw cell value.</summary>
    public FieldReadResult Read(object? raw)
    {
        if (ImportText.IsEmpty(raw))
        {
            return FieldReadResult.None;
        }

        if (_field.Parser is ImportParser.Text or ImportParser.Code)
        {
            return _field.Parser == ImportParser.Text ? ReadText(raw) : ReadCode(raw);
        }

        if (raw is string s && _naMarkers.Contains(ImportText.Clean(s).ToLowerInvariant()))
        {
            return FieldReadResult.None;
        }

        return _field.Parser switch
        {
            ImportParser.Decimal => ReadDecimal(raw),
            ImportParser.Integer => ReadInteger(raw),
            ImportParser.Choice => ReadMap(raw, _choices),
            ImportParser.Unit => ReadMap(raw, _units),
            ImportParser.Date => ReadDate(raw),
            _ => FieldReadResult.Fail(RegistryImportKeys.ValueInvalid),
        };
    }

    private static FieldReadResult ReadText(object? raw)
    {
        var text = ImportText.Clean(ImportText.AsText(raw));
        return text.Length == 0 ? FieldReadResult.None : FieldReadResult.Ok(text);
    }

    private FieldReadResult ReadCode(object? raw)
        => ImportText.NormalizeCode(raw, _field.Pad) is { } code
            ? FieldReadResult.Ok(code)
            : FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);

    private static FieldReadResult ReadMap(object? raw, Dictionary<string, string> map)
        => map.TryGetValue(ImportText.Fold(ImportText.AsText(raw)), out var canonical)
            ? FieldReadResult.Ok(canonical)
            : FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);

    private static FieldReadResult ReadDate(object? raw)
    {
        if (raw is DateTime date)
        {
            return FieldReadResult.Ok(date);
        }

        if (raw is string text && CellDateParser.TryParse(ImportText.Clean(text), out var parsed))
        {
            return FieldReadResult.Ok(parsed);
        }

        return FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
    }

    private FieldReadResult ReadInteger(object? raw)
    {
        var number = ReadDecimal(raw);

        if (number.ErrorKey is not null || number.Value is not decimal whole)
        {
            return number;
        }

        return whole == decimal.Truncate(whole) && whole is >= long.MinValue and <= long.MaxValue
            ? FieldReadResult.Ok((long)whole)
            : FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
    }

    private FieldReadResult ReadDecimal(object? raw)
    {
        decimal value;
        string? warning = null;

        switch (raw)
        {
            case double d:
                if (!double.IsFinite(d))
                {
                    return FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
                }

                if (!decimal.TryParse(
                        d.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    || (value == 0m && d != 0d))
                {
                    // 1e-30 does not fit decimal: a silent 0 would be a wrong emission limit.
                    return FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
                }

                break;

            case string text:
                var read = ParseDecimalText(text);
                if (read.Value is not { } parsed)
                {
                    return FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
                }

                value = parsed;
                warning = read.Ambiguous ? RegistryImportKeys.ValueAmbiguous : null;
                break;

            default:
                return FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
        }

        if (_field.Factor is { } factor)
        {
            try
            {
                value = checked(value * factor);
            }
            catch (OverflowException)
            {
                return FieldReadResult.Fail(RegistryImportKeys.ValueInvalid);
            }
        }

        return _field.NonNegative && value < 0m
            ? FieldReadResult.Fail(RegistryImportKeys.ValueNegative)
            : FieldReadResult.Ok(value, warning);
    }

    /// <summary>Parses a decimal typed as text.</summary>
    /// <param name="text">Cell text.</param>
    /// <returns>The value (null when unreadable) and whether the separator was ambiguous.</returns>
    /// <remarks>
    /// Rules: spaces, NBSP and apostrophes are thousands marks and dropped; with both <c>,</c> and <c>.</c> the LAST one is
    /// the decimal separator; one kind repeated is thousands (groups of three); one single separator is decimal.
    /// <c>1,234</c> (single comma, three digits, no leading zero) is read as 1.234 and flagged ambiguous.
    /// Exponent (<c>1.4e-06</c>) is kept exactly.
    /// </remarks>
    public static (decimal? Value, bool Ambiguous) ParseDecimalText(string text)
    {
        var s = text.Replace("−", "-", StringComparison.Ordinal);
        s = new string([.. s.Where(c => !char.IsWhiteSpace(c) && c != ' ' && c != '\'' && c != '’')]);

        if (s.Length == 0)
        {
            return (null, false);
        }

        var sign = "";
        if (s[0] is '+' or '-')
        {
            sign = s[0] == '-' ? "-" : "";
            s = s[1..];
        }

        var exponent = "";
        var e = s.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            var tail = s[(e + 1)..];
            if (!ExponentShape().IsMatch(tail))
            {
                return (null, false);
            }

            exponent = "E" + tail;
            s = s[..e];
        }

        if (s.Length == 0 || !MantissaShape().IsMatch(s))
        {
            return (null, false);
        }

        var dots = s.Count(c => c == '.');
        var commas = s.Count(c => c == ',');
        var ambiguous = false;
        string mantissa;

        if (dots > 0 && commas > 0)
        {
            var decimalMark = s.LastIndexOf('.') > s.LastIndexOf(',') ? '.' : ',';
            var thousandsMark = decimalMark == '.' ? ',' : '.';
            var at = s.LastIndexOf(decimalMark);

            if (s[at..].Contains(thousandsMark, StringComparison.Ordinal) || s.Count(c => c == decimalMark) != 1
                || !GroupsOfThree(s[..at], thousandsMark))
            {
                return (null, false);
            }

            mantissa = s[..at].Replace(thousandsMark.ToString(), "", StringComparison.Ordinal) + "." + s[(at + 1)..];
        }
        else if (dots + commas > 1)
        {
            var mark = dots > 0 ? '.' : ',';
            if (!GroupsOfThree(s, mark))
            {
                return (null, false);
            }

            mantissa = s.Replace(mark.ToString(), "", StringComparison.Ordinal);
        }
        else if (dots + commas == 1)
        {
            var mark = dots > 0 ? '.' : ',';
            var at = s.IndexOf(mark, StringComparison.Ordinal);
            var before = s[..at];
            var after = s[(at + 1)..];

            ambiguous = mark == ',' && exponent.Length == 0 && after.Length == 3 && before.Length is >= 1 and <= 3 && before != "0"
                        && after.All(char.IsAsciiDigit);
            mantissa = before + "." + after;
        }
        else
        {
            mantissa = s;
        }

        if (mantissa is "." or "")
        {
            return (null, false);
        }

        return decimal.TryParse(
            sign + mantissa + exponent, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (value, ambiguous)
            : (null, false);
    }

    private static bool GroupsOfThree(string text, char mark)
    {
        var parts = text.Split(mark);
        return parts.Length > 1
               && parts[0].Length is >= 1 and <= 3
               && parts.Skip(1).All(p => p.Length == 3)
               && parts.All(p => p.All(char.IsAsciiDigit));
    }

    [GeneratedRegex(@"^[+-]?\d{1,3}\z")]
    private static partial Regex ExponentShape();

    [GeneratedRegex(@"^[\d.,]+\z")]
    private static partial Regex MantissaShape();
}
