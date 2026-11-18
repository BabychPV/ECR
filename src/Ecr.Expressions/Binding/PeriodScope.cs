// src/Ecr.Expressions/Binding/PeriodScope.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Binding;

/// <summary>B179 L1: яку частину довідника за періодом читає виклик <c>REG*</c>.</summary>
public enum PeriodScopeKind : byte
{
    /// <summary>Ключі — <c>[Period].Year</c> і <c>[Period].Sequence</c>: місяць споживача = місяць джерела.</summary>
    Month = 0,

    /// <summary>Ключ — лише <c>[Period].Year</c> (можливо, з літеральними місяцями, див. <see cref="PeriodScope.MonthMask"/>).</summary>
    Year = 1,

    /// <summary>Період у ключах не впізнано — будь-який рік (безпечна надмножина).</summary>
    All = 2,
}

/// <summary>
/// B179 L1: обсяг читання довідника за періодом для ребра графа залежностей (дизайн B179 §2 «PeriodScope»).
/// </summary>
/// <param name="Kind">Вид обсягу.</param>
/// <param name="MonthMask">
/// Лише для <see cref="PeriodScopeKind.Year"/>: біти 1…12 літеральних місяців (річна розкладка D-474 переписує
/// <c>[Period].Sequence</c> на число m у колонці <c>M{m}</c>); 0 — увесь рік.
/// </param>
public readonly record struct PeriodScope(PeriodScopeKind Kind, int MonthMask)
{
    /// <summary>Місяць споживача = місяць джерела.</summary>
    public static PeriodScope Month { get; } = new(PeriodScopeKind.Month, 0);

    /// <summary>Увесь рік споживача = рік джерела.</summary>
    public static PeriodScope Year { get; } = new(PeriodScopeKind.Year, 0);

    /// <summary>Будь-який період.</summary>
    public static PeriodScope All { get; } = new(PeriodScopeKind.All, 0);

    /// <summary>Рік джерела, лише літеральний місяць <paramref name="month"/>.</summary>
    /// <param name="month">Місяць 1…12.</param>
    public static PeriodScope FixedMonth(int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        return new(PeriodScopeKind.Year, 1 << month);
    }

    /// <summary>Найвужчий обсяг, що покриває обидва (кілька формул на одному ребрі).</summary>
    /// <param name="other">Другий обсяг.</param>
    /// <remarks>
    /// ⚠ «Місяць» ∪ «літеральні місяці» не виражається жодним із двох — розширюється до всього року: зайвий
    /// перерахунок дешевший за пропущений.
    /// </remarks>
    public PeriodScope Join(PeriodScope other)
    {
        if (Kind == PeriodScopeKind.All || other.Kind == PeriodScopeKind.All)
        {
            return All;
        }

        if (Kind == PeriodScopeKind.Month && other.Kind == PeriodScopeKind.Month)
        {
            return Month;
        }

        if (Kind == PeriodScopeKind.Year && other.Kind == PeriodScopeKind.Year && MonthMask != 0 && other.MonthMask != 0)
        {
            return new(PeriodScopeKind.Year, MonthMask | other.MonthMask);
        }

        return Year;
    }
}

