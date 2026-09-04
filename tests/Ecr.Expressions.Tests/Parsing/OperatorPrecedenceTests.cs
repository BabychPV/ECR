using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>Пріоритети операторів за таблицею 02b §2.</summary>
public sealed class OperatorPrecedenceTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("-2 ^ 2", -4)]          // унарний мінус слабший за степінь
    [InlineData("2 * 3 % 4", 2)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Арифметика_обчислюється_за_пріоритетами(string expr, int expected)
        => Assert.Equal(expected, Expr.Number(expr));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Степінь_правоасоціативний_2_у_3_у_2_дорівнює_512()
    {
        // Ліва асоціативність дала б (2^3)^2 = 64 — теж «правдоподібне» число,
        // і саме тому помилку тут неможливо помітити на очі.
        Assert.Equal(512m, Expr.Number("2 ^ 3 ^ 2"));
        Assert.NotEqual(64m, Expr.Number("2 ^ 3 ^ 2"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкатенація_слабша_за_додавання()
    {
        // Якби `&` був сильнішим, вийшло б 1 & 2 = '12', потім '12' + 3.
        var value = Expr.Eval("1 + 2 & 'x'");

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal("3x", value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_слабше_за_конкатенацію()
    {
        var value = Expr.Eval("'a' & 'b' = 'ab'");

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.True((bool)value.Value!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AND_сильніший_за_OR()
    {
        // TRUE OR (FALSE AND FALSE) = TRUE.
        // Якби OR був сильнішим: (TRUE OR FALSE) AND FALSE = FALSE.
        var value = Expr.Eval("TRUE OR FALSE AND FALSE");

        Assert.True((bool)value.Value!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Одинарне_і_подвійне_дорівнює_це_синоніми_порівняння()
    {
        // Присвоєння в мові немає взагалі, тому двозначності, як у
        // C-подібних мовах, не виникає (02b §2).
        Assert.Equal(Expr.Eval("1 = 1").Value, Expr.Eval("1 == 1").Value);
        Assert.Equal(Expr.Eval("1 = 2").Value, Expr.Eval("1 == 2").Value);

        // Синоніми не «дають однакову відповідь», а розбираються в ОДИН і той
        // самий вузол: інакше десь у рушії лишилася б друга гілка порівняння.
        var single = Assert.IsType<BinaryNode>(
            Expr.Parse("1 = 1", ExpressionDialect.Template).Expression!.Root);
        var doubled = Assert.IsType<BinaryNode>(
            Expr.Parse("1 == 1", ExpressionDialect.Template).Expression!.Root);

        Assert.Equal(BinaryOperator.Equal, single.Operator);
        Assert.Equal(single.Operator, doubled.Operator);
    }
}
