// tests/Ecr.Infrastructure.Tests/Jobs/RestartCoveredByTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-11 (аудит 2026-10-03): у режимі <c>Database</c> відмова перезапуску через задачу,
/// що вже чекає на ту саму ціль, не показується як «деталі не пережили перезапуск сервера».
/// </summary>
/// <remarks>Мутаційні докази — в описі коміту.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RestartCoveredByTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Перезапуск_провалу_за_наявної_Queued_на_ціль_називає_ту_що_чекає()
    {
        var (failed, waiting) = await FailedWithQueuedBehindAsync();
        await using var host = NewHost();

        var outcome = await host.Queue.RestartAsync(failed, CancellationToken.None);

        Assert.False(outcome.IsRestarted);
        Assert.Equal(waiting, outcome.CoveringJobId);
        Assert.Equal("Failed", (await RowAsync(failed))!.State);
    }

    [Fact]
    public async Task Планувальник_відмовляє_409_restartCoveredBy_а_не_йде_в_Quartz()
    {
        var (failed, waiting) = await FailedWithQueuedBehindAsync();
        await using var host = NewHost();
        var scheduler = new DbBackgroundJobScheduler(host.Queue, new QuartzJobScheduler(), new JobQueueSignal());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => scheduler.RestartAsync(failed, CancellationToken.None));

        Assert.Equal(ErrorCodes.JobStateConflict, error.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.restartCoveredBy", error.Details!["messageKey"]);
        Assert.Equal(waiting, error.Details["coveredBy"]);
        Assert.Equal(failed, error.Details["jobId"]);
    }

    [Fact]
    public async Task Перезапуск_провалу_без_черги_на_ціль_як_і_раніше_перезапускає()
    {
        var target = Target();
        var failed = await EnqueueAsync(target);
        await using var host = NewHost();
        var claim = (await host.ClaimAsync())!.Claim;
        Assert.True(await host.Queue.FailAsync(claim, "причина", "ECR-SYS-0500", CancellationToken.None));

        Assert.Equal(JobRestartOutcome.Restarted, await host.Queue.RestartAsync(failed, CancellationToken.None));
        Assert.Equal("Queued", (await RowAsync(failed))!.State);
    }

    /// <summary>Провалена задача на ціль і нова <c>Queued</c> на ту саму ціль позаду.</summary>
    private async Task<(string Failed, string Waiting)> FailedWithQueuedBehindAsync()
    {
        var target = Target();
        var failed = await EnqueueAsync(target);
        await using (var host = NewHost())
        {
            var claim = (await host.ClaimAsync())!.Claim;
            Assert.True(await host.Queue.FailAsync(claim, "причина", "ECR-SYS-0500", CancellationToken.None));
        }

        var waiting = await EnqueueAsync(target);
        Assert.NotEqual(failed, waiting);
        return (failed, waiting);
    }
}
