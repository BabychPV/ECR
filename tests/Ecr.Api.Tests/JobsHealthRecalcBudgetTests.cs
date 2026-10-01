// tests/Ecr.Api.Tests/JobsHealthRecalcBudgetTests.cs
using Ecr.Api.Health;
using Ecr.Api.Startup;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
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
/// ПРД-13 (НФ-8.6.4): перерахунок, що завершився, але вийшов за бюджет (600 с), жовтить
/// перевірку <c>jobs</c> добу — за зразком <c>jobs.deferralExhausted</c>
/// (<see cref="JobsHealthStaleTests"/>), але для УСПІШНОЇ задачі: слід лишає
/// <c>RecalculationBudgetMonitor</c> конвертом у <c>itg.JobProgress.Message</c>.
/// </summary>
/// <remarks>
/// ⚠ Справжня база і справжній <see cref="JobProgressStore"/>. Лічильник глобальний, тож кожен
/// тест живе у СВОЄМУ місяці 2036 року (вікно — доба, місяці не перетинаються), а перед
/// перевіркою йде прохід прибирання «на чисто» — чужі завислі рядки на той час застарілі.
/// Мутації (прогнано): (1) прибрати блок лічильника в <c>JobsHealthCheck</c> — червоний
/// перший тест і тест кінцевої точки; (2) прибрати фільтр стану <c>Succeeded</c> у
/// <c>JobProgressStore.CountWithMessageKeyAsync</c> — червоний тест «Failed не рахується».
/// </remarks>
[Collection("SqlServer")]
public sealed class JobsHealthRecalcBudgetTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly List<string> createdJobs = [];

    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("jobs", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Успішний_перерахунок_понад_бюджет_жовтить_jobs_добу_з_лічильником()
    {
        var at = new DateTime(2036, 1, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(at.AddHours(-1), "Succeeded", OverBudgetEnvelope());

        var degraded = await CheckAtAsync(at);

        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Equal(1, degraded.Data["recalcOverBudget"]);
        Assert.Contains("1", degraded.Description, StringComparison.Ordinal);

        // Через добу — поза вікном: зелений сам.
        var later = at.Add(JobsHealthCheck.RecalcOverBudgetWindow).AddMinutes(1);
        await SweepAtAsync(later);
        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(later)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Успішний_перерахунок_у_межах_бюджету_jobs_не_жовтить()
    {
        var at = new DateTime(2036, 3, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(
            at.AddHours(-1), "Succeeded",
            JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope("jobs.recalcFormulasDone")));

        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(at)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Failed_із_тим_самим_ключем_не_рахується_як_перевищення_бюджету()
    {
        // Провалена задача — окремий сигнал (Failed у /jobs); тривалість до відмови не є тривалістю перерахунку.
        var at = new DateTime(2036, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        await SweepAtAsync(at);
        await FinishAsync(at.AddHours(-1), "Failed", OverBudgetEnvelope());

        Assert.Equal(HealthStatus.Healthy, (await CheckAtAsync(at)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Перевищення_бюджету_не_робить_health_ready_503_а_jobs_показує_жовтий()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        var now = app.Services.GetRequiredService<Domain.Abstractions.IClock>().UtcNow;
        var jobId = $"overbudget-{Guid.NewGuid():N}";
        await FinishAsync(now.AddMinutes(-5), "Succeeded", OverBudgetEnvelope(), jobId);

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        // ⛔ Повільний перерахунок не виводить інстанс із ротації: лише Degraded, не 503.
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

    /// <summary>Рівно той конверт, який пише <c>RecalculationBudgetMonitor</c>.</summary>
    private static string OverBudgetEnvelope()
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            RecalculationBudgetMonitor.OverBudgetKey,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["seconds"] = "720", ["limit"] = "600" }));

    private async Task FinishAsync(DateTime at, string state, string message, string? jobId = null)
    {
        jobId ??= $"overbudget-{Guid.NewGuid():N}";
        createdJobs.Add(jobId);

        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        await store.StartAsync(jobId, "Ecr.Infrastructure.Jobs.RecalculationJob", at.AddMinutes(-10), CancellationToken.None);
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
