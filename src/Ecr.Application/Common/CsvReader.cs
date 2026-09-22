using System.Text;

namespace Ecr.Application.Common;

/// <summary>Розбір CSV за RFC 4180 — дзеркало <see cref="CsvFormat"/>.</summary>
public static class CsvReader
{
    /// <summary>Розбирає текст на записи; BOM і завершальний порожній рядок ігноруються.</summary>
    /// <param name="text">Вміст файлу.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Знімає префікс <c>'</c>, яким <see cref="CsvFormat.Field"/> нейтралізує формулу.</summary>
    /// <param name="value">Поле з файлу.</param>
    public static string UnescapeFormula(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? value[1..]
            : value;
    }
}
