using System.Globalization;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Справжня глибина вкладеності дерева виразу — за тими самими правилами, що й
/// <see cref="EvaluationBudget.EnterNesting"/> в обчислювачі.
/// </summary>
/// <remarks>
/// ⛔ RC5: межа <see cref="EvaluationBudget.MaxNestingDepth"/> (96) — межа СТЕКА
/// обчислення, і раніше вона відкривалась автору лише як мовчазне <c>#BUDGET</c>
/// у готовому документі. Публікація й редактор мають відхиляти формулу, яку
/// обчислювач однаково не порахує, тож міряти треба рівно те, що міряє він.
///
/// ⚠ Лівий гребінь бінарних вузлів (<c>a+b-c*d</c>) — ОДИН рівень, а не N:
/// <see cref="Evaluator"/> обходить його циклом. Глибину дають праві піддерева,
/// аргументи функцій, гілки <c>IF</c>, операнд унарного мінуса.
///
/// ⚠ Обхід тут ітеративний, з явним стеком, а не рекурсивний: його кличуть із
/// перевірок публікації й редактора на вході, який ще може бути завеликим, і
/// рекурсія в ньому відтворила б ту саму аварію, від якої він захищає.
/// Відповідність обчислювачеві доводить <c>ExpressionNestingTests</c>: він
/// порівнює це число з <see cref="EvaluationBudget.Deepest"/> на корпусі.
/// </remarks>
public static class ExpressionNesting
{
    /// <summary>Ключ каталогу зауваження «формула надто глибока»; підстановки <c>depth</c> і <c>max</c>.</summary>
    public const string MessageKey = "expr.tooDeep";

    /// <summary>
    /// Зауваження, якщо глибина дерева перевищує межу обчислення; <c>null</c> — глибина в межах.
    /// </summary>
    /// <param name="root">Корінь дерева.</param>
    /// <returns>Зауваження з ключем <see cref="MessageKey"/> або <c>null</c>.</returns>
    /// <remarks>
    /// ⚠ Єдине місце, де межа 96 стає відмовою для автора: публікація шаблону й методології,
    /// збереження формули й редактор кличуть саме його, а не міряють кожен по-своєму.
    /// </remarks>
    public static ExpressionDiagnostic? Diagnose(AstNode root)
    {
        var depth = Measure(root);
        if (depth <= EvaluationBudget.MaxNestingDepth)
        {
            return null;
        }

        var max = EvaluationBudget.MaxNestingDepth;
        return new ExpressionDiagnostic(
            ExpressionErrors.Syntax,
            $"The formula is too complex: nesting depth {depth}, allowed {max}. Split it into several calculated columns.",
            root.Position,
            1,
            MessageKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["depth"] = depth.ToString(CultureInfo.InvariantCulture),
                ["max"] = max.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Глибина дерева: найбільша кількість одночасно зайнятих рівнів обчислювача.</summary>
    /// <param name="root">Корінь дерева.</param>
    /// <returns>Число від 1 (літерал) і більше.</returns>
    public static int Measure(AstNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var deepest = 0;
        var pending = new Stack<(AstNode Node, int Depth)>();
        pending.Push((root, 1));

        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (depth > deepest)
            {
                deepest = depth;
            }

            switch (node)
            {
                case BinaryNode binary:
                    // Увесь лівий гребінь — один рівень: правий операнд кожного вузла
                    // й крайній лівий операнд обчислюються на глибині depth + 1.
                    var spine = binary;
                    while (true)
                    {
                        pending.Push((spine.Right, depth + 1));
                        if (spine.Left is BinaryNode inner)
                        {
                            spine = inner;
                            continue;
                        }

                        pending.Push((spine.Left, depth + 1));
                        break;
                    }

                    break;

                case UnaryNode unary:
                    pending.Push((unary.Operand, depth + 1));
                    break;

                case ConditionalNode conditional:
                    pending.Push((conditional.Condition, depth + 1));
                    pending.Push((conditional.WhenTrue, depth + 1));
                    pending.Push((conditional.WhenFalse, depth + 1));
                    break;

                case FunctionNode function:
                    foreach (var argument in function.Arguments)
                    {
                        pending.Push((argument, depth + 1));
                    }

                    break;
            }
        }

        return deepest;
    }
}
