// tests/Ecr.Api.Tests/Health/RecalculationWorkerHealthTests.cs
using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// I2-2: перевірка <c>worker</c> жовтіє, коли Api віддав перерахунок пулу
/// <c>EcrWorker</c>, а виконавця немає, — і мовчить, коли він є чи не потрібен.
/// </summary>
/// <remarks>
/// Факти (реєстр служб, черга) підмінено: предмет — рішення перевірки. Справжній
/// SQL черги — <see cref="RecalculationWorkerProbeTests"/>.
/// </remarks>
public sealed class RecalculationWorkerHealthTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("worker", Substitute.For<IHealthCheck>(), null, null),
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(null, null)]
    [InlineData("Quartz", "InProcess")]
    [InlineData("Database", "InProcess")]
    [InlineData("Quartz", "Worker")]
    public async Task Без_пулу_перерахунок_у_процесі_Api_і_стан_зелений(string? mode, string? executor)
    {
        // Навіть «служби немає» не має значення: перерахунок виконує сам Api.
        var result = await CheckAsync(mode, executor, WorkerServiceState.Missing, new(5, 0));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False(result.Data.ContainsKey("workerService"));
    }

    /// <remarks>
    /// ⛔ Головний випадок: Executor = Worker, а служби на сервері немає. Мутація
    /// (прогнано): прибрати гілку <c>Missing</c> → на порожній черзі зелений, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Worker_без_служби_жовтий_ще_до_першої_задачі()
    {
        var result = await CheckAsync("Database", "Worker", WorkerServiceState.Missing, new(0, 0));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Missing", result.Data["workerService"]);
        Assert.Contains("not installed", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Worker_з_вимкненою_службою_жовтий()
    {
        var result = await CheckAsync("Database", "Worker", WorkerServiceState.Disabled, new(0, 0));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Disabled", result.Data["workerService"]);
    }

    /// <remarks>
    /// Служба є, але задач ніхто не бере (зупинена, падає, без рядка підключення).
    /// Мутація (прогнано): умову застою замінити на <c>false</c> → червоний.
    /// Unknown — Linux чи недоступний реєстр: застій черги однаково ловиться.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(WorkerServiceState.Installed)]
    [InlineData(WorkerServiceState.Unknown)]
    public async Task Застряглі_задачі_без_живої_оренди_жовтий(WorkerServiceState service)
    {
        var result = await CheckAsync("Database", "Worker", service, new(3, 0));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(3, result.Data["stalledJobs"]);
        Assert.Contains("3 job(s)", result.Description, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Черга довша за пул — не збій: воркери зайняті, хтось тримає оренду.
    /// Мутація (прогнано): прибрати <c>LiveLeases == 0</c> з умови → червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(3, 1)]
    [InlineData(0, 0)]
    public async Task Пул_працює_або_простоює_зелений(int stalled, int live)
    {
        var result = await CheckAsync("Database", "Worker", WorkerServiceState.Installed, new(stalled, live));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Installed", result.Data["workerService"]);
    }

    /// <remarks>
    /// ⛔ Жодного <c>Unhealthy</c> і жодного винятку назовні: перевірка має тег
    /// <c>ready</c>, 503 зняв би Api з балансувальника через фонову чергу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Збій_читання_черги_жовтий_а_не_виняток()
    {
        var probe = Substitute.For<IRecalculationWorkerProbe>();
        probe.ReadServiceState().Returns(WorkerServiceState.Installed);
        probe.ReadQueueAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<RecalculationQueueState>>(_ => throw new InvalidOperationException("db down"));

        var result = await Check(Configuration("Database", "Worker"), probe);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    private static Task<HealthCheckResult> CheckAsync(
        string? mode, string? executor, WorkerServiceState service, RecalculationQueueState queue)
    {
        var probe = Substitute.For<IRecalculationWorkerProbe>();
        probe.ReadServiceState().Returns(service);
        probe.ReadQueueAsync(RecalculationWorkerHealthCheck.StallAfter, Arg.Any<CancellationToken>()).Returns(queue);
        return Check(Configuration(mode, executor), probe);
    }

    private static Task<HealthCheckResult> Check(IConfiguration configuration, IRecalculationWorkerProbe probe)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");
        return new RecalculationWorkerHealthCheck(configuration, probe, new FakeUiStringCatalog(), user)
            .CheckHealthAsync(Context, CancellationToken.None);
    }

    private static IConfiguration Configuration(string? mode, string? executor)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DbBackgroundJobScheduler.ModeKey] = mode,
                [JobLaneMap.ExecutorKey] = executor,
            })
            .Build();
}
