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

    /// <summary>
    /// R9-F3 / F3-03: перевідкритий період закривається за дедлайном Reopen, а не за межами,
    /// які зсунула нова політика.
    /// </summary>
    /// <remarks>
    /// Січень (hard-close 45 → межа 17.03) закрито, 20.03 перевідкрито до 25.03. 21.03 політику
    /// змінено на hard-close 120 (межа січня стала б 31.05). Без фіксу 26.03 січень ефективно
    /// <c>Grace</c> (межа ще попереду) — запис дозволено без події Reopen; з фіксом — <c>Closed</c>.
    /// Мутація: прибрати <c>ReopenedUntil is not null</c> у <c>PeriodBoundaryRefresh</c> — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Зміна_політики_не_подовжує_перевідкритий_період_за_дедлайн_Reopen()
    {
        var reopenedAt = new DateTime(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);
        var until = reopenedAt.AddDays(5);
        var jan = ReopenedJanuary(reopenedAt, until);
        var closeBefore = jan.ComputedCloseAt;

        await PolicyHandler(reopenedAt.AddDays(1)).HandleAsync(PolicyId, 0, 15, 120, 45, CancellationToken.None);

        Assert.Equal(closeBefore, jan.ComputedCloseAt);
        Assert.Equal(PeriodState.Closed, new Ecr.Domain.Services.PeriodStateCalculator().Effective(jan, until.AddDays(1)));
    }

    /// <summary>
    /// R9-F3 / F3-03 (вторинний випадок): період, що вже минув свою межу закриття, але
    /// збережений стан ще <c>Grace</c> (годинна задача станів не відпрацювала), нових меж не
    /// отримує — зміна політики не відкриває закритий період.
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати перевірку <c>now ≥ ComputedCloseAt</c> у <c>PeriodBoundaryRefresh</c> — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Зміна_політики_не_відкриває_період_що_ефективно_вже_закрився()
    {
        var project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
        var jan = Make(202601, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AdvanceTo(PeriodState.Grace, Now);
        ProjectBuilder.Attach(project, [jan]);
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);
        var closeBefore = jan.ComputedCloseAt;
        var afterClose = closeBefore.AddHours(1);

        await PolicyHandler(afterClose).HandleAsync(PolicyId, 0, 15, 120, 45, CancellationToken.None);

        Assert.Equal(closeBefore, jan.ComputedCloseAt);
        Assert.Equal(PeriodState.Closed, new Ecr.Domain.Services.PeriodStateCalculator().Effective(jan, afterClose));
    }

    /// <summary>
    /// R10-V9 / V9-01: після зміни політики <c>GET …/periods</c> від користувача з грантом <c>Write</c>
    /// (добудова календаря) не подовжує перевідкритий період — той самий сторож, що й у зміни політики.
    /// </summary>
    /// <remarks>
    /// Сценарій F3-03 з одним кроком більше: політику змінено (сторож спрацював), а потім хтось відкрив
    /// сторінку періодів. Доти <c>PeriodCalendar.Build</c> безумовно перераховував межі всіх наявних
    /// періодів за НОВОЮ політикою, і після <c>until</c> січень лишався <c>Grace</c>.
    /// Мутація: прибрати перевірку <c>AcceptsBoundaryRefresh</c> у циклі <c>PeriodCalendar.Build</c> — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Перевідкритий_період_GET_календаря_після_зміни_політики_не_подовжує()
    {
        var reopenedAt = new DateTime(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);
        var until = reopenedAt.AddDays(5);
        var jan = ReopenedJanuary(reopenedAt, until);
        var closeBefore = jan.ComputedCloseAt;

        await PolicyHandler(reopenedAt.AddDays(1)).HandleAsync(PolicyId, 0, 15, 120, 45, CancellationToken.None);
        await CalendarHandler(reopenedAt.AddDays(1)).HandleAsync(AccessBuilder.ProjectId, CancellationToken.None);

        Assert.Equal(closeBefore, jan.ComputedCloseAt);
        Assert.Equal(PeriodState.Closed, new Ecr.Domain.Services.PeriodStateCalculator().Effective(jan, until.AddDays(1)));
    }

    /// <summary>
    /// R10-V9 / V9-01 (вторинний випадок): ефективно закритий період (збережений стан ще <c>Grace</c>)
    /// <c>GET …/periods</c> після зміни політики знову не відкриває.
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати перевірку <c>now ≥ ComputedCloseAt</c> у <c>Period.AcceptsBoundaryRefresh</c> або
    /// передавати <c>utcNow = null</c> з <c>PeriodCalendarMaterializer</c> — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Ефективно_закритий_період_GET_календаря_після_зміни_політики_не_відкриває()
    {
        var project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
        var jan = Make(202601, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AdvanceTo(PeriodState.Grace, Now);
        ProjectBuilder.Attach(project, [jan]);
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);
        var closeBefore = jan.ComputedCloseAt;
        var afterClose = closeBefore.AddHours(1);

        await PolicyHandler(afterClose).HandleAsync(PolicyId, 0, 15, 120, 45, CancellationToken.None);
        await CalendarHandler(afterClose).HandleAsync(AccessBuilder.ProjectId, CancellationToken.None);

        Assert.Equal(closeBefore, jan.ComputedCloseAt);
        Assert.Equal(PeriodState.Closed, new Ecr.Domain.Services.PeriodStateCalculator().Effective(jan, afterClose));
    }

    /// <summary>
    /// R10-V9 / V9-01, зворотний бік: сторож не глушить A7-26 — відкритий період, межа якого ще попереду,
    /// від <c>GET …/periods</c> нові межі отримує; збережений <c>Closed</c> — ні.
    /// </summary>
    /// <remarks>
    /// Мутація: у <c>PeriodCalendar.Build</c> не перераховувати наявні періоди зовсім — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task GET_календаря_перераховує_межі_відкритого_періоду_і_не_чіпає_закритого()
    {
        var closedJanBefore = _closedJan.ComputedCloseAt;
        _policy.UpdateOffsets(-3, 10, 30, 45);

        await CalendarHandler(Now).HandleAsync(AccessBuilder.ProjectId, CancellationToken.None);

        Assert.Equal(Local(new DateOnly(2026, 3, 30)), _openFeb.ComputedCloseAt);
        Assert.Equal(Local(new DateOnly(2026, 5, 30)), _scheduledApr.ComputedCloseAt);
        Assert.Equal(closedJanBefore, _closedJan.ComputedCloseAt);
    }

    /// <summary>
    /// UAT 08.11 (e): «кнопки реопен немає». Збереження політики, що переносить строк блокування лютого в минуле,
    /// ОДРАЗУ переводить лютий у <c>Closed</c> (той самий шлях, що F-08 в архівації) і пише аудит переходу.
    /// </summary>
    /// <remarks>
    /// До фіксу збережений <c>Open</c> лишався до прогону <c>PeriodStateJob</c>: Periods показувала «Open · closes
    /// today» без Reopen. Мутація: прибрати виклик <c>PeriodStateCatchUp.ApplyAsync</c> в
    /// <c>UpdatePeriodPolicyHandler</c> — червоніє (стан лишається <c>Open</c>, аудиту переходу немає).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Збереження_політики_одразу_блокує_місяць_чий_новий_строк_минув_і_пише_аудит()
    {
        var project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
        var feb = Make(202602, 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        feb.AdvanceTo(PeriodState.Open, Now);
        ProjectBuilder.Attach(project, [feb]);
        project.Activate(Now);
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);

        // 20.03: стара межа лютого (+45 днів) — середина квітня; нова (+10) — 11.03, уже минула.
        var at = new DateTime(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);
        var audit = Substitute.For<IAuditWriter>();
        var clock = Substitute.For<Ecr.Domain.Abstractions.IClock>();
        clock.UtcNow.Returns(at);

        await new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow, audit, clock)
            .HandleAsync(PolicyId, 0, 5, 10, 45, CancellationToken.None);

        Assert.Equal(PeriodState.Closed, feb.State);
        await audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "Period"
                && r.Operation == "AdvanceState"
                && r.EntityId == feb.Id
                && r.ChangedByUserId == AdminId
                && r.OldJson!.Contains("Open")
                && r.NewJson!.Contains("Closed")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// UAT 08.11 (e), зворотний бік: чернетку (її періодів не веде задача станів) збереження політики не просуває.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Збереження_політики_не_просуває_стани_чернетки()
    {
        var at = new DateTime(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);
        var clock = Substitute.For<Ecr.Domain.Abstractions.IClock>();
        clock.UtcNow.Returns(at);

        await new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow, Substitute.For<IAuditWriter>(), clock)
            .HandleAsync(PolicyId, 0, 5, 10, 45, CancellationToken.None);

        Assert.Equal(PeriodState.Open, _openFeb.State);
    }

    /// <summary>
    /// F13 e1 (09.11), зворотний бік: політику 3/20 повернули на 15/45 — жовтень із «Late» (Grace) ОДРАЗУ
    /// повертається в Open з аудитом, а заблокований вересень лишається Locked.
    /// </summary>
    /// <remarks>
    /// Мутація: у <c>PeriodStateCatchUp</c> брати лише <c>Effective</c> (вперед) — жовтень лишається Grace, червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.6")]
    public async Task Відпущена_політика_повертає_Late_місяць_у_Open_а_Locked_лишає()
    {
        var (sep, oct, audit) = await LoosenAfterTightPolicy();

        Assert.Equal(PeriodState.Closed, sep.State);
        Assert.Equal(PeriodState.Open, oct.State);
        await audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "Period"
                && r.Operation == "AdvanceState"
                && r.EntityId == oct.Id
                && r.OldJson!.Contains("Grace")
                && r.NewJson!.Contains("Open")),
            Arg.Any<CancellationToken>());
        await audit.DidNotReceive().WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "Period" && r.EntityId == sep.Id),
            Arg.Any<CancellationToken>());
    }

    /// <summary>F13 e1: перевідкритий місяць відпущена політика назад не веде — його тримає дедлайн Reopen.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Відпущена_політика_не_чіпає_перевідкритий_місяць()
    {
        var at = new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
        _policy.UpdateOffsets(0, 3, 20, 45);
        var project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
        var jan = Make(202601, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AdvanceTo(PeriodState.Closed, at.AddDays(-2));
        jan.Reopen(at.AddDays(1), "F13 e4", at.AddDays(-1));
        ProjectBuilder.Attach(project, [jan]);
        project.Activate(at.AddDays(-300));
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);

        await PolicyHandler(at).HandleAsync(PolicyId, 0, 15, 45, 45, CancellationToken.None);

        Assert.Equal(PeriodState.Grace, jan.State);
        Assert.NotNull(jan.ReopenedUntil);
    }

    /// <summary>Домен: назад у Closed чи з Closed <see cref="Period.RederiveTo"/> не веде.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public void RederiveTo_не_відкриває_заблокований_місяць()
    {
        var ex = Assert.Throws<Ecr.Domain.Abstractions.DomainException>(() => _closedJan.RederiveTo(PeriodState.Open, Now));
        Assert.Equal("ECR-PRD-0409", ex.ErrorCode);
        Assert.Equal(PeriodState.Closed, _closedJan.State);
    }

    private async Task<(Period Sep, Period Oct, IAuditWriter Audit)> LoosenAfterTightPolicy()
    {
        // 09.11 06:00Z. Політика 3/20: вересень заблоковано 20.10, жовтень Late з 03.11 до 20.11.
        var at = new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
        _policy.UpdateOffsets(0, 3, 20, 45);
        var project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
        var sep = Make(202609, 9, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        sep.AdvanceTo(PeriodState.Closed, at.AddDays(-20));
        var oct = Make(202610, 10, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        oct.AdvanceTo(PeriodState.Grace, at.AddDays(-6));
        ProjectBuilder.Attach(project, [sep, oct]);
        project.Activate(at.AddDays(-300));
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);
        var audit = Substitute.For<IAuditWriter>();
        var clock = Substitute.For<Ecr.Domain.Abstractions.IClock>();
        clock.UtcNow.Returns(at);

        // 15/45: жовтень Open до 15.11.
        await new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow, audit, clock)
            .HandleAsync(PolicyId, 0, 15, 45, 45, CancellationToken.None);

        return (sep, oct, audit);
    }

    private Ecr.Application.Periods.BuildPeriodCalendarHandler CalendarHandler(DateTime utcNow)
    {
        var clock = Substitute.For<Ecr.Domain.Abstractions.IClock>();
        clock.UtcNow.Returns(utcNow);
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(AdminId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = AdminId }
                .Permission("Document.View")
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
                .Build());
        return new Ecr.Application.Periods.BuildPeriodCalendarHandler(
            _periods, _uow, clock, access, _user, new Ecr.Application.Periods.PeriodCalendarMaterializer(_periods));
    }

    private Period ReopenedJanuary(DateTime reopenedAt, DateTime until)
    {
        var project = ProjectBuilder.Project(PeriodKind.Monthly, ZoneId, AccessBuilder.ProjectId);
        var jan = Make(202601, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        jan.AdvanceTo(PeriodState.Closed, reopenedAt.AddDays(-1));
        jan.Reopen(until, "Виправлення звіту за січень", reopenedAt);
        ProjectBuilder.Attach(project, [jan]);
        _periods.FindProjectAsync(AccessBuilder.ProjectId, Arg.Any<CancellationToken>()).Returns(project);
        return jan;
    }

    private UpdatePeriodPolicyHandler PolicyHandler(DateTime utcNow)
    {
        var clock = Substitute.For<Ecr.Domain.Abstractions.IClock>();
        clock.UtcNow.Returns(utcNow);
        return new UpdatePeriodPolicyHandler(_periods, _access, _user, _uow, Substitute.For<IAuditWriter>(), clock);
    }
}
