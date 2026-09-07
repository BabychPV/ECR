using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє типи **при публікації**. Приведення не відбувається мовчки:
/// Excel вгадує тип і саме тому дає «майже правильні» числа; тут
/// неоднозначність — помилка, поки її ще дешево виправити (02b §5).
/// </summary>
public sealed class TypeChecker
{
    private static readonly FunctionRegistry Functions = new();

    /// <summary>Виводить тип виразу і збирає діагностики.</summary>
    /// <param name="node">Вузол виразу.</param>
    /// <param name="context">Джерело типів для посилань.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    public ExpressionValueType Check(AstNode node, ITypeContext context, List<ExpressionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);

        switch (node)
        {
            case LiteralNode literal:
                return literal.Type;

            case CellReferenceNode reference:
                return context.GetReferenceType(reference);

            case SymbolReferenceNode symbol:
                return symbol.Kind switch
                {
                    SymbolKind.Argument => context.GetArgumentType(symbol.Name),

                    // ⛔ Тут стояло `ExpressionValueType.Number` — «константа
                    // завжди число». Після появи `ConstantKind` це неправда:
                    // з 6507 констант корпусу 108 нечислові, і ~90 із них
                    // ужиті операндом порівняння. Жорстке `Number` робило б
                    // `CST.k1_CategorySelection_ = 'Summer'` «порівнянням
                    // різних типів», тобто ~90 хибних помилок публікації —
                    // щойно перевірку типів увімкнуть для методологій.
                    SymbolKind.Constant => context.GetConstantType(symbol.Name),
                    _ => ExpressionValueType.Null,
                };

            case PeriodPropertyNode period:
                return period.Property.ToUpperInvariant() is "START" or "END"
                    ? ExpressionValueType.Date
                    : ExpressionValueType.Number;

            case UnaryNode unary:
                return CheckUnary(unary, context, diagnostics);

            case BinaryNode binary:
                return CheckBinary(binary, context, diagnostics);

            case ConditionalNode conditional:
                return CheckConditional(conditional, context, diagnostics);

            case FunctionNode function:
                return CheckFunction(function, context, diagnostics);

            default:
                return ExpressionValueType.Null;
        }
    }

    private ExpressionValueType CheckUnary(
        UnaryNode node, ITypeContext context, List<ExpressionDiagnostic> diagnostics)
    {
        var operand = Check(node.Operand, context, diagnostics);

        if (node.Operator == UnaryOperator.Not)
        {
            Require(operand, ExpressionValueType.Boolean, node, diagnostics, "Заперечення застосовне лише до булевого значення.");
            return ExpressionValueType.Boolean;
        }

        Require(operand, ExpressionValueType.Number, node, diagnostics, "Унарний знак застосовний лише до числа.");
        return ExpressionValueType.Number;
    }

    private ExpressionValueType CheckBinary(
        BinaryNode node, ITypeContext context, List<ExpressionDiagnostic> diagnostics)
    {
        var left = Check(node.Left, context, diagnostics);
        var right = Check(node.Right, context, diagnostics);

        switch (node.Operator)
        {
            // Text & будь-що: друге приводиться до тексту інваріантно. Це
            // єдине дозволене неявне приведення в мові.
            case BinaryOperator.Concat:
                return ExpressionValueType.Text;

            case BinaryOperator.Add:
            case BinaryOperator.Subtract:
            {
                if (Is(left, ExpressionValueType.Date) && Is(right, ExpressionValueType.Date))
                {
                    // Date − Date → Number (днів); Date + Date не має сенсу.
                    if (node.Operator == BinaryOperator.Add)
                    {
                        Report(diagnostics, node, "Дати не додаються; різниця дат дає число днів.");
                    }

                    return ExpressionValueType.Number;
                }

                if (Is(left, ExpressionValueType.Date) && Is(right, ExpressionValueType.Number))
                {
                    return ExpressionValueType.Date;
                }

                RequireNumeric(left, node, diagnostics);
                RequireNumeric(right, node, diagnostics);
                return ExpressionValueType.Number;
            }

            case BinaryOperator.Multiply:
            case BinaryOperator.Divide:
            case BinaryOperator.Modulo:
            case BinaryOperator.Power:
                RequireNumeric(left, node, diagnostics);
                RequireNumeric(right, node, diagnostics);
                return ExpressionValueType.Number;

            case BinaryOperator.And:
            case BinaryOperator.Or:
                Require(left, ExpressionValueType.Boolean, node, diagnostics, "Логічна операція потребує булевих операндів.");
                Require(right, ExpressionValueType.Boolean, node, diagnostics, "Логічна операція потребує булевих операндів.");
                return ExpressionValueType.Boolean;

            default:
            {
                // Порівняння різних типів — помилка ПУБЛІКАЦІЇ. Excel тут
                // вгадав би, порівнявши число з текстом за кодами символів, і
                // дав би відповідь, яка виглядає осмисленою.
                if (!Comparable(left, right))
                {
                    Report(diagnostics, node,
                        $"Порівняння несумісних типів: {left} і {right}.");
                }

                return ExpressionValueType.Boolean;
            }
        }
    }

    private ExpressionValueType CheckConditional(
        ConditionalNode node, ITypeContext context, List<ExpressionDiagnostic> diagnostics)
    {
        var condition = Check(node.Condition, context, diagnostics);
        Require(condition, ExpressionValueType.Boolean, node, diagnostics, "Умова має бути булевою.");

        var whenTrue = Check(node.WhenTrue, context, diagnostics);
        var whenFalse = Check(node.WhenFalse, context, diagnostics);

        return whenTrue == ExpressionValueType.Null ? whenFalse : whenTrue;
    }

    private ExpressionValueType CheckFunction(
        FunctionNode node, ITypeContext context, List<ExpressionDiagnostic> diagnostics)
    {
        var argumentTypes = node.Arguments.Select(a => Check(a, context, diagnostics)).ToList();

        switch (node.Name.ToUpperInvariant())
        {
            case "IF" when argumentTypes.Count == 3:
                // Умова IF має бути Boolean — це помилка ПУБЛІКАЦІЇ, а не
                // рантайму: інакше «IF(значення; …)» тихо йшов би однією гілкою.
                Require(argumentTypes[0], ExpressionValueType.Boolean, node, diagnostics,
                    "Перший аргумент IF має бути умовою.");
                return argumentTypes[1] == ExpressionValueType.Null ? argumentTypes[2] : argumentTypes[1];

            case "IFERROR" when argumentTypes.Count == 2:
                return argumentTypes[0] == ExpressionValueType.Null ? argumentTypes[1] : argumentTypes[0];

            case "SUM" or "AVERAGE" or "MIN" or "MAX" or "PRODUCT" or "ROUND" or "ABS" or "SUMIF":
                foreach (var type in argumentTypes)
                {
                    RequireNumeric(type, node, diagnostics);
                }

                return ExpressionValueType.Number;

            default:
                return Functions.GetSignature(node.Name)?.ResultType ?? ExpressionValueType.Null;
        }
    }

    /// <summary>Чи можна порівнювати ці типи.</summary>
    private static bool Comparable(ExpressionValueType left, ExpressionValueType right)
        => left == ExpressionValueType.Null
           || right == ExpressionValueType.Null
           || left == right;

    private static bool Is(ExpressionValueType actual, ExpressionValueType expected)
        => actual == expected;

    private static void RequireNumeric(
        ExpressionValueType actual, AstNode node, List<ExpressionDiagnostic> diagnostics)
        => Require(actual, ExpressionValueType.Number, node, diagnostics,
            $"У арифметиці очікувалося число, а тип операнда — {actual}.");

    private static void Require(
        ExpressionValueType actual,
        ExpressionValueType expected,
        AstNode node,
        List<ExpressionDiagnostic> diagnostics,
        string message)
    {
        // Null сумісний з будь-чим: порожня комірка не робить формулу
        // неправильною, вона робить результат порожнім (02b §6.2).
        if (actual == expected || actual == ExpressionValueType.Null)
        {
            return;
        }

        Report(diagnostics, node, message);
    }

    private static void Report(List<ExpressionDiagnostic> diagnostics, AstNode node, string message)
        => diagnostics.Add(new ExpressionDiagnostic(
            ExpressionErrors.Unresolved, message, node.Position, 1));
}

