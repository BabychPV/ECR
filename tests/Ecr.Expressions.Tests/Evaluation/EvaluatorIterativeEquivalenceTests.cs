using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// RC5: ітеративне обчислення лівого гребеня дає ті самі значення, помилки й
/// кількість кроків, що й рекурсивне обчислення дерева.
/// </summary>
/// <remarks>
/// ⛔ Еталон — НЕ друга копія правил операторів і не рефлексія на приватні поля.
/// Він сам рекурсивний (лівий → правий, як було до RC5) і лише ЗБИРАЄ значення, а
/// кожну пару операндів віддає справжньому обчислювачу як вузол
/// <c>Binary(op, Argument(l), Argument(r))</c>, де лівий операнд — не бінарний
/// вузол, тобто шлях без гребеня. Отже правила <c>null</c>, #DIV/0, арифметики
/// режиму (Strict/Legacy) беруться з продуктиву, а перевіряється саме те, що
/// змінилося: порядок і поширення першої помилки в циклі.
///
/// ⚠ Єдине, що еталон знає сам, — «лівий операнд-помилка перемагає правий»
/// (правило поширення, 02b §6.4); без нього не можна подати помилку як операнд.
/// </remarks>
public sealed class EvaluatorIterativeEquivalenceTests
{
    private static readonly BinaryOperator[] Operators =
    [
        BinaryOperator.Add, BinaryOperator.Subtract, BinaryOperator.Multiply, BinaryOperator.Divide,
        BinaryOperator.Modulo, BinaryOperator.Concat, BinaryOperator.And, BinaryOperator.Or,
        BinaryOperator.Equal, BinaryOperator.NotEqual, BinaryOperator.Less, BinaryOperator.GreaterOrEqual,
        BinaryOperator.Power,
    ];

    /// <summary>Пул значень: нулі, null, помилки, текст, булеві, дата, довгі дроби.</summary>
    private static readonly ExpressionValue[] Pool =
    [
        ExpressionValue.Number(0m), ExpressionValue.Number(1m), ExpressionValue.Number(2m),
        ExpressionValue.Number(-2.5m), ExpressionValue.Number(7.25m), ExpressionValue.Number(100m),
        ExpressionValue.Number(0.1m), ExpressionValue.Number(3m),
        ExpressionValue.Null, ExpressionValue.Null,
        ExpressionValue.Text("x"), ExpressionValue.Text("12"), ExpressionValue.Text(string.Empty),
        ExpressionValue.Boolean(true), ExpressionValue.Boolean(false),
        ExpressionValue.Date(new DateTime(2026, 3, 1)),
        ExpressionValue.Error(ExpressionErrors.DivideByZero),
        ExpressionValue.Error(ExpressionErrors.BadValue),
    ];

    public static TheoryData<NumericMode> Modes => [NumericMode.Strict, NumericMode.Legacy];

