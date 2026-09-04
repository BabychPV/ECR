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
                // ⛔ Колонка з одиницею НА РЯДОК (ФВ-16.8, D-87): підсумувати її
                // без CONVERT не можна взагалі. Тут навіть немає двох одиниць,
                // які видно у виразі, — одиниця лежить у кожній комірці, і
                // SUM склав би тонни з кілограмами, не давши жодного натяку.
                foreach (var argument in node.Arguments)
                {
                    if (HasRowScopedUnit(argument, context))
                    {
                        Report(diagnostics, node,
                            "Агрегація колонки з одиницею на рядок потребує явного CONVERT "
                            + "до спільної одиниці (ФВ-16.8).");
                    }
                }

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

            case "CONVERT":
                return CheckConvert(node, context, diagnostics);

            default:
                return null;
        }
    }

    /// <summary>Одиниця результату <c>CONVERT(значення, 'з', 'у')</c>.</summary>
    /// <remarks>
    /// ⚠ Це ЄДИНЕ місце, де одиниця змінюється (D-74). Саме тому код цільової
    /// одиниці резолвиться тут, а не тлумачиться як рядковий літерал: інакше
    /// <c>CONVERT(x, 'kg', 'g')</c> лишався б безрозмірним, і наступне
    /// додавання до грамів проходило б перевірку випадково.
    /// </remarks>
    private static int? CheckConvert(
        FunctionNode node, IUnitContext context, List<ExpressionDiagnostic> diagnostics)
    {
        if (node.Arguments.Count < 3)
        {
            Report(diagnostics, node,
                "CONVERT потребує трьох аргументів: значення, вихідна одиниця, цільова одиниця.");
            return null;
        }

        // ⚠ ЦІЛЬОВА одиниця мусить бути літералом — і саме вона робить
        // перевірку можливою: результат виразу має оголошену одиницю, яку
        // видно при публікації. Обчислювана ціль означала б, що одиниця
        // результату відома лише в рантаймі, тобто не перевіряється взагалі.
        var to = UnitCode(node.Arguments[2]);
        if (to is null)
        {
            Report(diagnostics, node,
                "Цільова одиниця CONVERT задається літералом, а не виразом: "
                + "інакше одиниця результату невідома до запуску.");
            return null;
        }

        var target = context.ResolveUnitByCode(to);
        if (target is null)
        {
            Report(diagnostics, node, $"Одиниці «{to}» немає в довіднику uom.Unit.");
            return null;
        }

        // ⚠ ВИХІДНА одиниця може бути посиланням на колонку `DataType = Unit`
        // (ФВ-16.8): саме так фікстура пише `CONVERT([Amount], [AmountUnit],
        // 'kg')`. Розмірність такого джерела при публікації невідома — її
        // звіряє рантайм, а тут вимагати літерал означало б заборонити
        // єдиний легальний спосіб звести одиницю на рядок до спільної.
        var from = UnitCode(node.Arguments[1]);
        if (from is null)
        {
            if (node.Arguments[1] is not CellReferenceNode)
            {
                Report(diagnostics, node,
                    "Вихідна одиниця CONVERT — це літерал або посилання на колонку одиниці.");
            }

            return target;
        }

        var source = context.ResolveUnitByCode(from);
        if (source is null)
        {
            Report(diagnostics, node, $"Одиниці «{from}» немає в довіднику uom.Unit.");
            return null;
        }

        // ⛔ Різні розмірності — відмова, а не спроба «через базу»: саме тут
        // щільність не стає конверсією (ФВ-16.3, ФВ-16.5).
        if (context.GetDimension(source.Value) != context.GetDimension(target.Value))
        {
            Report(diagnostics, node,
                $"CONVERT з «{from}» у «{to}» неможливий: різні розмірності. "
                + "Потрібен контекстний коефіцієнт, а він належить методології.");
            return null;
        }

        return target;
    }

    /// <summary>Код одиниці з літерала; <c>null</c> — це не літерал.</summary>
    private static string? UnitCode(AstNode node)
        => node is LiteralNode { Value: string code } ? code : null;

    /// <summary>
    /// Чи містить піддерево посилання на колонку з одиницею на рядок, яку не
    /// привели <c>CONVERT</c>-ом.
    /// </summary>
    /// <remarks>
    /// Обхід зупиняється на <c>CONVERT</c>: усе, що під ним, уже приведено до
    /// однієї одиниці, і саме заради цього конверсія й написана.
    /// </remarks>
    private static bool HasRowScopedUnit(AstNode node, IUnitContext context)
        => node switch
        {
            FunctionNode f when f.Name.Equals("CONVERT", StringComparison.OrdinalIgnoreCase) => false,
            CellReferenceNode reference => context.IsRowScopedUnit(reference),
            FunctionNode f => f.Arguments.Any(a => HasRowScopedUnit(a, context)),
            BinaryNode b => HasRowScopedUnit(b.Left, context) || HasRowScopedUnit(b.Right, context),
            UnaryNode u => HasRowScopedUnit(u.Operand, context),
            ConditionalNode c => HasRowScopedUnit(c.WhenTrue, context)
                                 || HasRowScopedUnit(c.WhenFalse, context),
            _ => false,
        };

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

    /// <summary>Одиниця за кодом із <c>CONVERT</c>; <c>null</c> — такої немає.</summary>
    public int? ResolveUnitByCode(string code);

    /// <summary>
    /// Чи задається одиниця цього посилання **на рядок**
    /// (<c>ColumnDef.DataType = Unit</c>, ФВ-16.8).
    /// </summary>
    /// <remarks>
    /// Окреме питання від <see cref="GetReferenceUnit"/>: там відповідь «яка
    /// одиниця», тут — «чи є вона взагалі однією». Для такої колонки перша
    /// відповідь не існує, і <c>null</c> у ній означав би «безрозмірна» —
    /// рівно навпаки до дійсності.
    /// </remarks>
    public bool IsRowScopedUnit(CellReferenceNode reference);
}