/// <summary>
/// B179 L1: обсяги читання довідників виразом — за AST, без сканування тексту.
/// </summary>
/// <remarks>
/// Правила (дизайн B179 §2): для кожного виклику <c>REG*</c> із літеральним кодом довідника:
/// <list type="bullet">
/// <item><c>REGFIND('X', k1, k2, …)</c> — позиційні ключі: <c>[Period].Year</c>, а одразу за ним
/// <c>[Period].Sequence</c> → <see cref="PeriodScope.Month"/>; за ним цілий літерал 1…12 →
/// <see cref="PeriodScope.FixedMonth"/>; інше → <see cref="PeriodScope.Year"/>. Без <c>[Period].Year</c> →
/// <see cref="PeriodScope.All"/>.</item>
/// <item>Області рядка (<c>REGONE</c>, агрегати) — рівності <c>ROW.f = …</c> у ланцюжку <c>AND</c> аргументів-умов:
/// рівність на <c>[Period].Year</c> і на <c>[Period].Sequence</c> → місяць; лише рік → рік; інакше — усе.</item>
/// </list>
/// ⚠ Лише точні вузли поточного періоду (<c>PeriodOffset = 0</c>): <c>[Period].Year - 1</c> — це інший рік, тож
/// він не впізнається і дає <see cref="PeriodScope.All"/> (безпечно). Кілька викликів одного довідника — об'єднання
/// (<see cref="PeriodScope.Join"/>).
/// </remarks>
public static class RegistryReadScopes
{
    /// <summary>Обсяг кожного довідника, який вираз читає формами <c>REG*</c> діалекту.</summary>
    /// <param name="root">Корінь розібраного виразу.</param>
    /// <param name="dialect">Діалект виразу (набір форм довідників залежить від нього).</param>
    /// <returns>Код довідника (без регістру) → обсяг.</returns>
    public static IReadOnlyDictionary<string, PeriodScope> Collect(AstNode root, ExpressionDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(root);

        var scopes = new Dictionary<string, PeriodScope>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<AstNode>();
        pending.Push(root);

        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case FunctionNode function:
                    if (RegistryForms.Handles(function.Name, dialect)
                        && function.Arguments.Count > 0
                        && ReferenceResolver.RegistryCodeLiteral(function.Arguments[0]) is { } code)
                    {
                        var scope = OfCall(function);
                        scopes[code] = scopes.TryGetValue(code, out var known) ? known.Join(scope) : scope;
                    }

                    foreach (var argument in function.Arguments)
                    {
                        pending.Push(argument);
                    }

                    break;

                case BinaryNode binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.Condition);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.WhenFalse);
                    break;

                default:
                    break;
            }
        }

        return scopes;
    }

    /// <summary>Обсяг одного виклику форми довідника.</summary>
    /// <param name="call">Виклик із літеральним кодом довідника першим аргументом.</param>
    public static PeriodScope OfCall(FunctionNode call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (string.Equals(call.Name, RegistryForms.Find, StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 1; i < call.Arguments.Count; i++)
            {
                if (!IsPeriod(call.Arguments[i], "Year"))
                {
                    continue;
                }

                var next = i + 1 < call.Arguments.Count ? call.Arguments[i + 1] : null;
                if (next is not null && IsPeriod(next, "Sequence"))
                {
                    return PeriodScope.Month;
                }

                return next is LiteralNode { Type: ExpressionValueType.Number, Value: decimal month }
                       && month == decimal.Truncate(month) && month is >= 1 and <= 12
                    ? PeriodScope.FixedMonth((int)month)
                    : PeriodScope.Year;
            }

            return PeriodScope.All;
        }

        var year = false;
        var sequence = false;
        for (var i = 1; i < call.Arguments.Count; i++)
        {
            foreach (var equality in Conjuncts(call.Arguments[i]))
            {
                var other = equality.Left is RowFieldNode ? equality.Right
                    : equality.Right is RowFieldNode ? equality.Left
                    : null;
                year |= other is not null && IsPeriod(other, "Year");
                sequence |= other is not null && IsPeriod(other, "Sequence");
            }
        }

        return year ? (sequence ? PeriodScope.Month : PeriodScope.Year) : PeriodScope.All;
    }

    private static bool IsPeriod(AstNode node, string property)
        => node is PeriodPropertyNode { PeriodOffset: 0 } period
           && string.Equals(period.Property, property, StringComparison.OrdinalIgnoreCase);

    /// <summary>Рівності верхнього ланцюжка <c>AND</c> умови (під <c>OR</c> рівність ключем не є).</summary>
    private static IEnumerable<BinaryNode> Conjuncts(AstNode condition)
    {
        var pending = new Stack<AstNode>();
        pending.Push(condition);
        while (pending.TryPop(out var node))
        {
            if (node is BinaryNode { Operator: BinaryOperator.And } and)
            {
                pending.Push(and.Left);
                pending.Push(and.Right);
            }
            else if (node is BinaryNode { Operator: BinaryOperator.Equal } equality)
            {
                yield return equality;
            }
        }
    }
}
