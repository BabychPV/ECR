using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Аудит L7-03: три шляхи рушія, що кидали виняток замість значення-помилки
/// (`02b` §6.4 «помилка є значенням»).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>ROUND([A], [B])</c> з <c>B = 3e9</c> звужував decimal у int
/// ДО перевірки меж → <see cref="OverflowException"/>; <c>Round(@x, 16)</c> у
/// <c>Legacy</c> кликав <c>Math.Round(double, 16)</c> (межа BCL — 15) →
/// <see cref="ArgumentOutOfRangeException"/>; <c>@D + 1e7</c> кликав
/// <c>DateTime.AddDays</c> поза 0001…9999 → той самий виняток. Жоден викликач
/// не ловить — одна комірка валила PATCH (500) або весь прогін.
/// </remarks>
public sealed class ValueErrorInsteadOfExceptionTests
{
    private static ExpressionValue Methodology(
        string expression, IEvaluationArithmetic arithmetic, Action<TestEvaluationContext>? arrange = null)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var context = new TestEvaluationContext();
        arrange?.Invoke(context);

        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Methodology);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(3_000_000_000d)]
    [InlineData(-3_000_000_000d)]
    [InlineData(29d)]
    public void ROUND_шаблону_з_кількістю_знаків_поза_межею_дає_VALUE(double places)
    {
        var value = TemplateFunctions.Round(ExpressionValue.Number(1.5m), ExpressionValue.Number((decimal)places));

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROUND_шаблону_на_межі_28_знаків_рахується()
        => Assert.Equal(1.5m, TemplateFunctions.Round(ExpressionValue.Number(1.5m), ExpressionValue.Number(28m)).AsNumber());

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    [InlineData(16)]
    [InlineData(28)]
    public void Legacy_Round_понад_15_знаків_дає_VALUE_а_не_виняток(int digits)
    {
        var value = Methodology($"Round(@X, {digits})", new LegacyDoubleArithmetic(),
            c => c.Arguments["X"] = ExpressionValue.LegacyNumber(1.25));

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Legacy_Round_на_межі_15_знаків_рахується()
    {
        var value = Methodology("Round(@X, 15)", new LegacyDoubleArithmetic(),
            c => c.Arguments["X"] = ExpressionValue.LegacyNumber(1.25));

        Assert.Equal(1.25, value.AsDouble());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("@D + @N")]
    [InlineData("@D - @N")]
    public void Дата_плюс_десять_мільйонів_днів_дає_VALUE(string expression)
    {
        var value = Methodology(expression, new StrictDecimalArithmetic(), c =>
        {
            c.Arguments["D"] = ExpressionValue.Date(new DateTime(2026, 1, 1));
            c.Arguments["N"] = ExpressionValue.Number(10_000_000m);
        });

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дата_плюс_день_рахується_як_раніше()
    {
        var value = Methodology("@D + 1", new StrictDecimalArithmetic(),
            c => c.Arguments["D"] = ExpressionValue.Date(new DateTime(2026, 1, 31)));

        Assert.Equal(new DateTime(2026, 2, 1), value.Value);
    }
}
