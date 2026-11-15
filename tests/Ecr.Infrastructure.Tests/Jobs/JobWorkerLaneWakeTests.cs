// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerLaneWakeTests.cs
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
/// B85: окремий цикл воркера (<see cref="JobWorkerOptions.SeparateLanes"/>, у Api — <c>excel</c>) прокидається на
/// постановку в свій лейн, а не спить <see cref="JobWorkerOptions.PollInterval"/> (замір B85: старт задачі excel
/// 150–1100 мс при claim ≈ 5 мс).
/// </summary>
/// <remarks>
/// Без годинників: <c>PollInterval</c> = 1 година, тож задачу може запустити ЛИШЕ сигнал; синхронізація —
/// <see cref="TaskCompletionSource"/>; межа 30 с — лише запобіжник від зависання, не вимір.
/// Мутації: повернути <c>Task.Delay(PollInterval)</c> в окремий цикл <c>JobWorker.PollAsync</c> — 1-й червоний
/// (таймаут); у <c>JobQueueSignal.Notify(lane)</c> будити всі підписки — 3-й червоний; не будити жодної — 1-й і 2-й; не писати
/// в спільний канал у <c>Notify(lane)</c> — 2-й червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class JobWorkerLaneWakeTests
{
    private static readonly string JobCode = typeof(LaneWakeProbeJob).FullName!;

    [Fact]
    public async Task Постановка_в_лейн_окремого_циклу_будить_його_без_очікування_опитування()
    {
        var signal = new JobQueueSignal();
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var available = 0;
        var handedOut = 0;

        var claimed = new ClaimedJob(
            new JobClaimToken("lane-wake-1", Guid.NewGuid()), JobCode, JobLanes.Excel, null, null, 1, 0,
            Reclaimed: false, DateTime.UtcNow.AddMinutes(5), null, null, null, 0);

        var queue = Substitute.For<IJobQueue>();
        queue.ClaimAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var lanes = call.ArgAt<IReadOnlyCollection<string>>(0);
            if (!lanes.Contains(JobLanes.Excel))
            {
                return Task.FromResult<ClaimedJob?>(null);
            }

            var ready = Volatile.Read(ref available) == 1 && Interlocked.Exchange(ref handedOut, 1) == 0;
            polled.TrySetResult();
            return Task.FromResult(ready ? claimed : null);
        });
        queue.RenewAsync(default!, default, default).ReturnsForAnyArgs(Task.FromResult(LeaseState.Held));
        queue.CompleteAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            settled.TrySetResult();
            return Task.FromResult(true);
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => queue);
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<LaneWakeProbeJob>();
        await using var provider = services.BuildServiceProvider();

        using var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                SeparateLanes = [JobLanes.Excel],
                SeparateConcurrency = 1,
                PollInterval = TimeSpan.FromHours(1),
            },
            signal,
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Окремий цикл уже раз опитав порожній лейн і йде в очікування; задача з'являється ПІСЛЯ цього.
            await polled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Volatile.Write(ref available, 1);
            signal.Notify(JobLanes.Excel);

            await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Сигнал_лейна_будить_підписку_на_нього()
    {
        var signal = new JobQueueSignal();
        using var waiter = signal.Subscribe([JobLanes.Excel]);
        using var cts = new CancellationTokenSource();

        signal.Notify(JobLanes.Excel);
        var wait = waiter.WaitAsync(TimeSpan.FromHours(1), cts.Token);

        // Сигнал уже в каналі підписки: очікування завершується синхронно, без годинника.
        Assert.True(wait.IsCompletedSuccessfully);
        await wait;

        // Сигнал лейна будить і ОСНОВНИЙ цикл (спільний канал): там лейн теж може бути (interactive).
        Assert.True(signal.WaitAsync(TimeSpan.FromHours(1), cts.Token).IsCompletedSuccessfully);

        signal.Notify();
        Assert.True(waiter.WaitAsync(TimeSpan.FromHours(1), cts.Token).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Сигнал_чужого_лейна_і_після_відписки_не_будить()
    {
        var signal = new JobQueueSignal();
        var waiter = signal.Subscribe([JobLanes.Excel]);
        using var cts = new CancellationTokenSource();

        signal.Notify(JobLanes.Default);
        var foreign = waiter.WaitAsync(TimeSpan.FromHours(1), cts.Token);
        Assert.False(foreign.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreign);

        waiter.Dispose();
        signal.Notify(JobLanes.Excel);
        using var cts2 = new CancellationTokenSource();
        var afterDispose = waiter.WaitAsync(TimeSpan.FromHours(1), cts2.Token);
        Assert.False(afterDispose.IsCompleted);
        await cts2.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afterDispose);
    }

    /// <summary>Порожня задача: предмет — момент її старту.</summary>
    public sealed class LaneWakeProbeJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
    }
}
