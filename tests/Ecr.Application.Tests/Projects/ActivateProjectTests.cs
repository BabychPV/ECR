using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// Активація проєкту відкриває періоди <b>одразу</b> (директива №09 `W8` п.1,
/// `S-11`).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>Period.AdvanceTo</c> кликав рівно один викликач —
/// <c>PeriodStateJob</c> на годинному розкладі. Тобто щойно активований
/// проєкт до години показував на кожній комірці «період ще не відкрито», хоч
/// за датами він давно відкритий. Причина неправдива, перевірити її оператору
/// нічим, і саме такі дрібниці ламають довіру до всього екрана.
/// </remarks>
public sealed class ActivateProjectTests
{
    /// <summary>Середина лютого 2026: січень у Grace, лютий відкритий.</summary>
    private static readonly DateTime Now = new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ActivateProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        // ⚠ Грант на 10 — це `ProjectBuilder.Project()`'s дефолтний Id
        // (`Q-179`, аудит фази 2, авторизація).
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, 10, GrantLevel.Manage)
                .Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.12")]
    public async Task Активація_відкриває_період_не_чекаючи_годинного_прогону()
    {
        var project = Arrange();

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Active, project.Status);

        // ⛔ Головне твердження: стан ПЕРІОДУ, а не проєкту. Активний проєкт
        // із періодом у `Scheduled` — це рівно те, що бачив оператор годину:
        // проєкт наче працює, а заповнити не можна нічого.
        Assert.Contains(project.Periods, p => p.State == PeriodState.Open);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-77")]
    public async Task Активація_призначає_поточний_період()
    {
        var project = Arrange();

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        // ⚠ Поточний період теж не має чекати години: на нього спирається
        // кожен екран, який відкриває документ «за поточний період».
        Assert.NotNull(project.CurrentPeriodId);
        Assert.Equal(
            project.Periods.Single(p => p.State == PeriodState.Open).Id,
            project.CurrentPeriodId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_активація_відхиляється()
    {
        // ⛔ Q-179 (аудит фази 2, авторизація). Глобальне `Project.Manage`
        // саме по собі не давало права активувати БУДЬ-ЯКИЙ проєкт —
        // потрібен грант на КОНКРЕТНИЙ.
        var project = Arrange();
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());

        var denied = await Assert.ThrowsAsync<Application.Errors.AccessDeniedException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal(ProjectStatus.Draft, project.Status);
    }

    /// <summary>Проєкт-чернетка з побудованим календарем 2026 року.</summary>
    private Project Arrange()
    {
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        var policy = ProjectBuilder.Policy();

        ProjectBuilder.Attach(
            project,
            PeriodCalendar.Build(project, policy, TimeZoneInfo.Utc, []));

        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        return project;
    }

    private ActivateProjectHandler Handler()
        => new(_periods, _access, _user, _uow, new PeriodStateCalculator(), _clock);
}
