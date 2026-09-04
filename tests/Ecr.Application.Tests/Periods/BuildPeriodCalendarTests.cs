// tests/Ecr.Application.Tests/Periods/BuildPeriodCalendarTests.cs
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
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
    {
        var periodKind = Enum.Parse<PeriodKind>(kind);
        var project = ProjectBuilder.Project(periodKind);

        var periods = PeriodCalendar.Build(
            project, ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []);

        Assert.Equal(expected, periods.Count);
        Assert.Equal(expected, PeriodCalendar.CountFor(periodKind));

        // Межі не залазять за проєкт і йдуть суцільно: між кінцем одного
        // періоду і початком наступного не має бути дірки.
        Assert.Equal(project.PeriodStart, periods[0].PeriodStart);
        Assert.Equal(project.PeriodEnd, periods[^1].PeriodEnd);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void PeriodKey_рахується_як_рік_на_сто_плюс_послідовність()
    {
        var periods = PeriodCalendar.Build(
            ProjectBuilder.Project(), ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []);

        Assert.Equal(202601, periods[0].PeriodKeyValue);
        Assert.Equal(202612, periods[^1].PeriodKeyValue);
        Assert.All(periods, p => Assert.Equal((p.PeriodKeyValue / 100 * 100) + p.Sequence, p.PeriodKeyValue));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Для_квартального_періоду_PeriodKey_не_є_датою()
    {
        var periods = PeriodCalendar.Build(
            ProjectBuilder.Project(PeriodKind.Quarterly), ProjectBuilder.Policy(),
            ProjectBuilder.Zone(), existing: []);

        // ⚠ 202602 для кварталу — це ДРУГИЙ КВАРТАЛ, а не лютий. Читати
        // PeriodKey як «рік-місяць» можна лише для Monthly, і саме тому це
        // ключ, а не дата: квартал охоплює квітень–червень.
        Assert.Equal(202602, periods[1].PeriodKeyValue);
        Assert.Equal(new DateOnly(2026, 4, 1), periods[1].PeriodStart);
        Assert.Equal(new DateOnly(2026, 6, 30), periods[1].PeriodEnd);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Повторний_виклик_не_створює_дублікатів()
    {
        var project = ProjectBuilder.Project();
        var policy = ProjectBuilder.Policy();
        var zone = ProjectBuilder.Zone();

        var first = PeriodCalendar.Build(project, policy, zone, existing: []);
        var second = PeriodCalendar.Build(project, policy, zone, existing: first);

        // Календар будують і при створенні проєкту, і після зміни політики —
        // другий раз він має лише доповнювати, а не подвоювати рік.
        Assert.Equal(12, first.Count);
        Assert.Empty(second);
    }
}
