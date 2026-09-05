using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Adapters.Excel;

// ⚠ CA1822 (методи можна зробити статичними) вимкнено свідомо. Обидва класи
// названі КОНСТРУКТОРОМ `ExcelExporter` у контракті пакета (`05g` §1) і
// живуть у контейнері як співавтори. Статичні методи зробили б параметри
// конструктора зайвими — і контракт, і можливість підмінити поведінку в
// тестах зникли б заради економії, якої не існує: обидва класи без стану і
// створюються один раз як Singleton.
#pragma warning disable CA1822

/// <summary>
/// Транслює наші вирази в синтаксис Excel і назад.
/// </summary>
/// <remarks>
/// Це найпідступніша частина експорту: наша мова адресує рядки за
/// <c>RowKey</c>, Excel — за координатами. Тому потрібен **зворотний мапер
/// координат**, і будувати його треба на етапі експорту, а не «якось потім»
/// (ТЗ §13.5 п.6 — окрема пастка, яку легко недооцінити).
/// </remarks>
public sealed class FormulaTranslator
{
    /// <summary>
    /// Що повертається замість посилання, якого немає в мапі координат.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>#REF!</c>, а не порожнє місце і не нуль. Посилання на рядок, який
    /// в експорт не потрапив, — це втрачений зв'язок; Excel показує його як
    /// помилку, і людина бачить, що формула неповна. Нуль виглядав би як
    /// значення.
    /// </remarks>
    public const string MissingReference = "#REF!";

    /// <summary>Наш вираз → формула Excel.</summary>
    /// <param name="expression">Вираз у нашій граматиці.</param>
    /// <param name="coordinates">Мапа <c>(TableCode, RowKey, ColumnCode)</c> → адреса комірки Excel.</param>
    /// <remarks>
    /// ⚠ Мапа ключується <b>кодами</b>, а не ідентифікаторами. Вираз адресує
    /// комірку саме кодами (<c>[T1].[R10].[C3]</c>), і щоб перевести їх в
    /// ідентифікатори, транслятору знадобився б знімок структури — тобто ще
    /// одна залежність, яка вміє розходитися з тією, за якою будували книгу.
    /// </remarks>
    public string ToExcel(string expression, IReadOnlyDictionary<(string, string, string), string> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        var parsed = new Parser().Parse(expression, ExpressionDialect.Template);

        // ⚠ Нерозібраний вираз НЕ переноситься «як є». Текст нашої граматики
        // в комірці Excel — це або #NAME?, або, гірше, випадково валідна
        // формула, яка порахує щось своє.
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        Write(parsed.Expression.Root, coordinates, builder);

        return builder.Length == 0 ? string.Empty : "=" + builder;
    }

