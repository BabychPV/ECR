using Ecr.Application.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Ланцюги `a+a+…` на тисячу доданків не повинні давати експоненту в класифікації
/// «текст/число» (<c>ProducesTextOnly</c> ↔ <c>ProducesNumber</c> через <c>IsTextPlus</c>):
/// кожен вузол класифікується один раз. Час обмежено 10 с — експонента не вкладається.
/// </summary>
[Trait(TestCategories.Stage, TestCategories.Stage4)]
public sealed class MethodologyPublishChainPerformanceTests
{
    private const int Links = 1000;

    private static LiteralNode Num() => new(1m, ExpressionValueType.Number);

    private static LiteralNode Txt() => new("a", ExpressionValueType.Text);

    private static SymbolReferenceNode Unknown() => new(SymbolKind.Constant, "A");

    private static AstNode Chain(Func<AstNode> first, Func<AstNode> next)
    {
        var root = first();
        for (var i = 1; i < Links; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, root, next());
        }

        return root;
    }

    private static IReadOnlyList<PublishProblem> CheckWithin10Seconds(FormulaResultType type, AstNode root)
    {
        IReadOnlyList<PublishProblem>? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = MethodologyPublishChecks.Check([new ParsedFormula("Chain", type, root)], [], []);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Перевірка ланцюга не завершилась за 10 с (експонента).");
        Assert.Null(error);
        return result!;
    }

    [Fact]
    public void Текстовий_ланцюг_тисяча_доданків_класифікується_швидко_як_текст()
    {
        var problems = CheckWithin10Seconds(FormulaResultType.Number, Chain(Txt, Txt));

        Assert.Contains(problems, p => p.MessageKey == "publish.problem.numberReturnsText");
    }

    [Fact]
    public void Числовий_ланцюг_тисяча_доданків_класифікується_швидко_як_число()
    {
        var problems = CheckWithin10Seconds(FormulaResultType.Text, Chain(Num, Num));

        Assert.Contains(problems, p => p.MessageKey == "publish.problem.textReturnsNumber");
    }

    [Fact]
    public void Змішаний_ланцюг_число_плюс_текст_завершується_швидко()
    {
        var problems = CheckWithin10Seconds(FormulaResultType.Text, Chain(Num, Txt));

        Assert.DoesNotContain(problems, p => p.MessageKey == "publish.problem.numberReturnsText");
    }

    [Fact]
    public void Ланцюг_з_невідомих_операндів_завершується_швидко()
    {
        var problems = CheckWithin10Seconds(FormulaResultType.Number, Chain(Unknown, Unknown));

        Assert.DoesNotContain(problems, p => p.MessageKey == "publish.problem.numberReturnsText");
    }
}
