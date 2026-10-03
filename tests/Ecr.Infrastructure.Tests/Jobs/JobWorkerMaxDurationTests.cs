// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerMaxDurationTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="JobWorkerOptions.MaxDuration"/> (ФВ-9.8, I1, <c>Jobs:Workers:MaxDuration</c>):
/// задача, довша за межу, — <c>Failed</c> з ключем причини, а виконавець живий і
/// бере наступну.
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>overtime.CancelAfter(maxDuration)</c> у <see cref="JobWorker"/> —
/// задача лишається <c>Running</c>, тест червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class JobWorkerMaxDurationTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Задача_довша_за_межу_Failed_з_ключем_причини_і_без_ретраю_а_воркер_бере_наступну()
    {
        var probe = new WorkerProbe();
        await using var provider = BuildHost(probe);
        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                MaxConcurrency = 1,
                PollInterval = TimeSpan.FromMilliseconds(50),
                RenewInterval = TimeSpan.FromMilliseconds(100),
                MaxDuration = TimeSpan.FromMilliseconds(700),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var slow = await EnqueueJobAsync<WorkerBlockingJob>(provider);
            await probe.Started.Task.WaitAsync(Patience);

            var row = await WaitForStateAsync(slow, "Failed");

            // ⛔ Причина — ключ каталогу з межею, а не «впало невідомо чому».
            Assert.True(JobProgressMessageCodec.TryDecode(row.Message, out var envelope), row.Message);
            Assert.Equal(JobWorkerOptions.MaxDurationKey, envelope.Key);
            Assert.Equal("00:00:00.7000000", envelope.Params!["limit"]);
            Assert.Contains("00:00:00.7000000", row.Error, StringComparison.Ordinal);
            Assert.Equal(1, row.Attempt);
            Assert.True(await probe.Cancelled.Task.WaitAsync(Patience), "задачу не скасовано токеном");

            // Процес (воркер) живий: наступну задачу бере той самий виконавець з одним місцем.
            var next = await EnqueueJobAsync<WorkerProbeJob>(provider);
            await WaitForStateAsync(next, "Succeeded");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    [Trait("Finding", "L2-03")]
    public async Task Задача_що_ігнорує_токен_після_межі_і_пільги_втрачає_оренду()
    {
        var probe = new WorkerProbe();
        var hung = new HangGate();
        await using var provider = BuildHost(probe, hung);
        var onHang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                MaxConcurrency = 1,
                PollInterval = TimeSpan.FromMilliseconds(50),
                Lease = TimeSpan.FromSeconds(1),
                RenewInterval = TimeSpan.FromMilliseconds(100),
                MaxDuration = TimeSpan.FromMilliseconds(700),
                HangGrace = TimeSpan.FromMilliseconds(500),
                OnHang = () => onHang.TrySetResult(),
            },
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var jobId = await EnqueueJobAsync<WorkerHungJob>(provider);
            await hung.Started.Task.WaitAsync(Patience);
            await onHang.Task.WaitAsync(Patience);

            // ⛔ Оренду кинуто: інший процес переклеймлює рядок, як після падіння.
            // Без фіксу подовження тримає оренду, поки задача не повернеться, — тобто ніколи.
            await using var other = NewHost();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ClaimedJob? reclaimed;
            while ((reclaimed = await other.ClaimAsync("host/other")) is null)
            {
                Assert.True(clock.Elapsed < Patience, $"{jobId}: оренду досі тримає зависла задача.");
                await Task.Delay(100);
            }

            Assert.Equal(jobId, reclaimed.Claim.JobId);
            Assert.Equal(1, (await RowAsync(jobId))!.ReclaimCount);
        }
        finally
        {
            hung.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static async Task<string> EnqueueJobAsync<TJob>(ServiceProvider host)
        where TJob : IBackgroundJob
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>()
            .EnqueueAsync<TJob>(new { n = 1 }, CancellationToken.None);
    }

    private async Task<Domain.Entities.Integration.JobProgress> WaitForStateAsync(string jobId, string state)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
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

    private ServiceProvider BuildHost(WorkerProbe probe, HangGate? hung = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped(sp => new QuartzJobScheduler(null, sp.GetService<IJobProgressStore>(), sp.GetService<IClock>()));
        services.AddScoped<DbBackgroundJobScheduler>();
        services.AddSingleton(probe);
        services.AddScoped<WorkerProbeJob>();
        services.AddScoped<WorkerBlockingJob>();
        services.AddSingleton(hung ?? new HangGate());
        services.AddScoped<WorkerHungJob>();
        return services.BuildServiceProvider();
    }
}

/// <summary>Бар'єр задачі, що не стежить за токеном.</summary>
public sealed class HangGate
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Процесорна фаза, що токена не перевіряє (L2-03): чекає бар'єра, а не скасування.</summary>
public sealed class WorkerHungJob(HangGate gate) : IBackgroundJob
{
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        gate.Started.TrySetResult();
        await gate.Release.Task.ConfigureAwait(false);
    }
}
