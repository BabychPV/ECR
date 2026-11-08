using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// UAT recalc-progress (08.11): звуження переліку задач за проєктом, батьком розкладу, видом — і похідний стан
/// батька-розкладу в переліку.
/// </summary>
/// <remarks>
/// Мутації: (1) не передавати <c>scope.ProjectId</c> у <c>JobListFilter</c> — червоніє перший тест; (2) прибрати
/// перевірку <c>JobListKinds.All</c> — другий; (3) брати <c>EffectiveState</c> лише з <c>JobCompletionWarning</c> —
/// третій (батько розкладу з дітьми в черзі читався «Succeeded»).
/// </remarks>
public sealed class ListJobsScopeTests
{
    private const int UserId = 7;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListJobsScopeTests()
    {
        _user.UserId.Returns(UserId);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Build());
        _jobs.ListRecentAsync(Arg.Any<JobListFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<JobSummary>());
    }

    private ListJobsHandler Handler() => new(_jobs, _access, _user, new FakeUiStringCatalog());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Звуження_доходить_до_сховища_разом_із_автором_із_сеансу()
    {
        await Handler().HandleAsync(
            null, null, mine: true, 1, hideRoutine: false,
            new JobListScope(ProjectId: 42, ParentJobId: "P-1", HideChildren: true, Kind: JobListKinds.ProjectRecalculation),
            CancellationToken.None);

        await _jobs.Received(1).ListRecentAsync(
            Arg.Is<JobListFilter>(f =>
                f.CreatedByUserId == UserId && f.ProjectId == 42 && f.ParentJobId == "P-1" && f.HideChildren
                && f.Kind == JobListKinds.ProjectRecalculation),
            1,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невідомий_вид_задачі_422_а_не_порожній_перелік()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            null, null, mine: true, null, hideRoutine: false, new JobListScope(Kind: "projectRecalc"),
            CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.jobKind", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Батько_розкладу_з_дітьми_в_роботі_в_переліку_FannedOut_а_не_Succeeded()
    {
        _jobs.ListRecentAsync(Arg.Any<JobListFilter>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new JobSummary(
                    "IRecalculationJob~project42-year~x", JobListKinds.RecalculationJobCode, "Succeeded", 100,
                    DateTime.UtcNow, DateTime.UtcNow, ProjectId: 42,
                    FanOut: new FanOutStatus(92, 80, 4, 8, 0)),
            });

        var list = await Handler().HandleAsync(
            null, null, mine: true, null, hideRoutine: false, JobListScope.None, CancellationToken.None);

        Assert.Equal(FanOutStatus.StateFannedOut, Assert.Single(list).EffectiveState);
    }
}
