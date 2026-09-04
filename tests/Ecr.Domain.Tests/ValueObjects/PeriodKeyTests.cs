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
    {
        var january = PeriodKey.Create(2026, 1);
        var december = PeriodKey.Create(2026, 12);

        Assert.Equal(202601, january.Value);
        Assert.Equal(202612, december.Value);
        Assert.Equal(2026, december.Year);
        Assert.Equal(12, december.Sequence);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Квартальний_період_дає_ключ_YYYY01_до_YYYY04_а_не_місяць()
    {
        // Четвертий квартал — це Sequence = 4, а не місяць 10, 11 чи 12.
        var q4 = PeriodKey.Create(2026, 4);

        Assert.Equal(202604, q4.Value);
        Assert.Equal(4, q4.Sequence);

        // Саме тут ламається здогадка «ключ = YYYYMM»: 202604 для квартального
        // періоду означає Q4, а для місячного — квітень. Число те саме, зміст різний,
        // і відрізняє їх лише PeriodKind проєкту, а не сам ключ.
        Assert.Equal(PeriodKey.Create(2026, 4).Value, q4.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Річний_період_дає_послідовність_1()
    {
        var year = PeriodKey.Create(2026, 1);

        Assert.Equal(1, year.Sequence);
        Assert.Equal(202601, year.Value);

        var (from, to) = PeriodKey.YearRange(2026, PeriodKind.Yearly);
        Assert.Equal(from, to);
    }

    [Theory]
    [InlineData(1899, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 100)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_поза_допустимими_межами_кидає_виняток(int year, int sequence)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PeriodKey.Create(year, sequence));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Діапазон_року_для_квартального_періоду_охоплює_чотири_ключі_а_не_дванадцять()
    {
        var (from, to) = PeriodKey.YearRange(2026, PeriodKind.Quarterly);

        Assert.Equal(202601, from.Value);
        Assert.Equal(202604, to.Value);

        // Порівняння з місячним показує, чому архівація не може жорстко брати YYYY01..YYYY12:
        // для квартального проєкту це зачепило б вісім неіснуючих партицій.
        var (monthlyFrom, monthlyTo) = PeriodKey.YearRange(2026, PeriodKind.Monthly);
        Assert.Equal(202601, monthlyFrom.Value);
        Assert.Equal(202612, monthlyTo.Value);
        Assert.NotEqual(monthlyTo.Value, to.Value);
    }
}
