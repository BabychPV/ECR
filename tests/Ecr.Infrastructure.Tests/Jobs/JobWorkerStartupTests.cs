// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerStartupTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="JobWorker"/> без бази: непридатна черга (RCSI) і роль процесу (P3).
/// </summary>
/// <remarks>
/// Мутації: пауза після відмови = <c>PollInterval</c> замість <c>RcsiBackoff</c> —
/// перший тест червоний (десятки claim за секунду); Critical на кожну відмову —
/// червоний так само; прибрати <c>UseRole</c> — другий червоний (claim під чужою роллю).
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class JobWorkerStartupTests
{
    [Fact]
    public async Task RCSI_вимкнено_Critical_один_раз_і_наступний_claim_не_раніше_паузи()
    {
        var queue = Substitute.For<IJobQueue>();
        queue.ClaimAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromException<ClaimedJob?>(new InvalidOperationException(
                "Черга задач не захоплює задачі: у базі «x» вимкнено READ_COMMITTED_SNAPSHOT (RCSI).")));

        var logger = new RecordingLogger<JobWorker>();
        var worker = Worker(queue, logger, JobProgressStore.CurrentRole, TimeSpan.FromMilliseconds(400));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(1_000);
        await worker.StopAsync(CancellationToken.None);

        // Без паузи опитування раз на 10 мс дало б ~100 claim; з паузою 400 мс — 1 + ⌊1000/400⌋.
        var claims = queue.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IJobQueue.ClaimAsync));
        Assert.InRange(claims, 1, 4);

        var critical = logger.OfLevel(LogLevel.Critical);
        Assert.Single(critical);
        Assert.Contains("READ_COMMITTED_SNAPSHOT", critical[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Роль_задається_до_першого_claim_чужа_роль_після_фіксації_зупиняє_старт()
    {
        _ = JobProgressStore.CurrentInstanceId; // фіксує роль процесу тестів
        var other = JobProgressStore.Roles.First(r => r != JobProgressStore.CurrentRole);

        var queue = Substitute.For<IJobQueue>();
        var worker = Worker(queue, new RecordingLogger<JobWorker>(), other, TimeSpan.FromMinutes(1));

        // .NET 10: ExecuteAsync іде у фоні — відмова видна в ExecuteTask (хост зупиняється, BackgroundServiceExceptionBehavior.StopHost).
        await worker.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        await worker.StopAsync(CancellationToken.None);
        await queue.DidNotReceiveWithAnyArgs().ClaimAsync(default!, default!, default, default);
        queue.ClearReceivedCalls();

        // Та сама роль, що вже зафіксована, — claim іде від імені ЦЬОГО процесу.
        // ⚠ Чекаємо сам claim, а не фіксовані 200 мс: у .NET 10 ExecuteAsync стартує
        // через пул потоків, і під навантаженням CI (run 36609214718) StopAsync
        // встигав скасувати цикл раніше за перший claim — «received no matching calls».
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.ClaimAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                claimed.TrySetResult();
                return Task.FromResult<ClaimedJob?>(null);
            });

        var same = Worker(queue, new RecordingLogger<JobWorker>(), JobProgressStore.CurrentRole, TimeSpan.FromMinutes(1));
        await same.StartAsync(CancellationToken.None);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await same.StopAsync(CancellationToken.None);

        await queue.Received().ClaimAsync(
            Arg.Any<IReadOnlyCollection<string>>(), JobProgressStore.CurrentInstanceId,
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    private static JobWorker Worker(IJobQueue queue, RecordingLogger<JobWorker> logger, string role, TimeSpan rcsiBackoff)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => queue);
        var provider = services.BuildServiceProvider();

        return new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = role,
                PollInterval = TimeSpan.FromMilliseconds(10),
                RcsiBackoff = rcsiBackoff,
            },
            new JobQueueSignal(),
            logger);
    }
}
