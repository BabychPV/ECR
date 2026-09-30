using Ecr.Expressions.Ast;

namespace Ecr.Calculations;

/// <summary>Вид входу кроку трейсу (FEATURE-HSE301-VIEW §7.2).</summary>
public enum TraceInputKind : byte
{
    /// <summary><c>@Arg</c> — значення комірки рядка.</summary>
    Argument = 0,

    /// <summary><c>CST.X</c> — константа методології.</summary>
    Constant = 1,

    /// <summary><c>!Formula</c> — результат іншої формули версії.</summary>
    Formula = 2,

    /// <summary><c>[Period].X</c> — календарний контекст.</summary>
    Period = 3,
}

/// <summary>Одне посилання виразу, яке читає обчислення.</summary>
/// <param name="Kind">Вид посилання.</param>
/// <param name="Code">Код аргументу, константи чи формули; для періоду — властивість.</param>
/// <param name="Node">Вузол AST — саме його рушій обчислює, щоб дати значення входу.</param>
public sealed record TraceReference(TraceInputKind Kind, string Code, AstNode Node)
{
    /// <summary>Зсув періоду; ненульовий лише для <c>[Period-1].X</c>.</summary>
    public int PeriodOffset => Node is PeriodPropertyNode period ? period.PeriodOffset : 0;
}

/// <summary>
/// Збирає входи кроку обходом AST: <c>@Arg</c>, <c>CST.</c>, <c>!Formula</c>,
/// <c>[Period].X</c> (рішення V-8, <c>D-177</c>).
/// </summary>
/// <remarks>
/// ⛔ Лише ЗБИРАЄ посилання — значень не читає. Значення входу бере той самий рушій
/// із того самого контексту, що рахував формулу (<c>GenericCalculationModule</c>):
/// друга семантика читання тут розійшлася б із рушієм, і трейс пояснював би не те
/// число, яке пішло в результат.
/// <para>
/// ⚠ Порядок — перше входження в тексті виразу, повтори відкинуто: <c>!V * !V</c> —
/// один вхід. Шапки (<c>HDR.</c>) і комірки методологія не читає (02b §3.4), тож і
/// входами вони не стають.
/// </para>
/// </remarks>
public static class ReferenceCollector
{
    /// <summary>Посилання виразу в порядку першого входження.</summary>
    /// <param name="root">Корінь розібраного виразу.</param>
    /// <returns>Посилання без повторів.</returns>
    public static IReadOnlyList<TraceReference> Collect(AstNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<TraceReference>();
        var seen = new HashSet<(TraceInputKind, string, int)>();

        // ⚠ Стек із правими дітьми першими — обхід зліва направо без рекурсії: глибина
        // виразу обмежена бюджетом рушія, а не стеком потоку воркера.
        var pending = new Stack<AstNode>();
        pending.Push(root);

        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case SymbolReferenceNode { Kind: SymbolKind.Argument } symbol:
                    Add(found, seen, new TraceReference(TraceInputKind.Argument, symbol.Name, symbol));
                    break;

                case SymbolReferenceNode { Kind: SymbolKind.Constant } symbol:
                    Add(found, seen, new TraceReference(TraceInputKind.Constant, symbol.Name, symbol));
                    break;

                case SymbolReferenceNode { Kind: SymbolKind.Formula } symbol:
                    Add(found, seen, new TraceReference(TraceInputKind.Formula, symbol.Name, symbol));
                    break;

                case PeriodPropertyNode period:
                    Add(found, seen, new TraceReference(TraceInputKind.Period, period.Property, period));
                    break;

                case BinaryNode binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.WhenFalse);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.Condition);
                    break;

                case FunctionNode function:
                    for (var i = function.Arguments.Count - 1; i >= 0; i--)
                    {
                        pending.Push(function.Arguments[i]);
                    }

                    break;

                default:
                    break;
            }
        }

        return found;
    }

    private static void Add(
        List<TraceReference> found, HashSet<(TraceInputKind, string, int)> seen, TraceReference reference)
    {
        if (seen.Add((reference.Kind, reference.Code.ToUpperInvariant(), reference.PeriodOffset)))
        {
            found.Add(reference);
        }
    }
}
