using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// У <c>Strict</c> усі оператори порівняння — в одній числовій семантиці,
/// <see cref="decimal"/> (аудит A6).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>Evaluator.Compare</c> впорядковував числа через
/// <c>AsDouble()</c> у будь-якому режимі, а рівність рахувалась у
/// <see cref="decimal"/>. Для <c>@A = 1.0000000000000001</c>, <c>@B = 1</c>
/// це давало одночасно <c>@A &gt;= @B</c>, <c>@A &lt;= @B</c> і <c>@A &lt;&gt; @B</c>
/// при <c>NOT @A &gt; @B</c>: оператори суперечили один одному. Контракт
/// (<c>02b</c> §5): <c>Strict</c> — наскрізний <c>decimal</c>.
/// </remarks>
public sealed class StrictComparisonIsDecimalTests
{
    private static ExpressionValue Eval(string expression, IEvaluationArithmetic arithmetic, decimal a, decimal b)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var context = new TestEvaluationContext();
        context.Arguments["A"] = ExpressionValue.Number(a);
        context.Arguments["B"] = ExpressionValue.Number(b);

        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Methodology);
    }

    private static bool Bool(ExpressionValue value)
    {
        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        return (bool)value.Value!;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Strict_число_більше_за_межею_double_більше_а_не_рівне()
    {
        // ⛔ Сценарій аудиту: у `double` обидва — рівно 1, у `decimal` — ні.
        var strict = new StrictDecimalArithmetic();
        const decimal a = 1.0000000000000001m;

        Assert.True(Bool(Eval("@A > @B", strict, a, 1m)));
        Assert.True(Bool(Eval("@A >= @B", strict, a, 1m)));
        Assert.False(Bool(Eval("@A <= @B", strict, a, 1m)));
        Assert.False(Bool(Eval("@A < @B", strict, a, 1m)));
        Assert.False(Bool(Eval("@A = @B", strict, a, 1m)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    [InlineData("1.0000000000000001", "1")]
    [InlineData("123456.0000000000001", "123456")] // приклад аудиту: поріг CST.Limit
    [InlineData("0.30000000000000001", "0.3")]
    [InlineData("-5.00000000000000001", "-5")]
    [InlineData("7", "7.000")] // рівні з різним масштабом
    [InlineData("2.5", "10")]
    public void Strict_оператори_порівняння_не_суперечать_один_одному(string left, string right)
    {
        // ⚠ Трихотомія: рівно одне з `<`, `=`, `>` істинне, а `<=`, `>=`, `<>`
        // узгоджені з ними. Пара на межі точності `double` — саме те місце, де
        // дві числові семантики в одному режимі розходились.
        var strict = new StrictDecimalArithmetic();
        var a = decimal.Parse(left, System.Globalization.CultureInfo.InvariantCulture);
        var b = decimal.Parse(right, System.Globalization.CultureInfo.InvariantCulture);

        var less = Bool(Eval("@A < @B", strict, a, b));
        var equal = Bool(Eval("@A = @B", strict, a, b));
        var greater = Bool(Eval("@A > @B", strict, a, b));

        Assert.Equal(1, (less ? 1 : 0) + (equal ? 1 : 0) + (greater ? 1 : 0));
        Assert.Equal(less || equal, Bool(Eval("@A <= @B", strict, a, b)));
        Assert.Equal(greater || equal, Bool(Eval("@A >= @B", strict, a, b)));
        Assert.Equal(!equal, Bool(Eval("@A <> @B", strict, a, b)));

        // І відповідь та сама, що в `decimal.CompareTo` — а не лише узгоджена.
        Assert.Equal(a.CompareTo(b) < 0, less);
        Assert.Equal(a.CompareTo(b) > 0, greater);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_впорядкування_лишається_double_і_теж_узгоджене()
    {
        // Контроль другого режиму: у `Legacy` усі порівняння — `double`, як у
        // NCalc 1.3.8 (після A5 — і рівність теж), тож та сама пара там рівна.
        var legacy = new LegacyDoubleArithmetic();
        const decimal a = 1.0000000000000001m;

        Assert.False(Bool(Eval("@A > @B", legacy, a, 1m)));
        Assert.True(Bool(Eval("@A = @B", legacy, a, 1m)));
        Assert.True(Bool(Eval("@A >= @B", legacy, a, 1m)));
    }
}
