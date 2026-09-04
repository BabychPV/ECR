using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// <see cref="PeriodKey"/> = <c>Year * 100 + Sequence</c> (R-A6).
/// Збіг із <c>YYYYMM</c> існує **лише для місячних періодів**, і саме тому
/// виводити місяць арифметикою заборонено.
/// </summary>
public sealed class PeriodKeyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Місячний_період_дає_ключ_у_форматі_YYYYMM()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Квартальний_період_дає_ключ_YYYY01_до_YYYY04_а_не_місяць()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Річний_період_дає_послідовність_1()
        => Assert.Fail("not implemented");

    [Theory]
    [InlineData(1899, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 100)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_поза_допустимими_межами_кидає_виняток(int year, int sequence)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Діапазон_року_для_квартального_періоду_охоплює_чотири_ключі_а_не_дванадцять()
        => Assert.Fail("not implemented");
}
