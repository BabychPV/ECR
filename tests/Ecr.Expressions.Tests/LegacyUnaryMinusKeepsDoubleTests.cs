using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Унарний мінус у <c>Legacy</c> не звужує <see cref="double"/> до
/// <see cref="decimal"/> посеред обчислення (аудит A4).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>Evaluator.Unary</c> брав <c>operand.AsNumber()</c> — тобто
/// <c>(decimal)double</c>, що округлює до 15 значущих цифр, — і повертав
/// <c>Number(decimal)</c>. Контракт (<c>02b</c> §5: <c>Legacy</c> — подвійна
/// точність, семантика NCalc 1.3.8) і власна примітка
/// <see cref="ExpressionValue.AsNumber"/> забороняють звуження всередині
/// обчислення. NCalc 1.3.8 рахує заперечення як <c>0 - x</c> у типі операнда,
/// тобто в <c>double</c>.
/// </remarks>
public sealed class LegacyUnaryMinusKeepsDoubleTests
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
    public void Legacy_мінус_третини_помножений_на_три_дає_рівно_мінус_один()
    {
        // ⛔ Сценарій аудиту. У `double` −(1/3)·3 = −1 точно; звуження до 15
        // знаків давало −0.333333333333333·3 = −0.999999999999999 — інше число
        // в останніх знаках, тобто саме те, що `ER-C-11` забороняє.
        var value = Eval("-(1/3)*3", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.True(value.IsDouble);
        Assert.Equal(-1d, value.AsDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Legacy_мінус_нескінченності_це_мінус_нескінченність_а_не_VALUE()
    {
        // `1/0` у Legacy — `+∞` як ЗНАЧЕННЯ (виміряно на NCalc 1.3.8), і його
        // заперечення — `−∞`, яке той, хто зберігає, маскує в нуль із причиною.
        // `#VALUE` тут — інша комірка у звіті, ніж у чинній системі.
        var value = Eval("-(1/0)", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.True(double.IsNegativeInfinity(value.AsDouble()!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Legacy_мінус_числа_поза_межею_decimal_лишається_числом()
    {
        // `Pow(10, 30)` — скінченний `double` (як у NCalc), і його заперечення
        // теж: −1e30, а не `#VALUE`.
        var value = Eval("-Pow(10, 30)", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.Equal(-1e30, value.AsDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_унарний_плюс_не_відкидає_нескінченність()
    {
        // ⚠ Та сама вада на сусідньому операторі: `+` перевіряв «чи число»
        // через `AsNumber()`, а той для `±∞` і для |x| > 7.9e28 дає `null`.
        Assert.True(double.IsPositiveInfinity(Eval("+(1/0)", new LegacyDoubleArithmetic()).AsDouble()!.Value));
        Assert.Equal(1e30, Eval("+Pow(10, 30)", new LegacyDoubleArithmetic()).AsDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Strict_мінус_лишається_decimal()
    {
        // Контроль другого боку: `decimal`-значення заперечується в `decimal`.
        var value = Eval("-(1/3)", new StrictDecimalArithmetic());

        Assert.False(value.IsDouble);
        Assert.Equal(-(1m / 3m), value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Мінус_над_нечислом_лишається_VALUE()
    {
        Assert.Equal(ExpressionErrors.BadValue, Eval("-'x'", new LegacyDoubleArithmetic()).ErrorCode);
        Assert.Equal(ExpressionErrors.BadValue, Eval("+'x'", new StrictDecimalArithmetic()).ErrorCode);
    }
}
