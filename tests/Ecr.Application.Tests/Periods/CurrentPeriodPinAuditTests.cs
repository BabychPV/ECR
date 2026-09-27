// tests/Ecr.Application.Tests/Periods/CurrentPeriodPinAuditTests.cs
using System.Reflection;
using Ecr.Application.Common;
using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// ФВ-1.13: поточний період проєкту — режими <c>Auto</c> і <c>Pinned</c>;
/// <c>Pinned</c> ставить адміністратор <b>з причиною і аудитом</b>.
/// </summary>
/// <remarks>
/// ⚠ Доменна частина (причина обов'язкова, пін переважає задачу) доведена в
/// <c>ProjectCurrentPeriodTests</c>. Тут — те, чого домен не бачить: що дія
/// адміністратора лишає запис у журналі структурних змін із причиною й
/// автором, і що повернення в <c>Auto</c> теж журналюється.
///
/// Мутаційний доказ: у <c>SetCurrentPeriodHandler.HandleAsync</c> прибрати виклик
/// <c>audit.WriteStructureChangeAsync(…)</c> — тест почервоніє на
/// <c>Assert.Single(_written)</c>.
/// </remarks>
public sealed class CurrentPeriodPinAuditTests
{
    private const int AdminId = 7;
    private const int ProjectId = 42;
    private const int PeriodId = 55;
    private static readonly DateTime Now = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly List<StructureChangeRecord> _written = [];
    private readonly Project _project = MakeProject();

    public CurrentPeriodPinAuditTests()
    {
        _user.UserId.Returns(AdminId);
        _user.CorrelationId.Returns("corr-1");
        _clock.UtcNow.Returns(Now);
        _periods.FindProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_project);
        _access.BuildProfileAsync(AdminId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = AdminId }
                .Permission(SetCurrentPeriodHandler.Permission)
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Manage)
                .Build());
        _audit.WriteStructureChangeAsync(Arg.Do<StructureChangeRecord>(_written.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.13")]
    public async Task Фіксація_адміністратором_лишає_Pinned_з_причиною_і_запис_аудиту_а_зняття_повертає_Auto()
    {
        var handler = new SetCurrentPeriodHandler(_periods, _uow, _audit, _access, _user, _clock);

        await handler.HandleAsync(ProjectId, PeriodId, "звірка за листом №17", CancellationToken.None);

        Assert.Equal(CurrentPeriodMode.Pinned, _project.CurrentPeriodMode);
        Assert.Equal(PeriodId, _project.CurrentPeriodId);
        Assert.Equal("звірка за листом №17", _project.CurrentPeriodPinnedReason);
        Assert.Equal(AdminId, _project.CurrentPeriodChangedByUserId);

        var pin = Assert.Single(_written);
        Assert.Equal("Project.CurrentPeriod", pin.EntityType);
        Assert.Equal(ProjectId, pin.EntityId);
        Assert.Equal("Pin", pin.Operation);
        Assert.Equal("звірка за листом №17", pin.ChangeReason);
        Assert.Equal(AdminId, pin.ChangedByUserId);
        Assert.Equal(Now, pin.ChangedAt);
        Assert.Contains($"\"currentPeriodId\":{PeriodId}", pin.NewJson, StringComparison.Ordinal);

        // Повернення в Auto — теж дія адміністратора, і теж у журналі.
        await handler.HandleAsync(ProjectId, pinnedPeriodId: null, reason: null, CancellationToken.None);

        Assert.Equal(CurrentPeriodMode.Auto, _project.CurrentPeriodMode);
        Assert.Null(_project.CurrentPeriodPinnedReason);
        Assert.Equal(2, _written.Count);
        Assert.Equal("Unpin", _written[1].Operation);
        await _uow.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.13")]
    public async Task Фіксація_без_причини_не_змінює_режим_і_не_пише_аудит()
    {
        var handler = new SetCurrentPeriodHandler(_periods, _uow, _audit, _access, _user, _clock);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(ProjectId, PeriodId, "  ", CancellationToken.None));

        Assert.Equal("err.ECR-PRD-0422.pinReasonRequired", error.Details!["messageKey"]);
        Assert.Equal(CurrentPeriodMode.Auto, _project.CurrentPeriodMode);
        Assert.Empty(_written);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    private static Project MakeProject()
    {
        var project = new Project(
            EcrCode.Create("PIN_2026"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Pin" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 3, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(project, ProjectId);

        var period = new Period(ProjectId, new PeriodKey(202602), 2,
                                new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        typeof(Entity<int>).GetProperty("Id")!.SetValue(period, PeriodId);

        var field = typeof(Project).GetField("_periods", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((List<Period>)field.GetValue(project)!).Add(period);

        return project;
    }
}
