// tests/Ecr.Application.Tests/Integration/JobStatusQueuePositionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// ✎ PERF-10 (<c>D-564c</c>): стан задачі, що ЧЕКАЄ в черзі, несе місце й орієнтовний старт; задача в іншому стані — ні (і
/// черга не питається).
/// </summary>
/// <remarks>МУТАЦІЯ: прибрати умову <c>State == "Queued"</c> чи заповнення полів у <c>GetJobStatusHandler</c> — червоний.</remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait("Finding", "PERF-10")]
public sealed class JobStatusQueuePositionTests
{
    private static readonly DateTime Start = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Queued", true)]
    [InlineData("Running", false)]
    [InlineData("Succeeded", false)]
    public async Task Місце_в_черзі_лише_для_задачі_що_чекає(string state, bool positioned)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.GetStatusAsync("j1", Arg.Any<CancellationToken>())
            .Returns(new JobStatus("j1", state, 0, null, null, CreatedByUserId: 9));
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }.Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns("en");
        var position = Substitute.For<IJobQueuePosition>();
        position.GetAsync("j1", Arg.Any<CancellationToken>()).Returns(new JobQueuePosition(3, Start));

        var status = await new GetJobStatusHandler(jobs, access, user, Substitute.For<IUiStringCatalog>(), queuePosition: position)
            .HandleAsync("j1", CancellationToken.None);

        Assert.Equal(positioned ? (3, Start) : (null, null), (status!.QueuePosition, status.EstimatedStartAt));
        await position.Received(positioned ? 1 : 0).GetAsync("j1", Arg.Any<CancellationToken>());
    }
}
