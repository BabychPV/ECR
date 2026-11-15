// tests/Ecr.Infrastructure.Tests/Jobs/JobSignalAfterCommitTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B125: постановка через <see cref="DbBackgroundJobScheduler"/> у транзакції викликача будить воркер ПІСЛЯ коміту, а
/// при відкаті (чи звільненні транзакції без коміту) — не будить; без транзакції — одразу, як і раніше.
/// </summary>
/// <remarks>
/// Без годинників: «сигнал уже є» перевіряється синхронно — очікування на каналі зі значенням завершується в момент
/// виклику (<c>IsCompleted</c>), на порожньому — ні; межа 30 с лише запобіжник від зависання.
/// Мутації: <c>NotifyAfterCommit</c> будить одразу — 1-й червоний (сигнал до коміту); <c>Release(.., notify: true)</c> на
/// відкаті — 2-й червоний; <c>NotifyAfterCommit</c> у транзакції нічого не відкладає — 1-й червоний (сигналу немає).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class JobSignalAfterCommitTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan Forever = TimeSpan.FromHours(1);

    [Fact]
    public async Task Сигнал_у_транзакції_не_приходить_до_коміту_і_приходить_після()
    {
        var signal = new JobQueueSignal();
        using var lane = signal.Subscribe([JobLaneMap.Of<SigProbeJob>()]);
        using var cts = new CancellationTokenSource();
        await using var db = Sql.CreateContext(signal);
        var scheduler = Scheduler(db, signal);

        Task primary = Task.CompletedTask, own = Task.CompletedTask;
        await new UnitOfWork(db, new SystemClock()).ExecuteInTransactionAsync(
            async ct =>
            {
                await scheduler.EnqueueAsync<SigProbeJob>(new { n = 1 }, ct);

                // До коміту рядка не видно — і сигналу ще немає ні основному циклу, ні підписці лейна.
                primary = signal.WaitAsync(Forever, cts.Token);
                own = lane.WaitAsync(Forever, cts.Token);
                Assert.False(primary.IsCompleted, "основний цикл розбуджено до коміту");
                Assert.False(own.IsCompleted, "підписку лейна розбуджено до коміту");
            },
            CancellationToken.None);

        // Після коміту обидва прокидаються (без годинника: очікування, почате до коміту, завершує саме сигнал).
        await primary.WaitAsync(TimeSpan.FromSeconds(30));
        await own.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Відкат_і_транзакція_без_коміту_сигналу_не_дають()
    {
        var signal = new JobQueueSignal();
        using var lane = signal.Subscribe([JobLaneMap.Of<SigProbeJob>()]);
        await using var db = Sql.CreateContext(signal);
        var scheduler = Scheduler(db, signal);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await scheduler.EnqueueAsync<SigProbeJob>(new { n = 2 }, CancellationToken.None);
            await tx.RollbackAsync();
        }

        await using (await db.Database.BeginTransactionAsync())
        {
            await scheduler.EnqueueAsync<SigProbeJob>(new { n = 3 }, CancellationToken.None);
        }

        await AssertSilentAsync(signal, lane);
    }

    [Fact]
    public async Task Без_транзакції_сигнал_одразу()
    {
        var signal = new JobQueueSignal();
        using var lane = signal.Subscribe([JobLaneMap.Of<SigProbeJob>()]);
        await using var db = Sql.CreateContext(signal);

        await Scheduler(db, signal).EnqueueAsync<SigProbeJob>(new { n = 4 }, CancellationToken.None);

        Assert.True(signal.WaitAsync(Forever, CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(lane.WaitAsync(Forever, CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Контекст_без_перехоплювача_будить_одразу_навіть_у_транзакції()
    {
        // Сигнал не бачить коміту такого контексту — відкладати нікуди; поведінка як до B125.
        var signal = new JobQueueSignal();
        await using var db = Sql.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();

        await Scheduler(db, signal).EnqueueAsync<SigProbeJob>(new { n = 5 }, CancellationToken.None);

        Assert.True(signal.WaitAsync(Forever, CancellationToken.None).IsCompletedSuccessfully);
        await tx.RollbackAsync();
    }

    private static DbBackgroundJobScheduler Scheduler(EcrDbContext db, JobQueueSignal signal)
        => new(new DbJobQueue(db, new SystemClock()), new QuartzJobScheduler(), signal);

    private static async Task AssertSilentAsync(JobQueueSignal signal, IDisposable laneWaiter)
    {
        using var cts = new CancellationTokenSource();
        var primary = signal.WaitAsync(Forever, cts.Token);
        var own = ((JobQueueSignal.LaneWaiter)laneWaiter).WaitAsync(Forever, cts.Token);
        Assert.False(primary.IsCompleted, "основний цикл розбуджено без коміту");
        Assert.False(own.IsCompleted, "підписку лейна розбуджено без коміту");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => primary);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => own);
    }
}

/// <summary>Задача-зонд B125: предмет — лише постановка (коротке ім'я: JobCode ≤ 64).</summary>
public sealed class SigProbeJob : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
}
