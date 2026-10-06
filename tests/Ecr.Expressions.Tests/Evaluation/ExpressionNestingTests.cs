using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// <see cref="ExpressionNesting.Measure"/> міряє рівно те, що міряє обчислювач.
/// </summary>
/// <remarks>
/// ⛔ Дві різні відповіді на питання «чи надто глибока формула» — те, заради
/// чого цей файл: публікація пускає формулу, яку обчислювач відхиляє, або
/// навпаки. Тому число звіряється з <see cref="EvaluationBudget.Deepest"/> —
/// лічильником, що стоїть у продуктиві, — на кожному виразі обох фікстур корпусу.
/// </remarks>
public sealed class ExpressionNestingTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Глибина_збігається_з_лічильником_обчислювача_на_всьому_корпусі()
    {
        var corpus = EvaluatorDepthGuardTests.CorpusExpressions();
        Assert.NotEmpty(corpus);

        var compared = 0;
        foreach (var (text, dialect) in corpus)
        {
            var parsed = Expr.Parse(text, dialect);
            Assert.True(parsed.IsSuccess, text);
            var root = parsed.Expression!.Root;

            var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
            _ = new Evaluator(new FunctionRegistry())
                .Evaluate(root, new TestEvaluationContext(), dialect, budget);

            var measured = ExpressionNesting.Measure(root);

            // Верхня межа, а не точний збіг, там, де обчислювач рахує менше за дерево:
            // ліниві гілки (IF, IFS, IFERROR, тернарник) він може не відвідати, а посилання на
            // комірку-аргумент читає групою без входу в рівень. Занижувати міру не можна, завищувати
            // на одиницю — можна (до межі 96 парсер за текстом не доходить); в усіх інших випадках — збіг.
            var lazy = text.Contains("IF", StringComparison.OrdinalIgnoreCase) || text.Contains('?') || text.Contains('[');
            if (lazy)
            {
                Assert.True(measured >= budget.Deepest, $"«{text}»: міра {measured} < обчислена {budget.Deepest}.");
            }
            else
            {
                Assert.True(measured == budget.Deepest, $"«{text}»: міра {measured}, обчислювач {budget.Deepest}.");
                compared++;
            }
        }

        Assert.True(compared > 20, $"Точно звірено лише {compared} виразів.");
    }

    [Theory]
    [InlineData("1 + 1 + 1 + 1 + 1", 2)]
    [InlineData("1 + 2 - 3 * 4 / 5", 3)]
    [InlineData("1 + (1 + (1 + 1))", 4)]
    [InlineData("SUM(1, SUM(2, SUM(3)))", 4)]
    [InlineData("-(-(-1))", 4)]
    public void Міра_на_відомих_формах(string text, int expected)
    {
        var parsed = Expr.Parse(text);
        Assert.True(parsed.IsSuccess, text);

        Assert.Equal(expected, ExpressionNesting.Measure(parsed.Expression!.Root));
    }

    [Fact]
    public void Плаский_ланцюг_у_сто_тисяч_вузлів_міряється_без_рекурсії()
    {
        AstNode root = new LiteralNode(1m, ExpressionValueType.Number);
        for (var i = 1; i < 100_000; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, root, new LiteralNode(1m, ExpressionValueType.Number));
        }

        // Той самий потік із малим стеком, що й у сторожі обчислення: рекурсивна
        // міра тут упала б, а ця — ні.
        var depth = 0;
        var thread = new Thread(() => depth = ExpressionNesting.Measure(root), maxStackSize: 256 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(2, depth);
    }

    [Fact]
    public void Праве_вкладення_має_глибину_за_кількістю_вузлів()
    {
        AstNode root = new LiteralNode(1m, ExpressionValueType.Number);
        for (var i = 0; i < 200; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, new LiteralNode(1m, ExpressionValueType.Number), root);
        }

        Assert.Equal(201, ExpressionNesting.Measure(root));
    }
}
