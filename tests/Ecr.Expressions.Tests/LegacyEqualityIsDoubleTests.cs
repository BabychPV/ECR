using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Рівність і <c>in</c> у <c>Legacy</c> порівнюють числа в <see cref="double"/>,
/// як NCalc 1.3.8 (аудит A5).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>Evaluator.AreEqual</c> і <c>MethodologyFunctions.In</c>
/// зводили обидва боки через <c>AsNumber()</c>, тобто в <see cref="decimal"/>
/// з округленням <c>double</c> до 15 значущих цифр. Контракт (<c>02b</c> §5)
/// каже: <c>Legacy</c> — подвійна точність, семантика NCalc 1.3.8. А NCalc
/// рівність і <c>in</c> рахує через <c>CompareUsingMostPreciseType</c>: для
/// параметрів (вони <c>double</c> — <c>Utilities.cs:188-215</c>) і дробових
/// літералів це порівняння <c>double</c>.
///
/// ⚠ Наслідок видно не в числі, а в ГІЛЦІ: <c>if(… = …)</c> обирав іншу
/// гілку, ніж чинна система, — і все, що нижче, рахувалося вже не так.
/// </remarks>
public sealed class LegacyEqualityIsDoubleTests
{
    /// <summary>1 + 10⁻¹⁶: у <c>decimal(34,16)</c> подається, у <c>double</c> — це рівно 1.</summary>
    private const decimal OneAndAHair = 1.0000000000000001m;

    private static ExpressionValue Eval(
        string expression, IEvaluationArithmetic arithmetic, Action<TestEvaluationContext>? arrange = null)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var context = new TestEvaluationContext();
        arrange?.Invoke(context);

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
    public void Legacy_нуль_один_плюс_нуль_два_НЕ_дорівнює_нуль_три()
    {
        // ⛔ Сценарій аудиту. У `double` 0.1 + 0.2 = 0.30000000000000004 ≠ 0.3 —
        // так рахує NCalc 1.3.8. Звуження до 15 знаків давало 0.3 = 0.3 → TRUE.
        var legacy = new LegacyDoubleArithmetic();

        Assert.False(Bool(Eval("0.1 + 0.2 = 0.3", legacy)));
        Assert.True(Bool(Eval("0.1 + 0.2 <> 0.3", legacy)));
        Assert.Equal(2d, Eval("if(0.1 + 0.2 = 0.3, 1, 2)", legacy).AsDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Legacy_in_порівнює_в_double()
    {
        var legacy = new LegacyDoubleArithmetic();

        Assert.False(Bool(Eval("in(0.1 + 0.2, 0.3, 0.5)", legacy)));
        Assert.True(Bool(Eval("in(0.1 + 0.2, 0.3, 0.30000000000000004)", legacy)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Legacy_аргумент_що_відрізняється_за_межею_double_рівний_як_у_NCalc()
    {
        // ⚠ Другий бік тієї самої вади. Чинна система подає аргумент параметром
        // `double` (`double.TryParse` збереженого значення), і 1.0000000000000001
        // там — рівно 1. Порівняння в `decimal` бачило різницю, якої NCalc не
        // бачить ніколи.
        var legacy = new LegacyDoubleArithmetic();
        void Arrange(TestEvaluationContext c) => c.Arguments["A"] = ExpressionValue.Number(OneAndAHair);

        Assert.True(Bool(Eval("@A = 1", legacy, Arrange)));
        Assert.False(Bool(Eval("@A <> 1", legacy, Arrange)));
        Assert.True(Bool(Eval("in(@A, 2, 1)", legacy, Arrange)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_нескінченність_і_NaN_у_рівності_як_раніше()
    {
        // Контроль: `Comparer.Default` NCalc вважає NaN рівним NaN і ∞ рівним ∞,
        // і так само рахувало й звуження (через `Equals`). Поведінка не змінилась.
        var legacy = new LegacyDoubleArithmetic();

        Assert.True(Bool(Eval("1/0 = 1/0", legacy)));
        Assert.True(Bool(Eval("0/0 = 0/0", legacy)));
        Assert.False(Bool(Eval("0/0 = 1", legacy)));
        Assert.False(Bool(Eval("1/0 = 1", legacy)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Strict_рівність_і_in_лишаються_decimal()
    {
        // Контроль другого режиму: `Strict` — наскрізний `decimal` (`02b` §5).
        var strict = new StrictDecimalArithmetic();
        void Arrange(TestEvaluationContext c) => c.Arguments["A"] = ExpressionValue.Number(OneAndAHair);

        Assert.True(Bool(Eval("0.1 + 0.2 = 0.3", strict)));
        Assert.True(Bool(Eval("in(0.1 + 0.2, 0.3)", strict)));
        Assert.False(Bool(Eval("@A = 1", strict, Arrange)));
        Assert.False(Bool(Eval("in(@A, 1)", strict, Arrange)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_тексту_і_різних_типів_не_змінилась()
    {
        var legacy = new LegacyDoubleArithmetic();

        Assert.True(Bool(Eval("'No - Нет' = 'No - Нет'", legacy)));
        Assert.False(Bool(Eval("'a' = 'A'", legacy)));
        Assert.True(Bool(Eval("in('b', 'a', 'b')", legacy)));
    }
}
