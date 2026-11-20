using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// PSS-31: конкатенація (<c>&amp;</c> і <c>+</c> двох текстів) не будує текст довший за стовпець сховища
/// (<see cref="ColumnDef.MaxStringLength"/>): довший результат — <c>#VALUE</c>.
/// </summary>
/// <remarks>
/// Межа кроків не бачить довжини: подвоєння тексту коштує один крок, а тридцять подвоєнь — гігабайт.
/// </remarks>
public sealed class ConcatenationLengthLimitTests
{
    private static ExpressionValue Concat(string left, string right, BinaryOperator op = BinaryOperator.Concat)
        => new Evaluator(new FunctionRegistry()).Evaluate(
            new BinaryNode(op, new LiteralNode(left, ExpressionValueType.Text), new LiteralNode(right, ExpressionValueType.Text)),
            new TestEvaluationContext(),
            ExpressionDialect.Template);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "PSS-31")]
    [InlineData(BinaryOperator.Concat)]
    [InlineData(BinaryOperator.Add)]
    public void Текст_рівно_на_межі_склеюється_а_на_знак_довший_дає_VALUE(BinaryOperator op)
    {
        Assert.Equal(1_000, Evaluator.MaxConcatenatedTextLength);
        Assert.Equal(ColumnDef.MaxStringLength, Evaluator.MaxConcatenatedTextLength);

        var edge = Concat(new string('a', 600), new string('b', 400), op);
        Assert.Equal(ExpressionValueType.Text, edge.Type);
        Assert.Equal(1_000, ((string)edge.Value!).Length);

        // Мутація: прибрати перевірку довжини в `Concatenate` — текст на 1 001 знак.
        var over = Concat(new string('a', 600), new string('b', 401), op);
        Assert.True(over.IsError);
        Assert.Equal(ExpressionErrors.BadValue, over.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "PSS-31")]
    public void Ланцюг_подвоєнь_зупиняється_помилкою_а_не_росте_експоненційно()
    {
        // `v1 = v0 & v0; v2 = v1 & v1; …` — по одному кроку на оператор, але довжина подвоюється; без межі тридцять
        // операторів дали б 2^31 знаків (OutOfMemory), з межею — #VALUE на дев'ятому. Тест зупиняється на двадцятому,
        // щоб мутація без межі червоніла, а не їла пам'ять.
        var value = ExpressionValue.Text("xy");
        var steps = 0;
        for (; steps < 20 && !value.IsError; steps++)
        {
            var text = (string)value.Value!;
            value = Concat(text, text);
        }

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
        Assert.Equal(9, steps);
    }
}
