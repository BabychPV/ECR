// tests/Ecr.Calculations.Tests/ConstantAmbiguousCategoriesTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Неоднозначність константи називає категорії-кандидати (Land Demo RC9, L-2).
/// </summary>
/// <remarks>
/// Константа AF із категоріями (5.1: <c>Loc_BeforeMR_B</c>…) без заданої
/// категорії дає <c>constantAmbiguous</c>. Раніше повідомлення казало лише
/// «16 кандидатів» — користувач не бачив, ЩО саме не вибрано.
/// </remarks>
public sealed class ConstantAmbiguousCategoriesTests
{
    private const int VersionId = 91;
    private static readonly DateOnly OnDate = new(2026, 6, 30);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Неоднозначність_без_категорії_називає_категорії_кандидатів()
    {
        var resolver = new ConstantResolver(null!);
        var candidates = new[]
        {
            Constant("EF_tons_NOx_", 40m, "Loc_BeforeMR_B"),
            Constant("EF_tons_NOx_", 30m, "Loc_BeforeMR_A"),
        };

        var error = Assert.Throws<DomainException>(
            () => resolver.Resolve(candidates, VersionId, "EF_tons_NOx_", null, null, OnDate));

        Assert.Equal("err.ECR-CALC-0422.constantAmbiguous", error.Details!["messageKey"]);
        Assert.Equal("Loc_BeforeMR_A, Loc_BeforeMR_B", error.Details!["categories"]);
        Assert.Contains("Loc_BeforeMR_A, Loc_BeforeMR_B", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Задана_категорія_вибирає_одну_константу_з_двох()
    {
        var resolver = new ConstantResolver(null!);
        var candidates = new[]
        {
            Constant("EF_tons_NOx_", 40m, "Loc_BeforeMR_B"),
            Constant("EF_tons_NOx_", 30m, "Loc_BeforeMR_A"),
        };

        Assert.Equal(
            40m,
            resolver.Resolve(candidates, VersionId, "EF_tons_NOx_", "Loc_BeforeMR_B", null, OnDate)!.Number);
    }

    private static MethodologyConstant Constant(string code, decimal value, string category)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 23);
        constant.SetScope(category, null);
        return constant;
    }
}
