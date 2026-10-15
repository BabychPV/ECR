using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// LAND-POST-K3 (2026-10-15): фіксація поточної поведінки - Text x Number = #VALUE в ОБОХ режимах арифметики, а
/// рівність Text з Number - FALSE (`'No - Нет' = 0`). Продукт не змінюється: тест лише ловить неявне «виправлення».
/// </summary>
/// <remarks>
/// Мутаційний доказ: коерсувати Text у число в <c>Evaluator</c>/арифметиці (напр. <c>AsNumber()</c> для Text) - Theory
/// червоніє. Різниця режимів для числового рядка живе вище (ToValue у Legacy), див. <c>LegacyNumericTextArgumentTests</c>.
/// </remarks>
public sealed class TextTimesNumberIsValueErrorTests
{
    private static ExpressionValue Eval(string expression, IEvaluationArithmetic arithmetic, string text)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var context = new TestEvaluationContext();
        context.Arguments["OT"] = ExpressionValue.Text(text);

        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Methodology);
    }

    public static TheoryData<bool> Modes => new() { true, false };

    private static IEvaluationArithmetic Arithmetic(bool strict) =>
        strict ? new StrictDecimalArithmetic() : new LegacyDoubleArithmetic();

    [Theory]
    [MemberData(nameof(Modes))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Text_на_Number_дає_VALUE_у_Strict_і_Legacy(bool strict)
    {
        foreach (var text in new[] { "No - Нет", "740.900000" })
        {
            var value = Eval("@OT * 2", Arithmetic(strict), text);

            Assert.True(value.IsError, $"'{text}' * 2 ({(strict ? "Strict" : "Legacy")}) не дав помилку");
            Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_Text_і_Number_FALSE_у_Strict_і_Legacy(bool strict)
    {
        var value = Eval("@OT = 0", Arithmetic(strict), "No - Нет");

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.False((bool)value.Value!);

        // літерал замість аргументу - так само
        var literal = Eval("'No - Нет' = 0", Arithmetic(strict), "x");
        Assert.Equal(ExpressionValueType.Boolean, literal.Type);
        Assert.False((bool)literal.Value!);
    }
}