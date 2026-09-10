using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// Зміна поясу майданчика проєкту через API (T6/#52).
/// </summary>
/// <remarks>
/// ⛔ Аудит (`[звірка]`, слабша впевненість) назвав відсутній ендпоінт над
/// повністю готовим і протестованим доменним методом
/// <see cref="Project.ChangeTimeZone"/> (домен-рівневі тести —
/// <c>ProjectTimeZoneIanaTests</c>, <c>TimeZoneImmutabilityTests</c>). Ці
/// тести перевіряють РІВНО те, чого раніше не існувало: що обробник
/// застосунку і право доступу над цим методом справді підключені, а не
/// правило само по собі — воно вже було правильним.
/// </remarks>
public sealed class ChangeProjectTimeZoneTests
{
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ChangeProjectTimeZoneTests()
    {
        _user.UserId.Returns(9);
        // ⚠ Грант на 10 — дефолтний Id `ProjectBuilder.Project()`.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, 10, GrantLevel.Manage)
                .Build());
    }

    private ChangeProjectTimeZoneHandler Handler() => new(_periods, _access, _user, _uow);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Draft_без_періодів_дозволяє_зміну()
    {
        var project = ProjectBuilder.Project(timeZoneId: "Asia/Almaty");
        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        await Handler().HandleAsync(project.Id, "Asia/Aqtau", CancellationToken.None);

        Assert.Equal("Asia/Aqtau", project.TimeZoneId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-1.1a")]
    public async Task D_134_Після_відкриття_періоду_зміна_відхиляється_ECR_PRD_0409()
    {
        // ⛔ D-134: «неможливе значення» тут — зміна поясу ПІСЛЯ того, як
        // хоч один період вийшов зі Scheduled. Ретроактивна зміна зсунула б
        // межі закритих періодів і переписала б `IsLateEdit` на поданих
        // формах.
        var project = ProjectBuilder.Project(timeZoneId: "Asia/Almaty");
        var periods = Ecr.Domain.Services.PeriodCalendar.Build(
            project, ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []);
        ProjectBuilder.Attach(project, periods);
        periods[0].TransitionTo(PeriodState.Open, new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(project.Id, "Asia/Aqtau", CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("Asia/Almaty", project.TimeZoneId);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-1.1b")]
    public async Task Невідомий_пояс_дає_ECR_CFG_4221()
    {
        var project = ProjectBuilder.Project(timeZoneId: "Asia/Almaty");
        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(project.Id, "Central Asia Standard Time", CancellationToken.None));

        Assert.Equal("ECR-CFG-4221", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Проєкту_немає_ECR_PRJ_0404()
    {
        _periods.FindProjectAsync(42, Arg.Any<CancellationToken>()).Returns((Project?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(42, "Asia/Aqtau", CancellationToken.None));

        Assert.Equal(ErrorCodes.ProjectNotFound, error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "Q-179")]
    public async Task Без_гранта_на_КОНКРЕТНИЙ_проєкт_зміна_відхиляється()
    {
        // ⛔ Той самий патерн, що й Activate/Archive/Clone (Q-179): глобальне
        // `Project.Manage` без гранта на ЦЕЙ проєкт не має бути достатнім.
        var project = ProjectBuilder.Project(timeZoneId: "Asia/Almaty");
        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Project.Manage").Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(project.Id, "Asia/Aqtau", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal("Asia/Almaty", project.TimeZoneId);
    }
}
