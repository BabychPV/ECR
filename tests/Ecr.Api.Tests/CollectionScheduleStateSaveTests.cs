// tests/Ecr.Api.Tests/CollectionScheduleStateSaveTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Integration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz.Impl;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Старт доводить стан постановки розкладу (<c>LastError</c>) до бази, а збій
/// цього запису старту не валить і не мовчить.
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionScheduleStateSaveTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пропущений_на_старті_розклад_отримує_LastError_у_базі()
    {
        var id = await AddScheduleAsync("15 2 * * *");
        var logger = new RecordingLogger<RecurringScheduleService>();

        await using (var db = Context())
        {
            await StartAsync(db, id, logger);
        }

        await using var read = Context();
        var saved = await read.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.False(string.IsNullOrWhiteSpace(saved.LastError));
        Assert.Equal(Now, saved.LastErrorAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_запису_стану_старт_не_валить_але_лишає_помилку_в_журналі()
    {
        var id = await AddScheduleAsync("15 2 * * *");
        var logger = new RecordingLogger<RecurringScheduleService>();

        await using var db = Context();
        var schedules = await db.CollectionSchedules.Where(s => s.Id == id).ToListAsync();

        // Розклад правлять саме зараз: версія рядка вже інша.
        await using (var other = Context())
        {
            (await other.CollectionSchedules.SingleAsync(s => s.Id == id)).SetLookback(3);
            await other.SaveChangesAsync();
        }

        // Стан ставиться напряму: цей тест стереже ЗАПИС, а не цикл постановки.
        Assert.Single(schedules).MarkInvalid("bad cron", Now);
        await RecurringScheduleService.SaveCollectionScheduleStateAsync(db, logger, CancellationToken.None);

        var error = Assert.Single(logger.OfLevel(LogLevel.Error));
        Assert.Contains("LastError", error.Message, StringComparison.Ordinal);
    }

    private static async Task StartAsync(
        EcrDbContext db, int id, RecordingLogger<RecurringScheduleService> logger)
    {
        var schedules = await db.CollectionSchedules.Where(s => s.Id == id).ToListAsync();
        await ApplyAsync(schedules, logger);
        await RecurringScheduleService.SaveCollectionScheduleStateAsync(db, logger, CancellationToken.None);
    }

    private static Task<int> ApplyAsync(
        List<CollectionSchedule> schedules, RecordingLogger<RecurringScheduleService> logger)
    {
        var jobs = new QuartzJobScheduler(new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        }));

        return RecurringScheduleService.ApplyCollectionSchedulesAsync(
            schedules, jobs, new CollectionScheduleApplier(jobs), logger, Now, CancellationToken.None);
    }

    private async Task<int> AddScheduleAsync(string cron)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = Context();

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Source" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);

        // ⛔ Неактивне НАВМИСНО, і це не дрібниця оформлення. База тестів спільна
        // на весь прогін: активне джерело, яке жодного разу не збиралося, для
        // `SourcesHealthCheck` — прогалина, тобто `Degraded`, і після цього
        // `HealthTests.Health_ready_зелений…` падає «Expected Healthy, actual
        // Degraded» у КОЖНОМУ повному прогоні, проходячи поодинці. Саме так воно
        // й було, і виглядало як плаваючий тест. Розкладу активність не потрібна:
        // перевіряється запис стану постановки, а не збір.
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var schedule = new CollectionSchedule(entity.Id, cron);
        db.CollectionSchedules.Add(schedule);
        await db.SaveChangesAsync();

        return schedule.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
