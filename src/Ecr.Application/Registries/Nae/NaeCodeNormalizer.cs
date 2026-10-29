// src/Ecr.Application/Registries/Nae/NaeCodeNormalizer.cs
using System.Globalization;
using System.Text;

namespace Ecr.Application.Registries.Nae;

/// <summary>
/// ONE normaliser for NAE (and substance) business codes: AF element names, Excel cells and user input all go through it.
/// </summary>
/// <remarks>
/// Same contract as <c>ImportText.NormalizeCode(object? raw, int pad)</c> of lane <c>integ-nae-n4</c> (630b46778): a numeric code
/// (number or text) is left-padded with zeros to <c>pad</c>, <c>NN_suffix</c> pads the numeric part, anything else is cleaned
/// and kept. Merge plan: when N4 reaches main, <c>ImportText.NormalizeCode</c> becomes a one-line call to this method (the
/// signature is identical), so the two can never drift.
/// </remarks>
public static class NaeCodeNormalizer
{
    /// <summary>Default width of an NAE code.</summary>
    public const int NaePad = 4;

    /// <summary>Normalises a raw code.</summary>
    /// <param name="raw">Cell or attribute value: string, number or <c>null</c>.</param>
    /// <param name="pad">Width numeric codes are padded to.</param>
    /// <returns>The code; <c>null</c> when the value is not a code (empty, fractional or negative number).</returns>
    public static string? Normalize(object? raw, int pad = NaePad)
    {
        if (raw is double d)
        {
            if (!double.IsFinite(d) || d < 0 || d != Math.Floor(d) || d > 1e15)
            {
                return null;
            }

            return PadDigits(((long)d).ToString(CultureInfo.InvariantCulture), pad);
        }

        if (raw is decimal m)
        {
            return m < 0 || m != decimal.Floor(m) ? null : PadDigits(decimal.Floor(m).ToString(CultureInfo.InvariantCulture), pad);
        }

        var text = Clean(raw switch
        {
            null => "",
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString() ?? "",
        });

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

    /// <summary>NBSP and any white space to single spaces, trimmed.</summary>
    /// <param name="text">Text.</param>
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
            if (char.IsWhiteSpace(ch) || ch is ' ' or '​' or '﻿')
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

    private static string PadDigits(string digits, int pad)
        => digits.Length >= pad ? digits : digits.PadLeft(pad, '0');
}