    [Theory]
    [MemberData(nameof(Modes))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ітеративне_обчислення_збігається_з_рекурсивним_на_випадкових_деревах(NumericMode mode)
    {
        var evaluator = new Evaluator(new FunctionRegistry()).WithArithmetic(EvaluationArithmetics.For(mode));
        var random = new Random(20261006);
        var context = NewContext(random);

        var successes = 0;
        for (var round = 0; round < 1500; round++)
        {
            // Довжина гребеня 2–90; праві піддерева (до двох рівнів) і змішані оператори.
            Choose(random, round);
            var terms = random.Next(2, 91);
            var tree = Comb(random, terms, depthLeft: 2);

            var (expectedValue, expectedSteps) = Reference(tree, context, evaluator);

            var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
            var actual = evaluator.Evaluate(tree, context, ExpressionDialect.Template, budget);

            Assert.True(
                expectedValue.Equals(actual),
                $"{mode}, раунд {round}, {terms} операндів: еталон {Show(expectedValue)}, дістали {Show(actual)}.");
            Assert.Equal(expectedSteps, budget.Spent);
            successes += actual.IsError ? 0 : 1;
        }

        // Тест, у якому кожен прогін закінчується помилкою, нічого не доводить.
        Assert.True(successes >= 150, $"{mode}: лише {successes} прогонів із 1500 дали не помилку.");
    }

    [Theory]
    [InlineData(BinaryOperator.Add)]
    [InlineData(BinaryOperator.Subtract)]
    [InlineData(BinaryOperator.Multiply)]
    [InlineData(BinaryOperator.Divide)]
    [InlineData(BinaryOperator.Concat)]
    [InlineData(BinaryOperator.And)]
    [InlineData(BinaryOperator.Or)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Однорідні_гребені_з_помилками_нулями_й_null_збігаються_з_еталоном(BinaryOperator op)
    {
        var evaluator = new Evaluator(new FunctionRegistry());
        var random = new Random((int)op + 7);
        var context = NewContext(random);

        for (var round = 0; round < 300; round++)
        {
            Choose(random, round);
            _leaves = op switch
            {
                BinaryOperator.Concat => [10, 11, 12, 8],
                BinaryOperator.And or BinaryOperator.Or => [13, 14, 8],
                _ => [0, 1, 2, 3, 4, 5, 6, 7, 8],
            };
            var terms = random.Next(2, 91);
            AstNode root = Leaf(random);
            for (var i = 1; i < terms; i++)
            {
                root = new BinaryNode(op, root, Leaf(random));
            }

            var (expectedValue, expectedSteps) = Reference(root, context, evaluator);
            var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
            var actual = evaluator.Evaluate(root, context, ExpressionDialect.Template, budget);

            Assert.True(
                expectedValue.Equals(actual),
                $"{op}, раунд {round}, {terms} операндів: еталон {Show(expectedValue)}, дістали {Show(actual)}.");
            Assert.Equal(expectedSteps, budget.Spent);
        }
    }

    [Theory]
    [InlineData("10 / 2 / 0 + 5", ExpressionErrors.DivideByZero)]
    [InlineData("1 / 0 + 'a' * 2", ExpressionErrors.DivideByZero)]
    [InlineData("'a' * 2 + 1 / 0", ExpressionErrors.BadValue)]
    [InlineData("100 - 1 - 2 - 3", null)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Помилка_поширюється_у_тому_ж_порядку_що_й_у_дереві(string text, string? errorCode)
    {
        var parsed = Expr.Parse(text);
        Assert.True(parsed.IsSuccess, text);

        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(parsed.Expression!.Root, new TestEvaluationContext(), ExpressionDialect.Template);

        if (errorCode is null)
        {
            Assert.Equal(94m, value.AsNumber());
        }
        else
        {
            Assert.Equal(errorCode, value.ErrorCode);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ланцюг_із_тисячі_доданків_рахується_сотні_разів_без_відхилень()
    {
        var parsed = Expr.Parse(string.Join(" + ", Enumerable.Range(1, 1000)));
        Assert.True(parsed.IsSuccess);

        var evaluator = new Evaluator(new FunctionRegistry());
        var context = new TestEvaluationContext();

        for (var i = 0; i < 300; i++)
        {
            var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
            var value = evaluator.Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Template, budget);

            Assert.Equal(500_500m, value.AsNumber());
            Assert.Equal(1999, budget.Spent);
        }
    }

    // ── еталон ──────────────────────────────────────────────────────────

    private static (ExpressionValue Value, int Steps) Reference(
        AstNode node, TestEvaluationContext context, Evaluator evaluator)
    {
        switch (node)
        {
            case SymbolReferenceNode symbol:
                return (context.GetArgument(symbol.Name), 1);

            case BinaryNode binary:
                var (left, leftSteps) = Reference(binary.Left, context, evaluator);
                var (right, rightSteps) = Reference(binary.Right, context, evaluator);
                return (Combine(binary.Operator, left, right, context, evaluator), 1 + leftSteps + rightSteps);

            default:
                throw new InvalidOperationException(node.GetType().Name);
        }
    }

    private static ExpressionValue Combine(
        BinaryOperator op, ExpressionValue left, ExpressionValue right,
        TestEvaluationContext context, Evaluator evaluator)
    {
        if (left.IsError)
        {
            return left;
        }

        if (right.IsError)
        {
            return right;
        }

        context.Arguments["$l"] = left;
        context.Arguments["$r"] = right;
        var pair = new BinaryNode(
            op,
            new SymbolReferenceNode(SymbolKind.Argument, "$l"),
            new SymbolReferenceNode(SymbolKind.Argument, "$r"));

        return evaluator.Evaluate(
            pair, context, ExpressionDialect.Template, new EvaluationBudget(Evaluator.MaxEvaluationSteps));
    }

    // ── генератор ───────────────────────────────────────────────────────

    private static TestEvaluationContext NewContext(Random random)
    {
        var context = new TestEvaluationContext();
        for (var i = 0; i < Pool.Length; i++)
        {
            context.Arguments["a" + i] = Pool[i];
        }

        _ = random;
        return context;
    }

    private static double _errorRate;
    private static int[] _leaves = [];
    private static BinaryOperator[] _ops = Operators;

    /// <summary>
    /// Однорідне сімейство операндів і операторів на раунд: суміш типів давала б
    /// #VALUE майже в кожному прогоні, і тест порівнював би помилку з помилкою.
    /// </summary>
    private static void Choose(Random random, int round)
    {
        _errorRate = new[] { 0d, 0.01, 0.15 }[round % 3];
        switch (round % 4)
        {
            case 0:
                _leaves = [0, 1, 2, 3, 4, 5, 6, 7, 8];
                _ops = [BinaryOperator.Add, BinaryOperator.Subtract, BinaryOperator.Multiply,
                    BinaryOperator.Divide, BinaryOperator.Modulo];
                break;
            case 1:
                _leaves = [10, 11, 12, 8];
                _ops = [BinaryOperator.Concat, BinaryOperator.Equal, BinaryOperator.NotEqual];
                break;
            case 2:
                _leaves = [13, 14, 8];
                _ops = [BinaryOperator.And, BinaryOperator.Or, BinaryOperator.Equal];
                break;
            default:
                _leaves = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 13, 14, 15];
                _ops = Operators;
                break;
        }

        _ = random;
    }

    private static SymbolReferenceNode Leaf(Random random)
        => new(
            SymbolKind.Argument,
            "a" + (random.NextDouble() < _errorRate
                ? random.Next(Pool.Length - 2, Pool.Length)
                : _leaves[random.Next(_leaves.Length)]));
    private static AstNode Comb(Random random, int terms, int depthLeft)
    {
        var root = Operand(random, depthLeft);
        for (var i = 1; i < terms; i++)
        {
            var op = _ops[random.Next(_ops.Length)];
            root = new BinaryNode(op, root, Operand(random, depthLeft));
        }

        return root;
    }

    private static AstNode Operand(Random random, int depthLeft)
        => depthLeft > 0 && random.Next(8) == 0
            ? Comb(random, random.Next(2, 5), depthLeft - 1)
            : Leaf(random);

    private static string Show(ExpressionValue value)
        => value.IsError ? value.ErrorCode! : $"{value.Type}:{value.Value}";
}
