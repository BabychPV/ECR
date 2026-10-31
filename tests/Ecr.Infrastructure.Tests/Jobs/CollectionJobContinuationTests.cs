// tests/Ecr.Infrastructure.Tests/Jobs/CollectionJobContinuationTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-462 (P1 аудиту PI): ручний діапазон, який прогін не дочитав за ліміт (Q-250), продовжується задачею в черзі
/// від місця зупинки — тим самим автором, з тим самим кінцем, з номером продовження; плановий збір, прогін без
/// просування і вичерпаний ланцюг — не продовжуються.
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionJobContinuationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Resume = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <remarks>⛔ МУТАЦІЯ: прибрати виклик <c>ContinueAsync</c> у <c>CollectionJob</c> — продовження немає, тест червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Ручний_діапазон_обірваний_годинником_ставить_продовження_від_місця_зупинки()
    {
        await using var db = Context();
        var entity = await ArrangeEntityAsync(db);
        var (jobs, runner) = Doubles(entity, Resume);

        await Job(db, runner, jobs).ExecuteAsync(
            new CollectionJobRequest(entity, From, To, Manual: true, Continuation: 4), new Progress("job-17"), CancellationToken.None);

        await jobs.Received(1).EnqueueAsync<ICollectionJob>(
            Arg.Is<object?>(p => IsContinuation(p, entity, 5)),
            Arg.Any<CancellationToken>(),
            7);
    }

    private static bool IsContinuation(object? payload, int entity, int number)
    {
        var r = payload as CollectionJobRequest;
        return r is not null
               && r.SourceEntityId == entity
               && r.FromUtc == Resume
               && r.ToUtc == To
               && r.Manual == true
               && r.Continuation == number;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    [InlineData("scheduled")]
    [InlineData("noResume")]
    [InlineData("noProgress")]
    [InlineData("exhausted")]
    public async Task Без_явного_діапазону_без_просування_чи_після_стелі_продовження_немає(string kind)
    {
        await using var db = Context();
        var entity = await ArrangeEntityAsync(db);
        var (jobs, runner) = Doubles(entity, kind switch
        {
            "noResume" => null,
            "noProgress" => From,
            _ => Resume,
        });

        var request = kind switch
        {
            "scheduled" => new CollectionJobRequest(entity, null, null),
            "exhausted" => new CollectionJobRequest(entity, From, To, Manual: true, Continuation: CollectionJob.MaxContinuations),
            _ => new CollectionJobRequest(entity, From, To, Manual: true),
        };

        await Job(db, runner, jobs).ExecuteAsync(request, new Progress("job-18"), CancellationToken.None);

        await jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<ICollectionJob>(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public void Номер_продовження_не_серіалізується_коли_його_немає()
    {
        // ⚠ Payload планового збору — ключ періодичної задачі: зайве поле дало б дубль розкладу після оновлення.
        var json = System.Text.Json.JsonSerializer.Serialize(new CollectionJobRequest(5, null, null));
        Assert.DoesNotContain("Continuation", json, StringComparison.Ordinal);

        var parsed = CollectionJobRequest.Parse(System.Text.Json.JsonSerializer.Serialize(
            new CollectionJobRequest(5, From, To, Manual: true, Continuation: 3)));
        Assert.Equal(3, parsed.Continuation);
    }

    private static (IBackgroundJobScheduler Jobs, ICollectionRunner Runner) Doubles(int entity, DateTime? resume)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.GetCreatedByUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(7);

        var runner = Substitute.For<ICollectionRunner>();
        runner.RunAsync(entity, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>())
            .Returns(call => new CollectionRunSummary(
                call.ArgAt<DateTime>(1), 10, Degraded: resume is not null, ResumeFromUtc: resume));

        return (jobs, runner);
    }

    private static CollectionJob Job(EcrDbContext db, ICollectionRunner runner, IBackgroundJobScheduler jobs)
        => new(
            runner,
            db,
            jobs,
            new TestClock(Now),
            Substitute.For<INotificationOutbox>(),
            new OutboxDispatcher(db, new TestClock(Now), Substitute.For<INotificationSender>()),
            Substitute.For<IRegistrySyncJob>());

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "D-462" }),
            ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⚠ Неактивна (як у сусідніх тестах задачі): активна без завершеного збору фарбує здоров'я джерел.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Канал прогресу воркера: несе ідентифікатор задачі, як <c>StoreJobProgress</c>.</summary>
    private sealed class Progress(string jobId) : IJobProgress, IJobIdentity
    {
        public string JobId { get; } = jobId;

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
