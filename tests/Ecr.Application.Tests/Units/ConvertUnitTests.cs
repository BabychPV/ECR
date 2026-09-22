// tests/Ecr.Application.Tests/Units/ConvertUnitTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Units;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Units;

/// <summary>
/// Явна конверсія одиниць через <c>POST /api/v1/units/convert</c>. Головне тут —
/// **симетричність** захисту від нульового множника: асиметричний захист не
/// падає, він тихо повертає константу замість числа (аудит 2026-09-16, §5.2).
/// </summary>
public sealed class ConvertUnitTests
{
    private const byte Mass = 1;
    private const byte Volume = 2;

    private readonly IUnitCatalog _catalog = Substitute.For<IUnitCatalog>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Тонни_у_кілограми_множаться_на_тисячу()
    {
        Catalogue(
            new UnitRef(1, "kg", Mass, 1m),
            new UnitRef(8, "t", Mass, 1000m));

        Assert.Equal(2500m, await Handler().HandleAsync(2.5m, "t", "kg", default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.3")]
    public async Task Різні_розмірності_відхиляються()
    {
        Catalogue(
            new UnitRef(1, "kg", Mass, 1m),
            new UnitRef(2, "m3", Volume, 1m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(1m, "kg", "m3", default));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0422.incompatibleDimensions", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нульовий_множник_ВИХІДНОЇ_одиниці_відхиляється_а_не_дає_константу()
    {
        // ⛔ Саме та асиметрія, від якої була знахідка: перевірявся лише `to`.
        // При `from.FactorToBase == 0` вираз `(value × 0) + from.OffsetToBase`
        // згортається до КОНСТАНТИ — усі різні вхідні значення дають одне й те
        // саме число. Доказ нижче: без захисту і 2, і мільйон дали б `-5`
        // (`(0 - 5) / 1`), і нічого в системі цього не помітило б.
        Catalogue(
            new UnitRef(1, "kg", Mass, 1m),
            new UnitRef(77, "broken", Mass, FactorToBase: 0m, OffsetToBase: 5m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(2m, "broken", "kg", default));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Contains("broken", error.Message, StringComparison.Ordinal);
        Assert.Equal("err.ECR-UOM-0422.zeroFactor", error.Details?["messageKey"]);
        Assert.Equal("broken", error.Details?["code"]);

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(1_000_000m, "broken", "kg", default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нульовий_множник_ЦІЛЬОВОЇ_одиниці_лишається_відхиленим()
    {
        Catalogue(
            new UnitRef(1, "kg", Mass, 1m),
            new UnitRef(77, "broken", Mass, FactorToBase: 0m));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(2m, "kg", "broken", default));

        Assert.Equal("ECR-UOM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-UOM-0422.zeroFactor", error.Details?["messageKey"]);
        Assert.Equal("broken", error.Details?["code"]);
    }

    private ConvertUnitHandler Handler() => new(_catalog);

    private void Catalogue(params UnitRef[] units)
        => _catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            units.ToDictionary(u => u.Code, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));
}
