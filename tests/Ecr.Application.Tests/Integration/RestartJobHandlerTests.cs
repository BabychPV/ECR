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
/// Директива №11, T10 #40: ручний перезапуск проваленої задачі — до цього
/// обробника автоматичний ретрай (<c>QuartzJobAdapter</c>) зупинявся на межі
/// спроб НАЗАВЖДИ, і полагоджену причину (недоступне джерело, зайняте
/// з'єднання) не можна було сказати системі спробувати ще раз без постановки
/// нової задачі й втрати історії прогресу.
/// </summary>
public sealed class RestartJobHandlerTests
{
    private const string JobId = "job-failed-1";
    private const int Viewer = 11;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RestartJobHandlerTests()
    {
        _user.UserId.Returns(Viewer);
        _access.BuildProfileAsync(Viewer, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Viewer }.Permission(GetJobStatusHandler.Permission).Build());
    }

    private RestartJobHandler Handler() => new(_jobs, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Провалену_задачу_можна_перезапустити()
    {
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Failed", 0, null, "Симуляція транзієнтної помилки."));
        _jobs.RestartAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);

        await Handler().HandleAsync(JobId, CancellationToken.None);

        await _jobs.Received(1).RestartAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Задачу_що_виконується_перезапустити_не_можна()
    {
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Running", 40, null, null));

        var denied = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        Assert.Equal(RestartJobHandler.NotFailedErrorCode, denied.ErrorCode);
        await _jobs.DidNotReceiveWithAnyArgs().RestartAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Невідому_задачу_перезапустити_не_можна()
    {
        _jobs.GetStatusAsync("unknown", Arg.Any<CancellationToken>())
            .Returns(new JobStatus("unknown", "Unknown", 0, null, null));

        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync("unknown", CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Деталь_задачі_що_не_пережила_перезапуск_сервера_дає_404()
    {
        // ⚠ Стан у базі — Failed (itg.JobProgress персистентний), але
        // деталі в Quartz УЖЕ немає: сховище чергИ в пам'яті (D-66) і не
        // пережило перезапуск процесу між провалом і спробою відновлення.
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Failed", 0, null, "щось пішло не так"));
        _jobs.RestartAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Без_ViewHealth_перезапуск_відхиляється()
    {
        _user.UserId.Returns(Viewer);
        _access.BuildProfileAsync(Viewer, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Viewer }.Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        await _jobs.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default);
    }
}
