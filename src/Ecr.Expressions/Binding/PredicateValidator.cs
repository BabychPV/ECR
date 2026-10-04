using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевірка №11 з переліку публікації: предикат динамічного діапазону не
/// містить заборонених конструкцій (02b §4.2, §12).
/// </summary>
/// <remarks>
/// ⚠ Межа тут проведена не з обережності. Предикат обчислюється в рантаймі
/// НАД КОЖНИМ рядком таблиці, і кожна дозволена конструкція множиться на
/// кількість рядків. Агрегат усередині предиката означав би агрегат на кожному
/// рядку — квадратична вартість на порожньому місці. Крос-періодне посилання
/// означало б читання чужого періоду під час фільтрації свого.
///
/// Але головна причина інша: дозволивши функції й вкладені предикати, ми
/// отримали б другу мову всередині мови — з власними правилами типів,
/// власними помилками і власним рушієм.
/// </remarks>
public static class PredicateValidator
{
    /// <summary>Перевіряє всі предикати у виразі.</summary>
    /// <param name="root">Корінь виразу.</param>
    /// <param name="diagnostics">Куди складати зауваження.</param>
    public static void Validate(AstNode root, List<ExpressionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (var predicate in FindPredicates(root))
        {
            CheckCondition(predicate, diagnostics);
        }
    }

    /// <remarks>
    /// ⛔ L7-01 (аудит 2026-10-03): обхід ЯВНИМ стеком, а не рекурсивним
    /// ітератором. Кожен рівень вкладеного <c>yield</c> — окремий кадр
    /// <c>MoveNext</c> на стеку потоку, і лівий гребінь <c>1+1+…</c> на 32 000
    /// доданків вбивав процес API саме тут. Порядок обходу той самий —
    /// зліва направо, згори вниз.
    /// </remarks>
    private static IEnumerable<AstNode> FindPredicates(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();

            if (node is CellReferenceNode { Row: RowSelector.Predicate predicate })
            {
                yield return predicate.Condition;
            }

            var children = Children(node).ToList();
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }
    }

    /// <summary>Перевіряє одну умову предиката згори вниз.</summary>
    /// <remarks>
    /// ⛔ Раніше цей метод мав ще й параметр <c>depth</c>: його передавали як
    /// <c>depth: 0</c>, збільшували на <c>depth + 1</c> у рекурсивній гілці — і
    /// НЕ ЧИТАЛИ ЖОДНОГО РАЗУ. Це гірше за відсутність межі: код виглядав
    /// обмеженим, і саме тому ніхто не шукав справжню межу там, де її бракувало
    /// (<see cref="Parsing.Parser"/>, рекурсивний спуск без жодного лічильника —
    /// <c>StackOverflowException</c>, який у .NET не перехоплюється і валить
    /// увесь процес).
    ///
    /// ⚠ Параметр ПРИБРАНО, а не доведено до діла, і це свідомий вибір місця
    /// для межі. Дерево сюди приходить рівно з одного джерела — парсера, — а він
    /// тепер обмежує глибину сам (<see cref="Parsing.Parser.MaxRecursionDepth"/>),
    /// тобто рекурсія тут обмежена вже на вході. Другий лічильник з власним
    /// числом означав би дві різні правди про те, який вираз занадто глибокий,
    /// і жодного місця, де це написано один раз.
    /// </remarks>
    private static void CheckCondition(AstNode node, List<ExpressionDiagnostic> diagnostics)
    {
        // ⚠ Умова предиката — довільний вираз, тож і тут лівий гребінь (L7-01).
        if (!TraversalStackGuard.TryEnter(node, diagnostics))
        {
            return;
        }

        switch (node)
        {
            case FunctionNode function:
                Report(diagnostics, node,
                    "expr.predicate.functionCall", DiagnosticParams.Of(("name", function.Name)),
                    $"Calling function \"{function.Name}\" in a predicate is not allowed: the predicate "
                    + "is evaluated for every row of the table.");
                return;

            case CellReferenceNode reference:
            {
                if (reference.PeriodOffset != 0)
                {
                    Report(diagnostics, node,
                        "expr.predicate.crossPeriod", null,
                        "A cross-period reference is not allowed in a predicate: filtering this period "
                        + "must not read another one.");
                }

                if (reference.Row is RowSelector.Predicate)
                {
                    Report(diagnostics, node, "expr.predicate.nested", null, "A nested predicate is not allowed.");
                }
                else if (reference.Row is not RowSelector.Current)
                {
                    Report(diagnostics, node,
                        "expr.predicate.sameRowOnly", null,
                        "A predicate may reference only columns of the SAME row.");
                }

                return;
            }

            case ConditionalNode:
                Report(diagnostics, node, "expr.predicate.ternary", null, "The ternary operator is not allowed in a predicate.");
                return;

            default:
                foreach (var child in Children(node))
                {
                    CheckCondition(child, diagnostics);
                }

                return;
        }
    }

    private static IEnumerable<AstNode> Children(AstNode node)
        => node switch
        {
            UnaryNode unary => [unary.Operand],
            BinaryNode binary => [binary.Left, binary.Right],
            ConditionalNode conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            FunctionNode function => function.Arguments,
            CellReferenceNode { Row: RowSelector.Predicate predicate } => [predicate.Condition],
            _ => [],
        };

    private static void Report(
        List<ExpressionDiagnostic> diagnostics,
        AstNode node,
        string messageKey,
        IReadOnlyDictionary<string, string>? messageParams,
        string message)
        => diagnostics.Add(new ExpressionDiagnostic(
            ExpressionErrors.Syntax, message, node.Position, 1, messageKey, messageParams));
}
