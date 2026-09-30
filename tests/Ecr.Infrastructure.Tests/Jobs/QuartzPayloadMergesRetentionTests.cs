// tests/Ecr.Infrastructure.Tests/Jobs/QuartzPayloadMergesRetentionTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Огляд O1, косметика: тіла задач злиття Quartz, що впали остаточно, не живуть у пам'яті
/// процесу вічно — понад <see cref="QuartzJobScheduler.MaxFailedMergeBodies"/> найстаріше
/// забувається (перезапуск тоді бере payload першої постановки з <c>JobDataMap</c>).
/// </summary>
/// <remarks>Мутація: без <c>QuartzPayloadMerges.MarkFailed</c> в адаптері — найстаріша
/// задача при перезапуску несе злите тіло, червоний.</remarks>
public sealed class QuartzPayloadMergesRetentionTests
{
    private const string Fallback = """{"fallback":true,"cells":[]}""";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Понад_межу_Failed_найстаріше_тіло_забувається_новіше_лишається()
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.SchedulerName.Returns($"retention-{Guid.NewGuid():N}");
        scheduler.ListenerManager.GetJobListeners().Returns([]);
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);
        var jobs = new QuartzJobScheduler(factory);

        var job = new FailingFormulaJob();
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton<IFormulaRecalculationJob>(job);
        await using var provider = services.BuildServiceProvider();

        var ids = new List<string>();
        for (var i = 0; i <= QuartzJobScheduler.MaxFailedMergeBodies; i++)
        {
            var id = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                $"doc{i}-p202601-formula-u1", new { Cells = new[] { new { RowId = i, ColumnDefId = 7 } } }, CancellationToken.None);
            ids.Add(id);
            Assert.Contains($"\"rowId\":{i},", await RunAsync(provider, scheduler, id, job), StringComparison.Ordinal);
        }

        // Найстаріше тіло забуто — перезапуск бере payload з JobDataMap; найновіше — на місці.
        Assert.Equal(Fallback, await RunAsync(provider, scheduler, ids[0], job));
        Assert.Contains(
            $"\"rowId\":{QuartzJobScheduler.MaxFailedMergeBodies},",
            await RunAsync(provider, scheduler, ids[^1], job),
            StringComparison.Ordinal);
    }

    /// <summary>Остаточний провал (ретраї вичерпано) задачі злиття; повертає payload прогону.</summary>
    private static async Task<string?> RunAsync(IServiceProvider provider, IScheduler scheduler, string jobId, FailingFormulaJob job)
    {
        var detail = Substitute.For<IJobDetail>();
        detail.Key.Returns(new JobKey(jobId));
        detail.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(IFormulaRecalculationJob).FullName! },
            { QuartzJobScheduler.PayloadKey, Fallback },
        });
        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap
        {
            { QuartzJobScheduler.RetryAttemptKey, QuartzJobAdapter.MaxRetryAttempts.ToString(CultureInfo.InvariantCulture) },
        });
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);

        await Assert.ThrowsAsync<JobExecutionException>(
            () => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context));

        return job.Last;
    }

    private sealed class FailingFormulaJob : IFormulaRecalculationJob
    {
        public string? Last { get; private set; }

        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Last = payload as string;
            throw new InvalidOperationException("провал");
        }
    }
}
