// tests/Ecr.Api.Tests/JobsHealthNotificationDigestTests.cs
using Ecr.Api.Health;
using Ecr.Api.Startup;
using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// CL-5: останнє зведення <c>NotificationJob</c>, що мало збої, але нікому не дійшло
/// («збоїв N; відправлено 0» — транспорт не налаштований), жовтить перевірку <c>jobs</c>.
/// Наступне зведення, яке дійшло або не мало збоїв, гасить жовтий.
/// </summary>
/// <remarks>
/// ⚠ Справжня база і справжній <see cref="JobProgressStore"/>; кожен тест — у СВОЄМУ місяці
/// 2037 року (вікно — доба), перед перевіркою — прибирання «на чисто», рядки видаляються в
/// <see cref="DisposeAsync"/> (причина — у <see cref="JobsHealthRecalcBudgetTests"/>).
/// Мутації (прогнано): (1) прибрати блок зведення в <c>JobsHealthCheck</c> — червоні перший
/// тест і тест кінцевої точки; (2) <c>OrderByDescending</c> → <c>OrderBy</c> у
/// <c>JobProgressStore.LatestSucceededMessageWithKeyAsync</c> — червоний «новіше гасить».
/// </remarks>
[Collection("SqlServer")]
public sealed class JobsHealthNotificationDigestTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly List<string> createdJobs = [];

    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("jobs", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Недоставлене_зведення_збоїв_жовтить_jobs_з_кількістю()
    {
        var at = new DateTime(2037, 1, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(at.AddMinutes(-30), "Succeeded", DigestEnvelope(count: 4, sent: 0, failures: 3));

        var degraded = await CheckAtAsync(at);

        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Equal(3, degraded.Data["notificationsUndelivered"]);
        Assert.Contains("3", degraded.Description, StringComparison.Ordinal);

        // Через добу без нових зведень — поза вікном: зелений сам.
        var later = at.Add(JobsHealthCheck.NotificationDigestWindow).AddMinutes(1);
        await SweepAtAsync(later);
        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(later)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Новіше_доставлене_зведення_гасить_жовтий()
    {
        var at = new DateTime(2037, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(at.AddHours(-2), "Succeeded", DigestEnvelope(count: 2, sent: 0));
        await FinishAsync(at.AddHours(-1), "Succeeded", DigestEnvelope(count: 2, sent: 1));

        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(at)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Зведення_без_збоїв_і_без_відправки_jobs_не_жовтить()
    {
        // Нема чого надсилати — не те саме, що нема чим надіслати.
        var at = new DateTime(2037, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(at.AddMinutes(-30), "Succeeded", DigestEnvelope(count: 0, sent: 0));

        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(at)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Лише_рядок_про_відсутніх_адресатів_jobs_не_жовтить()
    {
        // Свіжа інсталяція: збоїв немає, є лише інформаційний рядок «адресатів немає» (D-125) —
        // надсилати нічого не треба було. Жовтий тут навчав би ігнорувати картку (D-139).
        var at = new DateTime(2037, 7, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(at.AddMinutes(-30), "Succeeded", DigestEnvelope(count: 1, sent: 0, failures: 0));

        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(at)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Конверт_з_нечисловим_failures_не_валить_перевірку_а_бере_count()
    {
        var at = new DateTime(2037, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        var envelope = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            JobCompletionWarning.NotificationDoneKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = "2",
                [JobCompletionWarning.FailuresParam] = "abc",
                ["sent"] = "0",
            }));
        await FinishAsync(at.AddMinutes(-30), "Succeeded", envelope);

        var degraded = await CheckAtAsync(at);

        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Equal(2, degraded.Data["notificationsUndelivered"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Недоставлене_зведення_не_робить_health_ready_503_а_jobs_показує_жовтий()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        var now = app.Services.GetRequiredService<Domain.Abstractions.IClock>().UtcNow;
        await FinishAsync(now.AddMinutes(-1), "Succeeded", DigestEnvelope(count: 1, sent: 0));

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable, body);

        var jobs = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("name").GetString(), "jobs", StringComparison.Ordinal));
        Assert.Equal("Degraded", jobs.GetProperty("status").GetString());
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// ⛔ Рядки НЕ лишаються в базі. Лічильник рахує <c>UpdatedAt &gt;= межа</c> без верхньої межі, тож
    /// рядок 2036 року жовтив би кожен сусідній тест зі штучним годинником раніше (2032) — саме так
    /// перший прогін цього файлу поклав два тести <c>JobsHealthStaleTests</c>. Рядок «за зараз»
    /// жовтив би /health/ready усім тестам доби.
    /// </summary>
    public async Task DisposeAsync()
    {
        await using var db = sql.CreateContext();
        await db.JobProgresses.Where(p => createdJobs.Contains(p.JobId)).ExecuteDeleteAsync();
    }

    /// <summary>Рівно той конверт, який пише <c>NotificationJob</c> наприкінці прогону.</summary>
    private static string DigestEnvelope(int count, int sent, int? failures = null)
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            JobCompletionWarning.NotificationDoneKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [JobCompletionWarning.FailuresParam] =
                    (failures ?? count).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sent"] = sent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pending"] = "0",
            }));

    private async Task FinishAsync(DateTime at, string state, string message, string? jobId = null)
    {
        jobId ??= $"digest-{Guid.NewGuid():N}";
        createdJobs.Add(jobId);

        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        await store.StartAsync(jobId, "Ecr.Infrastructure.Jobs.NotificationJob", at.AddMinutes(-10), CancellationToken.None);
        await store.ReportAsync(jobId, 100, message, at, CancellationToken.None);
        await store.FinishAsync(jobId, state, state == "Failed" ? "test" : null, at, CancellationToken.None);
    }

    private async Task SweepAtAsync(DateTime at)
    {
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<Domain.Abstractions.IClock>(new TestClock(at));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await RecurringScheduleService.SweepOnceAsync(scope.ServiceProvider, purge: false, CancellationToken.None);
    }

    private async Task<HealthCheckResult> CheckAtAsync(DateTime at)
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.IsStarted.Returns(true);
        scheduler.IsShutdown.Returns(false);
        scheduler.GetJobKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), Arg.Any<CancellationToken>())
            .Returns(new List<JobKey> { new("job") });
        scheduler.GetTriggerKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TriggerKey> { new("trigger") });

        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        await using var db = sql.CreateContext();

        return await new JobsHealthCheck(
                factory, new FakeUiStringCatalog(), user, new JobProgressStore(db), new TestClock(at))
            .CheckHealthAsync(Context, CancellationToken.None);
    }
}
