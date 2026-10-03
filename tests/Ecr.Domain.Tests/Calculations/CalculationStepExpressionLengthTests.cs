// tests/Ecr.Domain.Tests/Calculations/CalculationStepExpressionLengthTests.cs
using Ecr.Domain.Entities.Calculations;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Вираз кроку трейсу не довший за колонку <c>calc.CalculationStep.Expression</c>
/// (аудит 2026-10-03, L10-01).
/// </summary>
/// <remarks>
/// ⛔ Формула методології може мати до 4000 символів (D256), а колонка кроку —
/// 2000. Без обрізання формула Thermaloxidizer (2409 симв.) з <c>#DIV/0</c>
/// валила <c>SaveChanges</c> і весь прогін ставав Failed.
/// Мутаційний доказ: у <c>CalculationStep.Describe</c> повернути
/// <c>Expression = expression;</c> — червоніє
/// <see cref="Довгий_вираз_обрізається_до_стелі_колонки_з_трикрапкою"/>.
/// </remarks>
public sealed class CalculationStepExpressionLengthTests
{
    [Fact]
    public void Довгий_вираз_обрізається_до_стелі_колонки_з_трикрапкою()
    {
        var expression = new string('a', 2500);
        var step = new CalculationStep(1, 202601, 1, "F1");

        step.Describe(expression, null, "#DIV/0", null);

        Assert.Equal(CalculationStep.MaxStepExpressionLength, step.Expression!.Length);
        Assert.Equal(2000, CalculationStep.MaxStepExpressionLength);
        Assert.EndsWith("…", step.Expression, StringComparison.Ordinal);
        Assert.StartsWith(expression[..1999], step.Expression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1999)]
    [InlineData(2000)]
    public void Вираз_до_стелі_включно_не_змінюється(int length)
    {
        var expression = new string('b', length);
        var step = new CalculationStep(1, 202601, 1, "F1");

        step.Describe(expression, 1m, null, null);

        Assert.Equal(expression, step.Expression);
    }

    [Fact]
    public void Відсутній_вираз_лишається_null()
    {
        var step = new CalculationStep(1, 202601, 1, "F1");

        step.Describe(null, 1m, null, null);

        Assert.Null(step.Expression);
    }
}
