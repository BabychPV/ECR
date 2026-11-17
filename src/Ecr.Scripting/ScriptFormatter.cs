using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.Expressions.Ast;
using Ecr.Scripting.Lexing;
using Ecr.Scripting.Parsing;

namespace Ecr.Scripting;

/// <summary>Канонічний друк значень констант (для редактора таблиць і конвертера).</summary>
public static partial class ScriptFormatter
{
    private const string Indent = "    ";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentPattern();

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberPattern();

    /// <summary>
    /// Друкує значення константи: скаляр — одним токеном; мапа — по рядку на ключ; вкладені мапи з рівними
    /// наборами ключів і скалярними значеннями — по одному рядку з вирівнюванням колонок.
    /// </summary>
    /// <param name="value">Константа.</param>
    public static string FormatConst(ScriptConst value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Format(value, 0);
    }

    private static string Format(ScriptConst value, int level)
    {
        if (!value.IsMap)
        {
            return Scalar(value);
        }

        var map = value.Map!;
        if (map.Count == 0)
        {
            return "{ }";
        }

        var pad = string.Concat(Enumerable.Repeat(Indent, level + 1));
        var close = string.Concat(Enumerable.Repeat(Indent, level));
        var sb = new StringBuilder("{\n");

        var rowsAligned = map.Values.All(v => v.IsMap && v.Map!.Count > 0 && v.Map.Values.All(x => !x.IsMap))
                          && map.Values.Select(v => string.Join('\u0001', v.Map!.Keys)).Distinct(StringComparer.Ordinal).Count() == 1;
        if (rowsAligned)
        {
            var keys = map.Values.First().Map!.Keys.ToList();
            var keyWidth = map.Keys.Max(k => Key(k).Length);
            string Cell(ScriptConst row, int i) => Key(keys[i]) + ": " + Scalar(row.Map![keys[i]]) + (i < keys.Count - 1 ? "," : string.Empty);
            var widths = keys.Select((_, i) => map.Values.Max(v => Cell(v, i).Length)).ToList();
            foreach (var (key, row) in map)
            {
                var cells = keys.Select((_, i) => Cell(row, i).PadRight(widths[i]));
                sb.Append(pad).Append((Key(key) + ":").PadRight(keyWidth + 1)).Append(" { ")
                  .Append(string.Join(' ', cells).TrimEnd()).Append(" },\n");
            }
        }
        else
        {
            foreach (var (key, child) in map)
            {
                sb.Append(pad).Append(Key(key)).Append(": ").Append(Format(child, level + 1)).Append(",\n");
            }
        }

        return sb.Append(close).Append('}').ToString();
    }

    private static string Scalar(ScriptConst c)
    {
        var v = c.Scalar!.Value;
        return v.Type switch
        {
            ExpressionValueType.Number => ScriptNumbers.KeyText((decimal)v.Value!),
            ExpressionValueType.Text => Quote((string)v.Value!),
            ExpressionValueType.Boolean => (bool)v.Value! ? "true" : "false",
            _ => "null",
        };
    }

    private static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                   .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static string Key(string key)
    {
        if (IdentPattern().IsMatch(key) && !ScriptLexer.Keywords.Contains(key))
        {
            return key;
        }

        if (NumberPattern().IsMatch(key)
            && decimal.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && ScriptNumbers.KeyText(n) == key)
        {
            return key;
        }

        return Quote(key);
    }
}
