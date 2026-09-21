using System.Text;

namespace Ecr.Application.Common;

/// <summary>Рядок CSV за RFC 4180 із захистом від формул у табличних редакторах.</summary>
/// <remarks>
/// ⚠ Значення, що починається з <c>= + - @</c>, табуляції чи CR, Excel виконує
/// як формулу (CSV injection, OWASP). Префікс <c>'</c> робить його текстом;
/// ціна — апостроф у сирому файлі, і це свідомий вибір на користь безпеки.
/// </remarks>
public static class CsvFormat
{
    /// <summary>Розділювач рядків — CRLF, як вимагає RFC 4180.</summary>
    public const string NewLine = "\r\n";

    /// <summary>Готує одне поле: нейтралізує формулу, бере в лапки за потреби.</summary>
    /// <param name="value">Сире значення; <c>null</c> — порожнє поле.</param>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>Складає рядок із полів разом із завершальним CRLF.</summary>
    /// <param name="fields">Поля рядка.</param>
    public static string Row(params string?[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var line = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            line.Append(Field(fields[i]));
        }

        return line.Append(NewLine).ToString();
    }
}
