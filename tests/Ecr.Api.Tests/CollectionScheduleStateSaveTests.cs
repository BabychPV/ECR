// tests/Ecr.Api.Tests/CollectionScheduleStateSaveTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Старт доводить стан постановки розкладу (<c>LastError</c>) до бази, а збій
/// цього запису старту не валить і не мовчить.
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionScheduleStateSaveTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly List<int> created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // ⛔ База спільна на весь прогін Api: розклад із невалідним cron, що лишився в ній,
    // на старті КОЖНОГО наступного хоста дає LogLevel.Error (ПРОПУЩЕНО — невалідний cron),
    // а сторожі на кшталт ФВ_5_24_Збій_запису… вимагають порожні ServerErrors.
    // Тому створене тут — прибирається тут (лише розклади: сутність і джерело неактивні).
    public async Task DisposeAsync()
    {
        if (created.Count == 0)
        {
            return;
        }

        await using var db = Context();
        await db.CollectionSchedules.Where(s => created.Contains(s.Id)).ExecuteDeleteAsync();
    }

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

    /// <remarks>
    /// Мутаційний доказ: (1) прибрати пропуск у <c>ApplyCollectionSchedulesAsync</c>
    /// → червоний (поставлено 2, <c>LastError</c> власної форми порожній);
    /// (2) <c>LocalSourceEntityIdsAsync</c> фільтрує <c>External</c> замість
    /// <c>Local</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.8")]
    public async Task ФВ_12_8_розклад_власної_форми_заведений_до_правила_на_старті_пропущено_з_LastError_у_базі()
    {
        var localId = await AddScheduleAsync(RecurringScheduleService.HourlyCron, RegistrySourceKind.Local);
        var externalId = await AddScheduleAsync(RecurringScheduleService.HourlyCron, RegistrySourceKind.External);
        var logger = new RecordingLogger<RecurringScheduleService>();

        // ⚠ Заглушка, а не Quartz: у спільному прогоні Api статичний LogProvider
        // Quartz уже прив'язаний до LoggerFactory закритого тестового хоста, і
        // справжня постановка падає ObjectDisposedException. Тут стережеться
        // запит і запис стану, а не сам Quartz (його — CollectionScheduleStartupTests).
        var scheduler = new RecordingScheduler();
        int applied;
        int externalEntityId;
        await using (var db = Context())
        {
            var schedules = await db.CollectionSchedules
                .Where(s => s.Id == localId || s.Id == externalId)
                .OrderBy(s => s.Id)
                .ToListAsync();
            externalEntityId = schedules.Single(s => s.Id == externalId).SourceEntityId;

            // Той самий запит, що й на старті: база спільна, тож у множині можуть
            // бути й чужі Local-сутності — важливо лише, що наша в ній є, а External — ні.
            var local = await RecurringScheduleService.LocalSourceEntityIdsAsync(db, CancellationToken.None);

            applied = await RecurringScheduleService.ApplyCollectionSchedulesAsync(
                schedules, local, scheduler, new CollectionScheduleApplier(scheduler), logger, Now,
                CancellationToken.None);
            await RecurringScheduleService.SaveCollectionScheduleStateAsync(db, logger, CancellationToken.None);
        }

        Assert.Equal(1, applied);
        Assert.Equal(CollectionScheduleApplier.PayloadOf(externalEntityId), Assert.Single(scheduler.Scheduled));

        await using var read = Context();
        var savedLocal = await read.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == localId);
        var savedExternal = await read.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == externalId);

        Assert.Equal(RecurringScheduleService.LocalEntityScheduleSkipped, savedLocal.LastError);
        Assert.Equal(Now, savedLocal.LastErrorAt);
        Assert.True(savedLocal.IsEnabled);

        Assert.Null(savedExternal.LastError);
        Assert.Single(logger.OfLevel(LogLevel.Warning));
    }

    private static async Task StartAsync(
        EcrDbContext db, int id, RecordingLogger<RecurringScheduleService> logger)
    {
        var schedules = await db.CollectionSchedules.Where(s => s.Id == id).ToListAsync();
        await ApplyAsync(schedules, logger, new HashSet<int>());
        await RecurringScheduleService.SaveCollectionScheduleStateAsync(db, logger, CancellationToken.None);
    }

    private static Task<int> ApplyAsync(
        List<CollectionSchedule> schedules,
        RecordingLogger<RecurringScheduleService> logger,
        IReadOnlySet<int> localSourceEntityIds)
    {
        var jobs = new QuartzJobScheduler(StandaloneQuartz.Factory("ecr-tests"));

        return RecurringScheduleService.ApplyCollectionSchedulesAsync(
            schedules, localSourceEntityIds, jobs, new CollectionScheduleApplier(jobs), logger, Now,
            CancellationToken.None);
    }

    private async Task<int> AddScheduleAsync(string cron, RegistrySourceKind kind = RegistrySourceKind.External)
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

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", kind);

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

        created.Add(schedule.Id);
        return schedule.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Планувальник, що лише запам'ятовує постановки; решта порту тут не потрібна.</summary>
    private sealed class RecordingScheduler : IBackgroundJobScheduler
    {
        public List<object?> Scheduled { get; } = [];

        public bool IsValidCron(string expression, out string? error)
        {
            error = null;
            return true;
        }

        public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
            where TJob : IBackgroundJob
        {
            Scheduled.Add(payload);
            return Task.CompletedTask;
        }

        public Task<bool> UnscheduleAsync<TJob>(object? payload, CancellationToken ct)
            where TJob : IBackgroundJob => throw new NotSupportedException();

        public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob => throw new NotSupportedException();

        public Task<string> EnqueueExclusiveAsync<TJob>(
            string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob => throw new NotSupportedException();

        public Task<string> EnqueueCoalescedAsync<TJob>(
            string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob => throw new NotSupportedException();

        public Task CancelAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> RestartAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<JobSummary>> ListRecentAsync(
            JobListFilter filter, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}
