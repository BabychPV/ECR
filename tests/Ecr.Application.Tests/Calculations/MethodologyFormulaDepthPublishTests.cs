using Ecr.Application.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// RC5: публікація методології відхиляє формулу, глибшу за межу обчислення, пунктом
/// <c>publish.problem.formulaTooDeep</c>, а довгий плаский ланцюг пропускає.
/// </summary>
[Trait(TestCategories.Stage, TestCategories.Stage4)]
public sealed class MethodologyFormulaDepthPublishTests
{
    private static LiteralNode One() => new(1m, ExpressionValueType.Number);

    private static ParsedFormula Formula(AstNode root) => new("Deep", FormulaResultType.Number, root);

    /// <summary>Унарні мінуси: глибина = кількість + 1.</summary>
    private static AstNode Negations(int count)
    {
        AstNode root = One();
        for (var i = 0; i < count; i++)
        {
            root = new UnaryNode(UnaryOperator.Negate, root);
        }

        return root;
    }

    [Fact]
    public void Формула_глибша_за_96_дає_пункт_формула_надто_складна_з_числами()
    {
        var problem = Assert.Single(MethodologyPublishChecks.Check([Formula(Negations(100))], [], []));

        Assert.Equal("publish.problem.formulaTooDeep", problem.MessageKey);
        Assert.Equal("Deep", problem.Args!["formula"]);
        Assert.Equal("101", problem.Args["depth"]);
        Assert.Equal("96", problem.Args["max"]);
        Assert.Contains("розбийте", problem.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Межа_точна_95_заперечень_проходять_а_96_ні()
    {
        Assert.Empty(MethodologyPublishChecks.Check([Formula(Negations(95))], [], []));
        Assert.Single(MethodologyPublishChecks.Check([Formula(Negations(96))], [], []));
    }

    [Fact]
    public void Плаский_ланцюг_у_тисячу_доданків_проходить()
    {
        AstNode root = One();
        for (var i = 1; i < 1000; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, root, One());
        }

        Assert.Empty(MethodologyPublishChecks.Check([Formula(root)], [], []));
    }
}
