// tests/Ecr.Application.Tests/GasComposition/GasCompositionValidationTests.cs
using Ecr.Application.Errors;
using Ecr.Application.GasComposition;
using Xunit;

namespace Ecr.Application.Tests.GasComposition;

/// <summary>
/// Перевірка значень версії складу (D-NEW): Σ = 100 ± 0.5 або явне підтвердження, діапазони, дублі, невідомі компоненти;
/// нуль — рядка немає.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не в коміті): (1) прибрати умову <c>!values.AcceptSumDeviation</c> (завжди відмова) →
/// <c>Підтверджена_Σ_поза_допуском_приймається</c> червоний; (2) прибрати перевірку Σ зовсім →
/// <c>Σ_поза_допуском_без_підтвердження_відмова</c> червоний; (3) допуск 0.5 → 1 →
/// <c>Межа_допуску_рівно_пів_відсотка</c> червоний; (4) прибрати перевірку дубля → <c>Дубль_компонента_відмова</c> червоний.
/// </remarks>
public sealed class GasCompositionValidationTests
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "Methane", "Ethane", "H2S", "CO2" };

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Σ_рівно_100_приймається_а_нулі_відкидаються()
    {
        var result = GasCompositionValidation.Validate(Values(("Methane", 97m), ("ethane", 3m), ("H2S", 0m)), Known);

        Assert.Equal(100m, result.Sum);
        Assert.False(result.NoComposition);
        Assert.Equal(2, result.Components.Count);
        Assert.Equal(3m, result.Components["Ethane"]); // регістр — як у довідника
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Межа_допуску_рівно_пів_відсотка()
    {
        Assert.Equal(99.5m, GasCompositionValidation.Validate(Values(("Methane", 99.5m)), Known).Sum);
        Assert.Equal(100.5m, GasCompositionValidation.Validate(Values(("Methane", 100m), ("CO2", 0.5m)), Known).Sum);
        Assert.Throws<BusinessRuleException>(() => GasCompositionValidation.Validate(Values(("Methane", 99.49m)), Known));
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Σ_поза_допуском_без_підтвердження_відмова()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => GasCompositionValidation.Validate(Values(("Methane", 90m)), Known));

        Assert.Equal("ECR-REG-0422", ex.ErrorCode);
        Assert.Equal(GasCompositionValidation.SumDeviationKey, ex.Details!["messageKey"]);
        Assert.Equal("90", ex.Details["sum"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Підтверджена_Σ_поза_допуском_приймається()
    {
        var result = GasCompositionValidation.Validate(Values(true, ("Methane", 90m)), Known);
        Assert.Equal(90m, result.Sum);

        var empty = GasCompositionValidation.Validate(Values(true), Known);
        Assert.True(empty.NoComposition);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Порожній_склад_без_підтвердження_відмова()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => GasCompositionValidation.Validate(Values(), Known));
        Assert.Equal(GasCompositionValidation.SumDeviationKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Дубль_компонента_відмова()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            GasCompositionValidation.Validate(Values(("Methane", 50m), ("METHANE", 50m)), Known));
        Assert.Equal(GasCompositionValidation.ComponentDuplicateKey, ex.Details!["messageKey"]);
    }

    [Theory]
    [Trait("Requirement", "D-476")]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    public void Мол_відсоток_поза_0_100_відмова(double value)
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            GasCompositionValidation.Validate(Values(true, ("Methane", (decimal)value)), Known));
        Assert.Equal(GasCompositionValidation.ComponentRangeKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Невідомий_компонент_відмова()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => GasCompositionValidation.Validate(Values(("Helium", 100m)), Known));
        Assert.Equal(GasCompositionValidation.ComponentUnknownKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Температура_нижче_абсолютного_нуля_і_відємна_густина_відмова()
    {
        var cold = new GasVersionValues(-273.15m, null, null, null, [new("Methane", 100m)], false);
        Assert.Equal(GasCompositionValidation.PropertyRangeKey,
            Assert.Throws<BusinessRuleException>(() => GasCompositionValidation.Validate(cold, Known)).Details!["messageKey"]);

        var dense = new GasVersionValues(20m, -1m, null, null, [new("Methane", 100m)], false);
        Assert.Equal(GasCompositionValidation.PropertyRangeKey,
            Assert.Throws<BusinessRuleException>(() => GasCompositionValidation.Validate(dense, Known)).Details!["messageKey"]);
    }

    private static GasVersionValues Values(params (string Code, decimal Value)[] components) => Values(false, components);

    private static GasVersionValues Values(bool accept, params (string Code, decimal Value)[] components)
        => new(20m, null, null, null, [.. components.Select(c => new GasComponentValue(c.Code, c.Value))], accept);
}
