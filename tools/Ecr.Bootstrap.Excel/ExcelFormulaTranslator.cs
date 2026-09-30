using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.Expressions.Parsing;

namespace Ecr.Bootstrap.Excel;

/// <summary>Результат перекладу формули Excel.</summary>
/// <param name="Expression">Вираз діалекту шаблонів; <c>null</c> — не перекладено.</param>
/// <param name="Problem">Чому не перекладено; <c>null</c> — перекладено.</param>
public sealed record FormulaTranslation(string? Expression, string? Problem)
{
    /// <summary>Чи вдалося перекласти.</summary>
    public bool IsSuccess => Expression is not null;
}

/// <summary>
/// Перекладає формулу Excel у вираз діалекту шаблонів — лише в однозначному
/// випадку: усі посилання ведуть на комірки ТОГО САМОГО рядка всередині
/// таблиці, функції — з переліку, який діалект виконує так само.
/// </summary>
/// <remarks>
/// ⛔ Нерозпізнане не вгадується (<c>tz/09</c> §9.4): посилання на інший рядок,
/// інший аркуш, іменований діапазон, масив або функцію поза переліком — це
/// відмова з причиною, і формула йде у звіт для ручного рішення. Хибно
/// перекладена формула дала б правдоподібні, але неправильні числа.
///
/// ⚠ Результат додатково розбирається справжнім <see cref="Parser"/> діалекту
/// <see cref="ExpressionDialect.Template"/>: переклад, який парсер не приймає,
/// теж відмова, а не запис у шаблон.
/// </remarks>
public sealed partial class ExcelFormulaTranslator
{
    /// <summary>Функції, що в діалекті шаблонів мають ту саму семантику, що й в Excel.</summary>
    public static readonly IReadOnlySet<string> SupportedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVERAGE", "MIN", "MAX", "COUNT", "ROUND", "ABS", "PRODUCT", "IF", "IFERROR",
    };

    private readonly Parser _parser = new();

    /// <summary>Перекладає формулу однієї комірки.</summary>
    /// <param name="formulaA1">Формула Excel у нотації A1, без початкового <c>=</c>.</param>
    /// <param name="row">Номер рядка аркуша, у якому стоїть формула.</param>
    /// <param name="columnCodes">Номер колонки аркуша → код колонки таблиці.</param>
    /// <returns>Вираз або причина відмови.</returns>
    public FormulaTranslation Translate(string formulaA1, int row, IReadOnlyDictionary<int, string> columnCodes)
    {
        ArgumentNullException.ThrowIfNull(formulaA1);
        ArgumentNullException.ThrowIfNull(columnCodes);

        var text = formulaA1.TrimStart('=');
        var sb = new StringBuilder(text.Length + 16);
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];

            if (ch == '"')
            {
                var end = i + 1;
                while (end < text.Length && !(text[end] == '"' && (end + 1 >= text.Length || text[end + 1] != '"')))
                {
                    end += text[end] == '"' ? 2 : 1;
                }

                if (end >= text.Length)
                {
                    return Fail("незакритий рядковий літерал");
                }

                sb.Append(text, i, end - i + 1);
                i = end + 1;
                continue;
            }

            if (ch is '\'' or '[' or '{' or '#' or '!')
            {
                return Fail(ch switch
                {
                    '\'' or '!' => "посилання на інший аркуш",
                    '[' => "посилання на іншу книгу або структуроване посилання таблиці",
                    '{' => "масив-константа",
                    _ => "формула містить помилку Excel (#REF!, #N/A…)",
                });
            }

            var reference = CellReference().Match(text, i);
            if (reference.Success && reference.Index == i)
            {
                if (reference.Groups["sheet"].Success)
                {
                    return Fail("посилання на інший аркуш");
                }

                var translated = TranslateReference(reference, row, columnCodes, out var problem);
                if (translated is null)
                {
                    return Fail(problem!);
                }

                sb.Append(translated);
                i += reference.Length;
                continue;
            }

            var identifier = Identifier().Match(text, i);
            if (identifier.Success && identifier.Index == i)
            {
                var name = identifier.Value;
                var next = i + name.Length;
                if (name.StartsWith("_xlfn.", StringComparison.OrdinalIgnoreCase))
                {
                    name = name["_xlfn.".Length..];
                }

                if (next < text.Length && text[next] == '(')
                {
                    if (!SupportedFunctions.Contains(name))
                    {
                        return Fail($"функції {name.ToUpperInvariant()} немає в діалекті шаблонів");
                    }

                    sb.Append(name.ToUpperInvariant());
                }
                else if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(name.ToUpperInvariant());
                }
                else
                {
                    return Fail($"іменований діапазон або невідоме ім'я «{name}»");
                }

                i = next;
                continue;
            }

            if (ch == ';')
            {
                // Роздільник аргументів у локалізованому записі.
                sb.Append(',');
                i++;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        var expression = sb.ToString().Trim();
        var parsed = _parser.Parse(expression, ExpressionDialect.Template);
        if (!parsed.IsSuccess)
        {
            var first = parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : "невідома причина";
            return Fail($"перекладений вираз «{expression}» не розбирається діалектом шаблонів: {first}");
        }

        return new FormulaTranslation(expression, null);
    }

    private static string? TranslateReference(
        Match reference, int row, IReadOnlyDictionary<int, string> columnCodes, out string? problem)
    {
        problem = null;
        var fromColumn = ColumnNumber(reference.Groups["c1"].Value);
        var fromRow = int.Parse(reference.Groups["r1"].Value, CultureInfo.InvariantCulture);
        var toColumn = fromColumn;
        var toRow = fromRow;

        if (reference.Groups["c2"].Success)
        {
            toColumn = ColumnNumber(reference.Groups["c2"].Value);
            toRow = int.Parse(reference.Groups["r2"].Value, CultureInfo.InvariantCulture);
        }

        if (fromRow != row || toRow != row)
        {
            problem = $"посилання {reference.Value} веде на інший рядок — потрібне посилання на RowKey, його вирішує людина";
            return null;
        }

        var parts = new List<string>();
        for (var c = Math.Min(fromColumn, toColumn); c <= Math.Max(fromColumn, toColumn); c++)
        {
            if (!columnCodes.TryGetValue(c, out var code))
            {
                problem = $"посилання {reference.Value} виходить за колонки даних таблиці";
                return null;
            }

            parts.Add($"[{code}]");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Номер колонки за літерами: <c>A</c> → 1, <c>AA</c> → 27.</summary>
    /// <param name="letters">Літери колонки.</param>
    /// <returns>Номер, з 1.</returns>
    public static int ColumnNumber(string letters)
    {
        ArgumentNullException.ThrowIfNull(letters);
        var n = 0;
        foreach (var ch in letters.Replace("$", string.Empty, StringComparison.Ordinal).ToUpperInvariant())
        {
            n = (n * 26) + (ch - 'A' + 1);
        }

        return n;
    }

    private static FormulaTranslation Fail(string problem) => new(null, problem);

    [GeneratedRegex(
        @"(?<sheet>[^\W\d][\w\.]*!)?\$?(?<c1>[A-Za-z]{1,3})\$?(?<r1>\d+)(?::\$?(?<c2>[A-Za-z]{1,3})\$?(?<r2>\d+))?(?![\w\(])")]
    private static partial Regex CellReference();

    [GeneratedRegex(@"[^\W\d][\w\.]*")]
    private static partial Regex Identifier();
}
