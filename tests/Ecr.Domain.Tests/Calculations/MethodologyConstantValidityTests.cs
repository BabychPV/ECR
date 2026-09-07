// tests/Ecr.Domain.Tests/Calculations/MethodologyConstantValidityTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Темпоральний відбір констант — той самий напівінтервал
/// <c>[ValidFrom, ValidTo)</c>, що й у довідниках (крок <c>I.10</c>).
/// </summary>
/// <remarks>
/// ⛔ Ціна помилки на день тут інша, ніж у довіднику, і вища: там запис
/// зникає зі списку, а тут у формулу підставляється **сусідній коефіцієнт
/// емісії**. Звіт виходить правдоподібним, відмови немає, і побачити це можна
/// лише звіркою чисел.
/// </remarks>
public sealed class MethodologyConstantValidityTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.1")]
    public void IsValidOn_бере_нижню_межу_включно_а_верхню_ні()
    {
        var constant = Numeric();

        // Коефіцієнт, чинний увесь 2024 рік, — так, як його пише перенос
        // (`2024-12-31 23:59:59` у джерелі → `2025-01-01` у нас).
        constant.SetValidity(new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1));

        Assert.False(constant.IsValidOn(new DateOnly(2023, 12, 31)));
        Assert.True(constant.IsValidOn(new DateOnly(2024, 1, 1)));
        Assert.True(constant.IsValidOn(new DateOnly(2024, 12, 31)));

        // ⚠ Ось той самий день: із закритою межею тут стояло б `true`, і
        // 1 січня рахувалося б торішнім коефіцієнтом.
        Assert.False(constant.IsValidOn(new DateOnly(2025, 1, 1)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Порожнє_вікно_відхиляється_разом_із_рівністю_меж()
    {
        var constant = Numeric();

        var reversed = Assert.Throws<DomainException>(
            () => constant.SetValidity(new DateOnly(2025, 1, 1), new DateOnly(2024, 1, 1)));
        Assert.Equal("ECR-CALC-0422", reversed.ErrorCode);

        var equal = Assert.Throws<DomainException>(
            () => constant.SetValidity(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 1)));
        Assert.Equal("ECR-CALC-0422", equal.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_меж_константа_чинна_завжди()
    {
        var constant = Numeric();

        Assert.True(constant.IsValidOn(new DateOnly(1990, 1, 1)));
        Assert.True(constant.IsValidOn(new DateOnly(2099, 12, 31)));
        Assert.Equal(ValidityWindow.Always, constant.Window);
    }

    private static MethodologyConstant Numeric()
        => new(methodologyVersionId: 1, EcrCode.Create("k1_Density_"), 0.85m, unitId: 3);
}
