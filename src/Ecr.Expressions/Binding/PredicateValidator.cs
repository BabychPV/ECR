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

    private static IEnumerable<AstNode> FindPredicates(AstNode node)
    {
        if (node is CellReferenceNode { Row: RowSelector.Predicate predicate })
        {
            yield return predicate.Condition;
        }

        foreach (var child in Children(node))
        {
            foreach (var found in FindPredicates(child))
            {
                yield return found;
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
        switch (node)
        {
            case FunctionNode function:
                Report(diagnostics, node,
                    $"Виклик функції '{function.Name}' у предикаті заборонений: предикат обчислюється " +
                    "над кожним рядком таблиці.");
                return;

            case CellReferenceNode reference:
            {
                if (reference.PeriodOffset != 0)
                {
                    Report(diagnostics, node,
                        "Крос-періодне посилання в предикаті заборонене: фільтрація свого періоду " +
                        "не має читати чужий.");
                }

                if (reference.Row is RowSelector.Predicate)
                {
                    Report(diagnostics, node, "Вкладений предикат заборонений.");
                }
                else if (reference.Row is not RowSelector.Current)
                {
                    Report(diagnostics, node,
                        "У предикаті дозволені лише колонки ТОГО САМОГО рядка.");
                }

                return;
            }

            case ConditionalNode:
                Report(diagnostics, node, "Тернарний оператор у предикаті заборонений.");
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

    private static void Report(List<ExpressionDiagnostic> diagnostics, AstNode node, string message)
        => diagnostics.Add(new ExpressionDiagnostic(ExpressionErrors.Syntax, message, node.Position, 1));
}
