// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerSettleTests.cs
using System.Data.Common;
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-07 (аудит 2026-10-03): виняток поза <c>try</c> у <see cref="JobWorker"/> — резолв
/// задачі з DI і фіксація результату — не лишає рядок <c>Running</c> до спливу оренди.
/// </summary>
/// <remarks>Мутаційні докази — в описі коміту.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class JobWorkerSettleTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Транзієнтний_збій_Complete_не_веде_до_повторного_виконання()
    {
        var probe = new WorkerProbe();
        var faults = new QueueFaults { CompleteFailures = 1 };
        await using var host = await StartHostAsync(probe, faults);

        var jobId = await EnqueueJobAsync<WorkerProbeJob>(host, new { n = 1 });

        var row = await WaitForStateAsync(jobId, "Succeeded");

        Assert.Single(probe.Runs);
        Assert.Equal(1, row.Attempt);
        Assert.Equal(2, faults.CompleteCalls);
    }

    [Fact]
    public async Task Задача_що_не_резолвиться_з_DI_одразу_Failed_без_переклеймів()
    {
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe, new QueueFaults());

        var jobId = await EnqueueJobAsync<WorkerUnresolvableJob>(host, new { n = 1 });

        var row = await WaitForStateAsync(jobId, "Failed");

        Assert.Equal(ErrorCodes.Internal, row.ErrorCode);
        Assert.Equal(0, row.ReclaimCount ?? 0);
        Assert.Contains("DI", row.Error, StringComparison.Ordinal);
    }

    private static async Task<string> EnqueueJobAsync<TJob>(ServiceProvider host, object payload)
        where TJob : IBackgroundJob
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>()
            .EnqueueAsync<TJob>(payload, CancellationToken.None);
    }

    private async Task<JobProgress> WaitForStateAsync(string jobId, string state)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var row = await RowAsync(jobId);
            if (row?.State == state)
            {
                return row;
            }

            Assert.True(clock.Elapsed < Patience, $"{jobId}: очікувався {state}, стан {row?.State ?? "—"}.");
            await Task.Delay(50);
        }
    }

    private async Task<WorkerHost> StartHostAsync(WorkerProbe probe, QueueFaults faults)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(faults);
        services.AddScoped<IJobQueue>(sp => new FlakyQueue(
            new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()),
            sp.GetRequiredService<QueueFaults>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped(sp => new QuartzJobScheduler(null, sp.GetService<IJobProgressStore>(), sp.GetService<IClock>()));
        services.AddScoped<DbBackgroundJobScheduler>();
        services.AddSingleton(probe);
        services.AddScoped<WorkerProbeJob>();
        services.AddScoped<WorkerUnresolvableJob>();
        var provider = services.BuildServiceProvider();

        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                PollInterval = TimeSpan.FromMilliseconds(50),
                RenewInterval = TimeSpan.FromMilliseconds(100),
                RetryDelay = n => TimeSpan.FromMilliseconds(50 * n),
                SettleRetryDelay = _ => TimeSpan.FromMilliseconds(50),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        return new WorkerHost(provider, worker);
    }

    private sealed class WorkerHost(ServiceProvider provider, JobWorker worker) : IAsyncDisposable
    {
        public static implicit operator ServiceProvider(WorkerHost host) => host.Provider;

        public ServiceProvider Provider { get; } = provider;

        public async ValueTask DisposeAsync()
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await Provider.DisposeAsync();
        }
    }

    /// <summary>Скільки разів зірвати фіксацію і скільки викликів було.</summary>
    public sealed class QueueFaults
    {
        private int completeCalls;

        public int CompleteFailures { get; init; }

        public int CompleteCalls => Volatile.Read(ref completeCalls);

        public bool NextCompleteFails() => Interlocked.Increment(ref completeCalls) <= CompleteFailures;
    }

    /// <summary>Як обрив з'єднання посеред COMMIT: драйвер кидає <see cref="DbException"/>.</summary>
    private sealed class TransientDbException(string message) : DbException(message);

    /// <summary>Черга, що зриває <see cref="IJobQueue.CompleteAsync"/> ДО запису.</summary>
    private sealed class FlakyQueue(IJobQueue inner, QueueFaults faults) : IJobQueue
    {
        public Task<JobEnqueueResult> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct)
            => inner.EnqueueAsync(request, ct);

        public Task<ClaimedJob?> ClaimAsync(IReadOnlyCollection<string> lanes, string owner, TimeSpan lease, CancellationToken ct)
            => inner.ClaimAsync(lanes, owner, lease, ct);

        public Task<LeaseState> RenewAsync(JobClaimToken claim, TimeSpan lease, CancellationToken ct)
            => inner.RenewAsync(claim, lease, ct);

        public Task<bool> FenceAsync(JobClaimToken claim, CancellationToken ct) => inner.FenceAsync(claim, ct);

        public Task<bool> CompleteAsync(JobClaimToken claim, CancellationToken ct)
            => faults.NextCompleteFails()
                ? throw new TransientDbException("A transport-level error has occurred.")
                : inner.CompleteAsync(claim, ct);

        public Task<bool> FailAsync(JobClaimToken claim, string reason, string? errorCode, CancellationToken ct)
            => inner.FailAsync(claim, reason, errorCode, ct);

        public Task<bool> RequeueAsync(JobClaimToken claim, TimeSpan delay, CancellationToken ct)
            => inner.RequeueAsync(claim, delay, ct);

        public Task<bool> DeferAsync(JobClaimToken claim, TimeSpan delay, CancellationToken ct)
            => inner.DeferAsync(claim, delay, ct);

        public Task<bool> ReleaseAsync(JobClaimToken claim, CancellationToken ct) => inner.ReleaseAsync(claim, ct);

        public Task<bool> AcknowledgeCancelAsync(JobClaimToken claim, CancellationToken ct)
            => inner.AcknowledgeCancelAsync(claim, ct);

        public Task<CancelOutcome> RequestCancelAsync(string jobId, CancellationToken ct)
            => inner.RequestCancelAsync(jobId, ct);

        public Task<bool> IsCancelRequestedAsync(string jobId, CancellationToken ct)
            => inner.IsCancelRequestedAsync(jobId, ct);

        public Task<JobRestartOutcome> RestartAsync(string jobId, CancellationToken ct) => inner.RestartAsync(jobId, ct);

        public Task<int> ExpireAsync(int maxReclaims, CancellationToken ct) => inner.ExpireAsync(maxReclaims, ct);
    }
}

/// <summary>Задача з залежністю, якої немає в DI: резолв кидає.</summary>
public sealed class WorkerUnresolvableJob(WorkerUnresolvableJob.IMissing missing) : IBackgroundJob
{
    /// <summary>Залежність, яку ніхто не зареєстрував.</summary>
    public interface IMissing
    {
    }

    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        GC.KeepAlive(missing);
        return Task.CompletedTask;
    }
}