/// <summary>Джерело типів для посилань.</summary>
public interface ITypeContext
{
    /// <summary>Тип значення посилання з дерева виразу.</summary>
    /// <remarks>
    /// Приймає вузол цілком із тієї самої причини, що й
    /// <c>IEvaluationContext.Read</c>: у дереві посилання записане кодами, а
    /// резолвінг кодів потребує знімка метаданих.
    /// </remarks>
    public ExpressionValueType GetReferenceType(CellReferenceNode reference);

    /// <summary>Тип значення колонки.</summary>
    public ExpressionValueType GetColumnType(int tableDefId, int columnDefId);

    /// <summary>Тип аргументу методології.</summary>
    public ExpressionValueType GetArgumentType(string name);

    /// <summary>Тип значення константи методології (<c>CST.&lt;код&gt;</c>).</summary>
    /// <param name="code">Код константи — те, що стоїть після <c>CST.</c>.</param>
    /// <returns>Тип константи; <c>Null</c> — контекст про неї не знає.</returns>
    /// <remarks>
    /// ⛔ Реалізація за замовчуванням віддає <c>Null</c>, а не <c>Number</c>, і
    /// різниця не стилістична. <c>Null</c> сумісний з усім (§6.2), тобто
    /// контекст, який про константи нічого не знає, нічого про них і не
    /// стверджує. <c>Number</c> — це твердження, і саме воно перетворювало
    /// <c>CST.k1_CategorySelection_ = 'Summer'</c> на «порівняння різних
    /// типів»: ~90 хибних помилок публікації на законних текстових
    /// константах корпусу.
    ///
    /// ⚠ Ціна замовчування названа: контекст без реалізації не спіймає й
    /// СПРАВЖНЬОЇ несумісності — текстової константи в множенні. Її сьогодні
    /// ловить окрема перевірка публікації методології
    /// (<c>MethodologyPublishChecks</c>, позиційний обхід), і доки контекст
    /// методологій не реалізує цей метод, вона там і лишається єдиною.
    /// </remarks>
    public ExpressionValueType GetConstantType(string code) => ExpressionValueType.Null;
}
