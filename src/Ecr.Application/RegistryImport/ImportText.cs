using System.Globalization;
using System.Text;

namespace Ecr.Application.RegistryImport;

/// <summary>Text normalisation shared by header matching and value parsing.</summary>
public static class ImportText
{
    /// <summary>
    /// Latin letters that look like Cyrillic ones (lower case) and are folded to them, so a header typed with a Latin
    /// <c>H</c> in <c>Hаименование</c> or <c>Y1</c> still matches (legacy files are full of them).
    /// </summary>
    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к',
        ['m'] = 'м', ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у',
        ['ё'] = 'е',
    };

    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['¹'] = '1', ['²'] = '2', ['³'] = '3', ['⁰'] = '0', ['⁴'] = '4', ['⁵'] = '5',
        ['⁶'] = '6', ['⁷'] = '7', ['⁸'] = '8', ['⁹'] = '9',
    };

    /// <summary>
    /// Matching key: lower case, look-alikes folded, superscript digits flattened, everything that is not a letter or
    /// a digit dropped (spaces, double spaces, line breaks, soft hyphens, hyphens, punctuation).
    /// </summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);

        foreach (var raw in text)
        {
            var ch = char.ToLowerInvariant(raw);

            if (Superscripts.TryGetValue(ch, out var plain))
            {
                ch = plain;
            }

            if (LookAlikes.TryGetValue(ch, out var folded))
            {
                ch = folded;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>Display text: NBSP and other white space to a single space, trimmed.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        var space = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == ' ' || ch == ' ' || ch == '​' || ch == '﻿')
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>A raw cell as text (numbers invariant, shortest round-trip).</summary>
    public static string AsText(object? raw) => raw switch
    {
        null => "",
        string s => s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTime t => t.TimeOfDay == TimeSpan.Zero
            ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>The raw cell holds nothing (null or blank text).</summary>
    public static bool IsEmpty(object? raw) => raw is null || (raw is string s && Clean(s).Length == 0);

    /// <summary>
    /// Normalises a business code: a numeric code (number or text) is left-padded with zeros to <paramref name="pad"/>,
    /// <c>NN_suffix</c> pads the numeric part, anything else is cleaned and kept.
    /// </summary>
    /// <returns>The code; <c>null</c> when the cell is not a code (empty, fractional number, negative).</returns>
    /// <remarks>
    /// ONE function for NAE and substance codes (legacy <c>CompositeKeyImport</c> keys): the file mixes
    /// <c>'0333'</c> and <c>2735</c>, <c>'6001'</c> and <c>6001</c>.
    /// </remarks>
    public static string? NormalizeCode(object? raw, int pad)
    {
        if (raw is double d)
        {
            if (!double.IsFinite(d) || d < 0 || d != Math.Floor(d) || d > 1e15)
            {
                return null;
            }

            return PadDigits(((long)d).ToString(CultureInfo.InvariantCulture), pad);
        }

        var text = Clean(AsText(raw));

        if (text.Length == 0)
        {
            return null;
        }

        if (text.All(char.IsAsciiDigit))
        {
            return PadDigits(text, pad);
        }

        // "131.0": a number that went through text.
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && text[..dot].All(char.IsAsciiDigit) && text[(dot + 1)..].Length > 0 && text[(dot + 1)..].All(c => c == '0'))
        {
            return PadDigits(text[..dot], pad);
        }

        var us = text.IndexOf('_', StringComparison.Ordinal);
        if (us > 0 && us < text.Length - 1 && text[..us].All(char.IsAsciiDigit))
        {
            return PadDigits(text[..us], pad) + text[us..];
        }

        return text;
    }

    private static string PadDigits(string digits, int pad)
        => digits.Length >= pad ? digits : digits.PadLeft(pad, '0');
}
