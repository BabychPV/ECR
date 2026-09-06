using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Оператори рахує <see cref="IEvaluationArithmetic"/>, а не сам обчислювач
/// (<c>I.7</c>, <c>H-24d-1</c>).
/// </summary>
/// <remarks>
/// ⛔ Тут була третя копія того самого класу дефекту.
/// <c>LegacyDoubleArithmetic.Binary</c> написаний на кроці <c>I.4</c>,
/// покритий тестами і **не потрапляв на шлях операторів узагалі**: метод
/// <c>Evaluator.Arithmetic</c> був <c>static</c> і рахував у <c>decimal</c>
/// незалежно від режиму. Арифметику кликали лише функції (<c>I.14</c>).
///
/// ⚠ Наслідок тихий, як завжди: у <c>Legacy</c> вираз <c>1/0</c> давав
/// <c>#DIV/0</c>, тоді як чинний рушій дає <c>+∞</c> — і саме це <c>+∞</c> він
/// далі маскує в нуль (<c>Utilities.cs:56-71</c>). Режим, який існує заради
/// відтворення чисел, на найпростішому діленні відтворював інше.
/// </remarks>
public sealed class OperatorArithmeticRoutingTests
{
    private static ExpressionValue Eval(string expression, IEvaluationArithmetic arithmetic)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, new TestEvaluationContext(), ExpressionDialect.Methodology);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Ділення_на_нуль_у_Legacy_дає_нескінченність_а_не_помилку()
    {
        // ⛔ Головне твердження. Виміряно на справжньому NCalc 1.3.8:
        // `1/0 → ∞`, а не `DivideByZeroException`. Той `catch` у чинному коді
        // для ділення на нуль не спрацьовує ніколи.
        var value = Eval("1/0", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.True(double.IsPositiveInfinity(value.AsDouble()!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Ділення_на_нуль_у_Strict_лишається_помилкою()
    {
        // ⚠ Другий бік: у `decimal` нескінченності не існує, і мовчазний нуль
        // тут був би невідрізненний від справжнього. Режими різняться, і
        // різниця має бути видима саме тут.
        var value = Eval("1/0", new StrictDecimalArithmetic());

        Assert.True(value.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Нуль_поділити_на_нуль_у_Legacy_дає_NaN()
    {
        // ⚠ Друга причина маскування, і вона інша за природою: `∞` — це
        // «завелике», `NaN` — «невизначене». У звіті вони мають стояти
        // окремими рядками, бо й лікуються по-різному.
        var value = Eval("0/0", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.True(double.IsNaN(value.AsDouble()!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Звичайне_ділення_у_Legacy_не_цілочисельне()
    {
        // ⛔ Замір, який скасував пастку 1 директиви №05: `365/31` дає
        // 11.774…, а не 11. Тест стоїть тут, а не лише в стенді, бо саме
        // цей шлях — оператор через арифметику — і рахує число.
        var value = Eval("365/31", new LegacyDoubleArithmetic());

        Assert.Equal(11.774193548387096d, value.AsDouble()!.Value, 12);
    }
}
