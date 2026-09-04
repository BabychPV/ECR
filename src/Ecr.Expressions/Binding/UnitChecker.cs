using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє сумісність одиниць. **Головна цінність механізму одиниць** —
/// саме ця перевірка: помилка ловиться до продуктиву, а не на звірці через
/// місяць (ФВ-16.7).
/// </summary>
public sealed class UnitChecker
{
    /// <summary>Виводить одиницю результату і перевіряє сумісність операндів.</summary>
    /// <param name="node">Вузол виразу.</param>
    /// <param name="context">Джерело одиниць.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    /// <returns>Ідентифікатор одиниці результату або <c>null</c>, якщо вираз безрозмірний.</returns>
    public int? Check(AstNode node, IUnitContext context, List<ExpressionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);

        switch (node)
        {
            case LiteralNode:
            case PeriodPropertyNode:
                // Літерал безрозмірний: `* 1000` не робить із тонн кілограми,
                // це робить CONVERT (D-74).
                return null;

            case CellReferenceNode reference:
                return context.GetReferenceUnit(reference);

            case SymbolReferenceNode { Kind: SymbolKind.Constant } constant:
                return context.GetConstantUnit(constant.Name);

            case SymbolReferenceNode:
                return null;

            case UnaryNode unary:
                return Check(unary.Operand, context, diagnostics);

            case ConditionalNode conditional:
                return Same(
                    Check(conditional.WhenTrue, context, diagnostics),
                    Check(conditional.WhenFalse, context, diagnostics),
                    node, context, diagnostics,
                    "Гілки умови мають бути в одній одиниці.");

            case BinaryNode binary:
                return CheckBinary(binary, context, diagnostics);

            case FunctionNode function:
                return CheckFunction(function, context, diagnostics);

            default:
                return null;
        }
    }

    private int? CheckBinary(BinaryNode node, IUnitContext context, List<ExpressionDiagnostic> diagnostics)
    {
        var left = Check(node.Left, context, diagnostics);
        var right = Check(node.Right, context, diagnostics);

        switch (node.Operator)
        {
            case BinaryOperator.Add:
            case BinaryOperator.Subtract:
                // ⚠ Неявних конверсій не буває (D-74). Тонни плюс кілограми —
                // це не «приблизно правильно», це число, помножене на тисячу.
                return Same(left, right, node, context, diagnostics,
                    "Додавання значень у різних одиницях потребує явного CONVERT.");

            case BinaryOperator.Multiply:
            {
                if (left is null || right is null)
                {
                    return left ?? right;
                }

                // Добуток двох розмірних величин дає похідну одиницю; якщо
                // такої в довіднику немає, її не можна вигадати.
                Report(diagnostics, node, "Добуток двох розмірних величин не має оголошеної одиниці.");
                return null;
            }

            case BinaryOperator.Divide:
            {
                if (right is null)
                {
                    return left;
                }

                if (left is null)
                {
                    Report(diagnostics, node, "Ділення безрозмірного на розмірне не має оголошеної одиниці.");
                    return null;
                }

                var derived = context.FindDerived(left.Value, right.Value);
                if (derived is null)
                {
                    Report(diagnostics, node,
                        "Похідної одиниці для цього ділення немає в довіднику uom.Unit.");
                }

                return derived;
            }

            case BinaryOperator.Less:
            case BinaryOperator.LessOrEqual:
            case BinaryOperator.Greater:
            case BinaryOperator.GreaterOrEqual:
                Same(left, right, node, context, diagnostics,
                    "Порівняння значень у різних одиницях потребує явного CONVERT.");
                return null;

            default:
                return null;
        }
    }

    private int? CheckFunction(FunctionNode node, IUnitContext context, List<ExpressionDiagnostic> diagnostics)
    {
        var units = node.Arguments.Select(a => Check(a, context, diagnostics)).ToList();

        switch (node.Name.ToUpperInvariant())
        {
            case "SUM" or "AVERAGE" or "MIN" or "MAX" or "PRODUCT" or "SUMIF":
            {
                // Агрегація різних одиниць — той самий D-74, лише розмазаний
                // по діапазону: одна комірка в кілограмах усередині тонн дає
                // підсумок, який виглядає правдоподібно.
                int? result = null;
                foreach (var unit in units.Where(u => u is not null))
                {
                    result = result is null
                        ? unit
                        : Same(result, unit, node, context, diagnostics,
                            "Агрегація значень у різних одиницях потребує явного CONVERT.");
                }

                return result;
            }

            case "ROUND" or "ABS":
                return units.Count > 0 ? units[0] : null;

            // ⚠ CONVERT повертає одиницю за КОДОМ із другого аргументу, і код
            // резолвиться в uom.Unit — це Етап 4 разом із самим довідником.
            // Тут вона лишається безрозмірною свідомо, щоб не вигадувати
            // ідентифікатор, якого ще немає.
            default:
                return null;
        }
    }

    private static int? Same(
        int? left,
        int? right,
        AstNode node,
        IUnitContext context,
        List<ExpressionDiagnostic> diagnostics,
        string message)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null || left == right)
        {
            return left;
        }

        var leftDimension = context.GetDimension(left.Value);
        var rightDimension = context.GetDimension(right.Value);

        Report(diagnostics, node,
            leftDimension == rightDimension
                ? message
                : "Операнди різних розмірностей: конверсія між ними неможлива в принципі.");

        return left;
    }

    private static void Report(List<ExpressionDiagnostic> diagnostics, AstNode node, string message)
        => diagnostics.Add(new ExpressionDiagnostic(
            ExpressionErrors.UnitMismatch, message, node.Position, 1));
}

/// <summary>Джерело одиниць.</summary>
public interface IUnitContext
{
    /// <summary>Одиниця посилання з дерева виразу; <c>null</c> — безрозмірне.</summary>
    public int? GetReferenceUnit(CellReferenceNode reference);

    /// <summary>Одиниця колонки; <c>null</c> — безрозмірна.</summary>
    public int? GetColumnUnit(int tableDefId, int columnDefId);

    /// <summary>Одиниця константи методології.</summary>
    public int? GetConstantUnit(string code);

    /// <summary>Розмірність одиниці.</summary>
    public byte GetDimension(int unitId);

    /// <summary>Шукає похідну одиницю за чисельником і знаменником.</summary>
    public int? FindDerived(int numeratorUnitId, int denominatorUnitId);
}
