// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerStartLatencyTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="JobWorker"/> передає затримку «готова → почала виконуватись» у
/// <see cref="IJobStartMetrics"/> (<c>ФВ-12.2</c>, <c>НФ-8.3.1</c>): значення — з claim
/// (<see cref="ClaimedJob.QueueWaitMs"/>), теги — код задачі й лейн.
/// </summary>
/// <remarks>
/// Без бази й без годинників: черга — підробка, значення затримки приходить готовим,
/// синхронізація — через <see cref="TaskCompletionSource"/>, а не <c>Task.Delay</c>.
/// Мутації: прибрати виклик метрики у <c>JobWorker</c> — 1-й і 3-й червоні; викинути
/// перевірку <c>QueueWaitMs is null</c> (метрика на переклейм) — 2-й червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class JobWorkerStartLatencyTests
{
    private static readonly string JobCode = typeof(LatencyProbeJob).FullName!;

    [Fact]
    public async Task Затримка_з_claim_доходить_до_метрики_з_кодом_задачі_і_лейном()
    {
        var (metrics, done) = await RunOneAsync(queueWaitMs: 1234, lane: JobLanes.Default);

        metrics.Received(1).RecordStartLatency(1234d, JobCode, JobLanes.Default);
        Assert.True(done.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Переклейм_без_значення_затримки_метрику_не_пише_а_задачу_виконує()
    {
        var (metrics, done) = await RunOneAsync(queueWaitMs: null, lane: JobLanes.Default);

        Assert.True(done.IsCompletedSuccessfully); // задача виконалась...
        metrics.DidNotReceiveWithAnyArgs().RecordStartLatency(default, default!, default); // ...а нуль у гістограму не пішов
    }

    [Fact]
    public async Task Лейн_у_тегу_той_що_у_claim_а_не_типовий()
    {
        var other = JobLanes.All.First(l => l != JobLanes.Default);
        var (metrics, _) = await RunOneAsync(queueWaitMs: 7, lane: other);

        metrics.Received(1).RecordStartLatency(7d, JobCode, other);
        metrics.DidNotReceive().RecordStartLatency(7d, JobCode, JobLanes.Default);
    }

    private static async Task<(IJobStartMetrics Metrics, Task Done)> RunOneAsync(long? queueWaitMs, string lane)
    {
        var metrics = Substitute.For<IJobStartMetrics>();
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var claimed = new ClaimedJob(
            new JobClaimToken("latency-1", Guid.NewGuid()), JobCode, lane, null, null, 1, 0,
            Reclaimed: queueWaitMs is null, DateTime.UtcNow.AddMinutes(5), null, null, null, queueWaitMs);

        var queue = Substitute.For<IJobQueue>();
        var handedOut = 0;
        queue.ClaimAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            Task.FromResult(Interlocked.Increment(ref handedOut) == 1 ? claimed : null));
        queue.RenewAsync(default!, default, default).ReturnsForAnyArgs(Task.FromResult(LeaseState.Held));
        queue.CompleteAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            settled.TrySetResult();
            return Task.FromResult(true);
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => queue);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(metrics);
        services.AddSingleton(executed);
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<LatencyProbeJob>();
        await using var provider = services.BuildServiceProvider();

        using var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                PollInterval = TimeSpan.FromMilliseconds(10),
            },
            new JobQueueSignal(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync(CancellationToken.None);

        return (metrics, executed.Task);
    }

    /// <summary>Порожня задача: предмет — чекання перед нею.</summary>
    public sealed class LatencyProbeJob(TaskCompletionSource executed) : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            executed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