    /// <summary>Формула Excel → наш вираз (для імпорту структури з довільного файлу).</summary>
    /// <param name="excelFormula">Формула з книги.</param>
    /// <param name="reverseCoordinates">Адреса Excel → <c>(TableCode, RowKey, ColumnCode)</c>.</param>
    /// <remarks>
    /// ⚠ Зворотна трансляція — best-effort, і нерозпізнане вона **не вгадує**:
    /// повертає <c>null</c>, щоб користувач побачив формулу, яку система не
    /// зрозуміла. Мовчазна здогадка створила б неправильне правило обчислення,
    /// і помилку знайшли б у числах, а не в конфігурації.
    /// </remarks>
    public string? FromExcel(
        string excelFormula, IReadOnlyDictionary<string, (string, string, string)> reverseCoordinates)
    {
        ArgumentNullException.ThrowIfNull(reverseCoordinates);

        if (string.IsNullOrWhiteSpace(excelFormula))
        {
            return null;
        }

        var text = excelFormula.TrimStart('=').Trim();
        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var symbol = text[index];

            if (!char.IsLetter(symbol) && symbol != '$' && symbol != '\'')
            {
                builder.Append(symbol);
                index++;
                continue;
            }

            var start = index;

            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '$' or '!' or '\'' or '.' or '_'))
            {
                index++;
            }

            var token = text[start..index];

            if (reverseCoordinates.TryGetValue(Normalize(token), out var address))
            {
                builder.Append(CultureInfo.InvariantCulture, $"[{address.Item1}].[{address.Item2}].[{address.Item3}]");
                continue;
            }

            // Ім'я, яке не є ані нашим посиланням, ані відомою функцією, —
            // привід відмовитися цілком, а не лишити половину виразу.
            if (!IsKnownFunction(token))
            {
                return null;
            }

            builder.Append(token);
        }

        return builder.ToString();
    }

    /// <summary>Функції, що мають однойменний відповідник в Excel.</summary>
    /// <remarks>
    /// ⛔ <c>CONVERT</c> у переліку немає: в Excel такої функції немає, і
    /// експорт «із формулами» саме тут втрачає частину семантики. Це
    /// говориться користувачеві, а не приховується — при зворотному імпорті
    /// множення на константу назад у <c>CONVERT</c> не перетворюється.
    /// </remarks>
    private static readonly HashSet<string> ExcelFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVERAGE", "MIN", "MAX", "COUNT", "ROUND", "ABS", "IF", "IFERROR", "AND", "OR", "NOT",
    };

    private static bool IsKnownFunction(string token) => ExcelFunctions.Contains(token);

    /// <summary>Пише вузол у синтаксисі Excel.</summary>
    private static void Write(
        AstNode node,
        IReadOnlyDictionary<(string, string, string), string> coordinates,
        System.Text.StringBuilder builder)
    {
        switch (node)
        {
            case LiteralNode literal:
                builder.Append(Literal(literal));
                break;

            case UnaryNode unary:
                builder.Append(unary.Operator switch
                {
                    UnaryOperator.Negate => '-',
                    UnaryOperator.Not => '!',
                    _ => '+',
                });
                Write(unary.Operand, coordinates, builder);
                break;

            case BinaryNode { Operator: BinaryOperator.Modulo } modulo:
                // ⚠ Остача в Excel — функція MOD, а не оператор. Інфіксного
                // відповідника немає, і '%' в Excel означає відсоток: вираз
                // a % b порахував би зовсім інше й не впав би.
                builder.Append("MOD(");
                Write(modulo.Left, coordinates, builder);
                builder.Append(',');
                Write(modulo.Right, coordinates, builder);
                builder.Append(')');
                break;

            case BinaryNode binary:
                builder.Append('(');
                Write(binary.Left, coordinates, builder);
                builder.Append(Operator(binary.Operator));
                Write(binary.Right, coordinates, builder);
                builder.Append(')');
                break;

            case ConditionalNode conditional:
                builder.Append("IF(");
                Write(conditional.Condition, coordinates, builder);
                builder.Append(',');
                Write(conditional.WhenTrue, coordinates, builder);
                builder.Append(',');
                Write(conditional.WhenFalse, coordinates, builder);
                builder.Append(')');
                break;

            case FunctionNode function:
                WriteFunction(function, coordinates, builder);
                break;

            case CellReferenceNode reference:
                builder.Append(Reference(reference, coordinates));
                break;

            default:
                // Діалект методологій (<c>@Arg</c>, <c>CST.X</c>) і календарний
                // контекст в Excel відповідника не мають: вони живуть у
                // методології, а не в книзі.
                builder.Append(MissingReference);
                break;
        }
    }

    private static void WriteFunction(
        FunctionNode function,
        IReadOnlyDictionary<(string, string, string), string> coordinates,
        System.Text.StringBuilder builder)
    {
        // ⚠ CONVERT перетворюється на саме значення без множника: коефіцієнт
        // живе в довіднику одиниць, і вписати його числом у книгу означало б
        // зафіксувати конверсію на момент експорту. Користувача про втрату
        // семантики попереджає експортер.
        if (string.Equals(function.Name, "CONVERT", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count > 0)
        {
            Write(function.Arguments[0], coordinates, builder);
            return;
        }

        if (!IsKnownFunction(function.Name))
        {
            builder.Append(MissingReference);
            return;
        }

        builder.Append(function.Name.ToUpperInvariant()).Append('(');

        for (var i = 0; i < function.Arguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            Write(function.Arguments[i], coordinates, builder);
        }

        builder.Append(')');
    }

    /// <summary>Посилання на комірку або діапазон у координатах книги.</summary>
    private static string Reference(
        CellReferenceNode reference, IReadOnlyDictionary<(string, string, string), string> coordinates)
    {
        var table = reference.TableCode ?? string.Empty;
        var column = reference.ColumnSelector;

        switch (reference.Row)
        {
            case RowSelector.Single single:
                return coordinates.TryGetValue((table, single.RowKey, column), out var one)
                    ? one
                    : MissingReference;

            case RowSelector.Range range:
                var from = coordinates.TryGetValue((table, range.FromRowKey, column), out var head)
                    ? head
                    : null;
                var to = coordinates.TryGetValue((table, range.ToRowKey, column), out var tail) ? tail : null;

                // Діапазон, у якого відомий лише один кінець, — не діапазон.
                // Excel прийняв би A5:A5 і мовчки порахував один рядок замість
                // десяти.
                return from is not null && to is not null ? $"{from}:{to}" : MissingReference;

            default:
                // Предикат обчислюється в рантаймі за даними, а поточний рядок
                // залежить від того, куди формулу поклали: обидва в статичну
                // координату не перетворюються.
                return MissingReference;
        }
    }

    private static string Literal(LiteralNode literal) => literal.Value switch
    {
        null => string.Empty,
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "TRUE" : "FALSE",
        DateTime date => $"DATE({date.Year},{date.Month},{date.Day})",
        _ => "\"" + literal.Value.ToString()?.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
    };

    private static string Operator(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Power => "^",
        BinaryOperator.Concat => "&",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.Less => "<",
        BinaryOperator.LessOrEqual => "<=",
        BinaryOperator.Greater => ">",
        BinaryOperator.GreaterOrEqual => ">=",
        BinaryOperator.And => "*",
        BinaryOperator.Or => "+",
        _ => "+",
    };

    /// <summary>Адреса без знаків абсолютності: <c>$B$7</c> і <c>B7</c> — одна комірка.</summary>
    private static string Normalize(string address)
        => address.Replace("$", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
#pragma warning restore CA1822
