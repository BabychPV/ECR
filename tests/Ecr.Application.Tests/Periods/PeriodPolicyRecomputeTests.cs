// tests/Ecr.Application.Tests/Periods/PeriodPolicyRecomputeTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Projects;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// ФВ-1.6 / D-217: зміна політики (чи поясу) ПЕРЕРАХОВУЄ збережені <c>Computed*At</c> наявних
/// періодів через обробник, а не лише через перебудову календаря; закриті періоди не чіпаються.
/// </summary>
/// <remarks>
/// Очікувані межі рахуються тим самим <c>TimeZoneInfo</c>, що й код, тож тест не залежить від
/// версії бази поясів ОС (D-217).
///
/// Мутаційні докази: прибрати цикл <c>PeriodBoundaryRefresh.Apply</c> у
/// <c>UpdatePeriodPolicyHandler</c> — перший тест червоніє; прибрати гілку
/// <c>State == Closed → continue</c> — другий; прибрати виклик у
/// <c>ChangeProjectTimeZoneHandler</c> — третій.
/// </remarks>
public sealed class PeriodPolicyRecomputeTests
{
    private const int AdminId = 9;
    private const int PolicyId = 1;
    private const string ZoneId = "Asia/Atyrau";
    private static readonly DateTime Now = new(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly PeriodPolicy _policy = ProjectBuilder.Policy();
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);
    private readonly Project _project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
    private readonly Period _closedJan;
    private readonly Period _openFeb;
    private readonly Period _scheduledApr;

    public PeriodPolicyRecomputeTests()
    {
        _closedJan = Make(202601, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        _closedJan.AdvanceTo(PeriodState.Closed, Now);
        _openFeb = Make(202602, 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        _openFeb.AdvanceTo(PeriodState.Open, Now);
        _scheduledApr = Make(202604, 4, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        ProjectBuilder.Attach(_project, [_closedJan, _openFeb, _scheduledApr]);

        _user.UserId.Returns(AdminId);
        _access.BuildProfileAsync(AdminId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = AdminId }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
                .Build());
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _periods.GetPolicyAsync(PolicyId, Arg.Any<CancellationToken>()).Returns(_policy);
        _periods.ListProjectIdsUsingPolicyAsync(PolicyId, Arg.Any<CancellationToken>())
            .Returns(new List<int> { AccessBuilder.ProjectId });
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(_project);
    }

    private Period Make(int key, byte sequence, DateOnly start, DateOnly end)
    {
        var period = new Period(AccessBuilder.ProjectId, new PeriodKey(key), sequence, start, end);
        period.RecomputeBoundaries(_policy, _zone);
        return period;
    }

    private DateTime Local(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _zone);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Зміна_політики_перераховує_збережені_межі_відкритого_і_запланованого_періодів()
    {
        var handler = new UpdatePeriodPolicyHandler(
            _periods, _access, _user, _uow, Substitute.For<IAuditWriter>(),
            Substitute.For<Ecr.Domain.Abstractions.IClock>());

        await handler.HandleAsync(PolicyId, -3, 10, 30, 45, CancellationToken.None);

        Assert.Equal(Local(new DateOnly(2026, 1, 29)), _openFeb.ComputedOpenAt);
        Assert.Equal(Local(new DateOnly(2026, 3, 10)), _openFeb.ComputedGraceAt);
        Assert.Equal(Local(new DateOnly(2026, 3, 30)), _openFeb.ComputedCloseAt);
        Assert.Equal(Local(new DateOnly(2026, 3, 29)), _scheduledApr.ComputedOpenAt);
        Assert.Equal(Local(new DateOnly(2026, 5, 30)), _scheduledApr.ComputedCloseAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Зміна_політики_не_переписує_межі_закритого_періоду()
    {
        var openBefore = _closedJan.ComputedOpenAt;
        var graceBefore = _closedJan.ComputedGraceAt;
        var closeBefore = _closedJan.ComputedCloseAt;
        var handler = new UpdatePeriodPolicyHandler(
            _periods, _access, _user, _uow, Substitute.For<IAuditWriter>(),
            Substitute.For<Ecr.Domain.Abstractions.IClock>());

        await handler.HandleAsync(PolicyId, -3, 10, 30, 45, CancellationToken.None);

        Assert.Equal(openBefore, _closedJan.ComputedOpenAt);
        Assert.Equal(graceBefore, _closedJan.ComputedGraceAt);
        Assert.Equal(closeBefore, _closedJan.ComputedCloseAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Зміна_поясу_до_відкриття_перераховує_межі_запланованих_періодів()
    {
        // Лише Scheduled-період: пояс змінюється тільки до відкриття першого (ФВ-1.1a).
        var project = ProjectBuilder.Project(PeriodKind.Monthly, "UTC", AccessBuilder.ProjectId);
        var scheduled = new Period(
            AccessBuilder.ProjectId, new PeriodKey(202604), 4, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        scheduled.RecomputeBoundaries(_policy, TimeZoneInfo.Utc);
        ProjectBuilder.Attach(project, [scheduled]);
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);

        await new ChangeProjectTimeZoneHandler(_periods, _access, _user, _uow)
            .HandleAsync(AccessBuilder.ProjectId, ZoneId, CancellationToken.None);

        Assert.Equal(Local(new DateOnly(2026, 4, 1)), scheduled.ComputedOpenAt);
        Assert.Equal(Local(new DateOnly(2026, 6, 14)), scheduled.ComputedCloseAt);
    }
}
