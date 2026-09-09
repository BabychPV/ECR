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
/// Q-156: автор власної задачі читає її стан без <c>System.ViewHealth</c>;
/// те право лишається обов'язковим лише для ЧУЖИХ задач.
/// </summary>
public sealed class GetJobStatusHandlerTests
{
    private const string JobId = "job-1";
    private const int Author = 9;
    private const int Stranger = 10;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public GetJobStatusHandlerTests()
    {
        _jobs.GetStatusAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new JobStatus(JobId, "Succeeded", 100, "export-abc", null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Автор_читає_стан_власної_задачі_без_ViewHealth()
    {
        _user.UserId.Returns(Author);
        _access.BuildProfileAsync(Author, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Author }.Build());
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var status = await Handler().HandleAsync(JobId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Succeeded", status!.State);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Чужий_без_ViewHealth_відхиляється()
    {
        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Stranger }.Build());
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(JobId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task З_ViewHealth_чужа_задача_видима()
    {
        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Stranger }.Permission(GetJobStatusHandler.Permission).Build());
        _jobs.GetCreatedByUserIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(Author);

        var status = await Handler().HandleAsync(JobId, CancellationToken.None);

        Assert.NotNull(status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Невідома_задача_дає_404_а_не_403_без_права()
    {
        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Stranger }.Build());
        _jobs.GetStatusAsync("unknown", Arg.Any<CancellationToken>())
            .Returns(new JobStatus("unknown", "Unknown", 0, null, null));

        var status = await Handler().HandleAsync("unknown", CancellationToken.None);

        Assert.Null(status);
        await _jobs.DidNotReceive().GetCreatedByUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private GetJobStatusHandler Handler() => new(_jobs, _access, _user);
}
