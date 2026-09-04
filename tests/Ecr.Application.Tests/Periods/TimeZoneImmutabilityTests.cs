// tests/Ecr.Application.Tests/Periods/TimeZoneImmutabilityTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// `TimeZoneId` незмінний після відкриття першого періоду (ФВ-1.1a, D-110):
/// ретроактивна зміна зсунула б межі **закритих** періодів і переписала б
/// `IsLateEdit` на **поданих** формах.
/// </summary>
public sealed class TimeZoneImmutabilityTests
{
    private static readonly DateTime Now = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_стані_Draft_пояс_змінюється()
    {
        var project = ProjectBuilder.Project();
        ProjectBuilder.Attach(project, PeriodCalendar.Build(
            project, ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []));

        project.ChangeTimeZone("Asia/Aqtau");

        // Поки жоден період не відкривався, межі ще нічиї зобов'язання.
        Assert.Equal("Asia/Aqtau", project.TimeZoneId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_відкриття_першого_періоду_зміна_дає_ECR_PRD_0409()
    {
        var project = ProjectBuilder.Project();
        var periods = PeriodCalendar.Build(
            project, ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []);
        ProjectBuilder.Attach(project, periods);

        periods[0].TransitionTo(PeriodState.Open, Now);

        var error = Assert.Throws<DomainException>(() => project.ChangeTimeZone("Asia/Aqtau"));

        // ⚠ Ознака — не статус проєкту, а факт, що якийсь період уже вийшов зі
        // Scheduled. Саме з цієї миті зміна поясу переписала б минуле: запис,
        // який був вчасним, став би пізнім заднім числом.
        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("Asia/Almaty", project.TimeZoneId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_поясу_в_Draft_перераховує_межі_періодів()
    {
        var project = ProjectBuilder.Project();
        var policy = ProjectBuilder.Policy();
        var periods = PeriodCalendar.Build(project, policy, ProjectBuilder.Zone(5), existing: []);
        ProjectBuilder.Attach(project, periods);

        var before = periods[0].ComputedOpenAt;

        project.ChangeTimeZone("UTC");
        foreach (var period in periods)
        {
            period.RecomputeBoundaries(policy, ProjectBuilder.Zone(0));
        }

        // Зсув поясу на п'ять годин зсуває межі рівно на п'ять годин — інакше
        // «опівніч на майданчику» перестала б бути опівніччю.
        Assert.Equal(before.AddHours(5), periods[0].ComputedOpenAt);
    }
}
