using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>Архівація проєкту (`D-123`, `A7-25`).</summary>
/// <remarks>
/// ⛔ Для цього обробника не існувало жодного тесту. Додано разом із
/// перевіркою гранта на конкретний проєкт (`Q-179`, аудит фази 2) — без
/// неї глобальне `Project.Manage` давало б право заархівувати БУДЬ-ЯКИЙ
/// проєкт, не лише свій.
/// </remarks>
public sealed class ArchiveProjectTests
{
    private static readonly DateTime Now = new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ArchiveProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        // ⚠ Грант на 10 — дефолтний Id `ProjectBuilder.Project()`.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, 10, GrantLevel.Manage)
                .Build());
    }

    /// <summary>Активний проєкт з усіма періодами закритими.</summary>
    private Project ArrangeClosed(bool leaveOnePeriodOpen = false)
    {
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        var policy = ProjectBuilder.Policy();

        ProjectBuilder.Attach(
            project,
            Ecr.Domain.Services.PeriodCalendar.Build(project, policy, TimeZoneInfo.Utc, []));

        project.Activate(Now);

        foreach (var period in project.Periods)
        {
            if (leaveOnePeriodOpen && period == project.Periods[0])
            {
                period.TransitionTo(PeriodState.Open, Now);
                continue;
            }

            period.TransitionTo(PeriodState.Open, Now);
            period.TransitionTo(PeriodState.Grace, Now);
            period.TransitionTo(PeriodState.Closed, Now);
        }

        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        return project;
    }

    private ArchiveProjectHandler Handler() => new(_periods, _access, _user, _uow, _clock);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-123")]
    public async Task Проєкт_з_усіма_закритими_періодами_архівується()
    {
        var project = ArrangeClosed();

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Archived, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-123")]
    public async Task Незакритий_період_блокує_архівацію()
    {
        var project = ArrangeClosed(leaveOnePeriodOpen: true);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_архівація_відхиляється()
    {
        // ⛔ Q-179 (аудит фази 2, авторизація).
        var project = ArrangeClosed();
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal(ProjectStatus.Active, project.Status);
    }
}
