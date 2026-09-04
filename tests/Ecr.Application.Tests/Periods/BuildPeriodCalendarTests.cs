// tests/Ecr.Application.Tests/Periods/BuildPeriodCalendarTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>Календар періодів і формула `PeriodKey` (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("Monthly", 12)]
    [InlineData("Quarterly", 4)]
    [InlineData("Yearly", 1)]
    public void Кількість_періодів_відповідає_періодичності(string kind, int expected)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void PeriodKey_рахується_як_рік_на_сто_плюс_послідовність()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Для_квартального_періоду_PeriodKey_не_є_датою()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторний_виклик_не_створює_дублікатів()
        => Assert.Fail("not implemented");
}
