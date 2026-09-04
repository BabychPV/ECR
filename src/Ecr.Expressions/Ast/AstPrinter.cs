using System.Globalization;
using System.Text;

namespace Ecr.Expressions.Ast;

/// <summary>
/// Друкує дерево назад у текст виразу.
/// </summary>
/// <remarks>
/// Потрібен у двох місцях, і обидва — не косметика:
/// <list type="number">
/// <item>предикат динамічного діапазону зберігається в
/// <c>cfg.FormulaDependency.FilterJson</c> і має пережити перезапуск;</item>
/// <item>UI показує розкритий діапазон і умову («формула охоплює рядки
/// 7001001…7001005»), що знімає цілий клас питань «а чому не порахувалося».</item>
/// </list>
/// Дужки ставляться навколо кожної складеної операції: надлишкові дужки не
/// змінюють значення, а їх нестача змінює.
/// </remarks>
public static class AstPrinter
{
    /// <summary>Текстове подання виразу.</summary>
    public static string Print(AstNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var text = new StringBuilder();
        Write(node, text);
        return text.ToString();
    }

    private static void Write(AstNode node, StringBuilder text)
    {
        switch (node)
        {
            case LiteralNode literal:
                text.Append(Literal(literal));
                break;

            case UnaryNode unary:
                text.Append(unary.Operator switch
                {
                    UnaryOperator.Negate => "-",
                    UnaryOperator.Plus => "+",
                    _ => "NOT ",
                });
                Write(unary.Operand, text);
                break;

            case BinaryNode binary:
                text.Append('(');
                Write(binary.Left, text);
                text.Append(' ').Append(Operator(binary.Operator)).Append(' ');
                Write(binary.Right, text);
                text.Append(')');
                break;

            case ConditionalNode conditional:
                text.Append('(');
                Write(conditional.Condition, text);
                text.Append(" ? ");
                Write(conditional.WhenTrue, text);
                text.Append(" : ");
                Write(conditional.WhenFalse, text);
                text.Append(')');
                break;

            case FunctionNode function:
                text.Append(function.Name).Append('(');
                for (var i = 0; i < function.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(", ");
                    }

                    Write(function.Arguments[i], text);
                }

                text.Append(')');
                break;

            case CellReferenceNode reference:
                WriteReference(reference, text);
                break;

            case SymbolReferenceNode symbol:
                text.Append(symbol.Kind switch
                {
                    SymbolKind.Argument => "@" + symbol.Name,
                    SymbolKind.Constant => "CST." + symbol.Name,
                    SymbolKind.Formula => "!" + symbol.Name,
                    _ => "HDR." + symbol.Name,
                });
                break;

            case PeriodPropertyNode period:
                text.Append(Period(period.PeriodOffset)).Append('.').Append(period.Property);
                break;

            default:
                text.Append("<?>");
                break;
        }
    }

    private static void WriteReference(CellReferenceNode reference, StringBuilder text)
    {
        if (reference.PeriodOffset != 0)
        {
            text.Append(Period(reference.PeriodOffset)).Append('.');
        }

        if (reference.SheetCode is { } sheet)
        {
            text.Append('[').Append(sheet).Append("].");
        }

        if (reference.TableCode is { } table)
        {
            text.Append('[').Append(table).Append("].");
        }

        switch (reference.Row)
        {
            case RowSelector.Single single:
                text.Append('[').Append(single.RowKey).Append("].");
                break;

            case RowSelector.Range range:
                text.Append('[').Append(range.FromRowKey).Append(':').Append(range.ToRowKey).Append("].");
                break;

            case RowSelector.Predicate predicate:
                text.Append("[WHERE ");
                Write(predicate.Condition, text);
                text.Append("].");
                break;

            default:
                break;
        }

        text.Append('[').Append(reference.ColumnSelector).Append(']');
    }

    private static string Period(int offset)
        => offset == 0
            ? "[Period]"
            : string.Create(CultureInfo.InvariantCulture, $"[Period:{(offset > 0 ? "+" : "-")}{Math.Abs(offset)}]");

    private static string Literal(LiteralNode literal)
        => literal.Type switch
        {
            ExpressionValueType.Number => ((decimal)literal.Value!).ToString(CultureInfo.InvariantCulture),
            ExpressionValueType.Text => "'" + ((string)literal.Value!).Replace("'", "''", StringComparison.Ordinal) + "'",
            ExpressionValueType.Boolean => (bool)literal.Value! ? "TRUE" : "FALSE",
            ExpressionValueType.Date => ((DateTime)literal.Value!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => "NULL",
        };

    private static string Operator(BinaryOperator op)
        => op switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Power => "^",
            BinaryOperator.Concat => "&",
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "<>",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.And => "AND",
            _ => "OR",
        };
}
