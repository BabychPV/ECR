using Ecr.Api.Health;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Кожна перевірка здоров'я вміє почервоніти (<c>D-139</c>).
/// </summary>
/// <remarks>
/// ⛔ Це і є «тест, який падає на невиправленому коді» (<c>D-134</c>) для
/// <c>A7-37</c>. Обидві перевірки — <c>jobs</c> і <c>sources</c> — від ЕТАПУ 5
/// відповідали заглушкою «з'явиться на Етапі 5» **незалежно ні від чого**:
/// <c>/health/ready</c> був жовтим завжди, при семи працюючих задачах і живому
/// зборі. Перевірка, яка ніколи не змінює відповіді, не перевіряє нічого — і
/// зелений тест на неї теж нічого не значив.
///
/// ⚠ Перевіряється саме ЗМІНА відповіді: зелена на справному, червона на
/// зламаному. Одне без одного не доводить нічого — заглушка, що завжди
/// зелена, пройшла б половину цих тестів.
///
/// ⚠ Тести юніт-рівня і бази не потребують: підсистема ламається підміною
/// залежності, а не зупинкою SQL Server. Зупиняти службу заради тесту — це
/// перевірка, яку не можна запустити в CI, тобто якої немає.
/// </remarks>
public sealed class HealthRedStateTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("test", Substitute.For<IHealthCheck>(), null, null),
    };

    // ── jobs ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.9")]
    public async Task Задачі_червоніють_коли_планувальника_немає_в_контейнері()
    {
        var check = new JobsHealthCheck(factory: null);

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.9")]
    public async Task Задачі_червоніють_коли_планувальник_зупинений()
    {
        // ⛔ Зупинений планувальник — найгірший стан із можливих: процес живий,
        // API відповідає, а стани періодів не оновлюються, партиції не
        // додаються і сповіщення не йдуть. Ззовні все гаразд.
        var scheduler = Substitute.For<IScheduler>();
        scheduler.IsStarted.Returns(false);

        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var result = await new JobsHealthCheck(factory).CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.9")]
    public async Task Задачі_жовтіють_коли_жодного_розкладу_не_зареєстровано()
    {
        var scheduler = StartedScheduler(jobs: 7, triggers: 0);

        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var result = await new JobsHealthCheck(factory).CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-12.9")]
    public async Task Задачі_зелені_коли_планувальник_працює_з_розкладами()
    {
        // ⚠ Без цього тесту попередні три довели б лише, що перевірка вміє
        // червоніти — а заглушка, яка завжди червона, теж уміє.
        var scheduler = StartedScheduler(jobs: 7, triggers: 7);

        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var result = await new JobsHealthCheck(factory).CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(7, result.Data["triggers"]);
    }

    // ── sources ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.9")]
    public async Task Джерела_червоніють_коли_сховища_немає_в_контейнері()
    {
        var result = await new SourcesHealthCheck(sources: null)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.9")]
    public async Task Джерела_червоніють_коли_останній_збір_упав()
    {
        var store = StoreWith(Source("pi-water", active: true, lastRunStatus: "Failed", gap: null));

        var result = await new SourcesHealthCheck(store)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(1, result.Data["failedSources"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.9")]
    public async Task Джерела_жовтіють_коли_в_покритті_є_прогалина()
    {
        var store = StoreWith(
            Source("pi-water", active: true, lastRunStatus: "Succeeded", gap: new DateTime(2026, 1, 1)));

        var result = await new SourcesHealthCheck(store)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.9")]
    public async Task Джерело_яке_ніколи_не_запускалося_рахується_прогалиною()
    {
        // ⛔ Забуте налаштування мовчить, і мовчання приймають за спокій.
        var store = StoreWith(Source("pi-air", active: true, lastRunStatus: null, gap: null));

        var result = await new SourcesHealthCheck(store)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-11.9")]
    public async Task Відсутність_активних_джерел_це_зелений_стан()
    {
        // ⚠ Система без інтеграції працездатна: дані вводять руками. Жовтий
        // колір тут означав би «щось не так» там, де все за налаштуванням, — і
        // саме так виникають індикатори, на які перестають дивитися.
        var store = StoreWith(Source("pi-water", active: false, lastRunStatus: null, gap: null));

        var result = await new SourcesHealthCheck(store)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ── db ──────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.9")]
    public async Task База_червоніє_коли_вона_недоступна()
    {
        // ⛔ Ламається саме з'єднання, а не служба SQL Server: зупиняти службу
        // заради тесту означало б перевірку, яку не можна запустити в CI,
        // тобто якої немає.
        //
        // ⚠ Порт 1 замість справжнього: з'єднання не встановиться швидко і
        // напевно, без очікування таймауту.
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=Ecr;Connect Timeout=1;TrustServerCertificate=True")
            .Options;

        await using var broken = new EcrDbContext(options);

        var check = new DatabaseHealthCheck(
            Substitute.For<ISqlCapabilities>(),
            broken,
            Substitute.For<IClock>());

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);

        // ⚠ Текст винятку клієнту НЕ віддається (ФВ-6.11): у ньому бувають
        // рядок підключення, ім'я сервера й облікові дані.
        var visible = $"{result.Description} {string.Join(' ', result.Data.Values)}";
        Assert.DoesNotContain("127.0.0.1", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", visible, StringComparison.OrdinalIgnoreCase);
    }

    // ── допоміжне ───────────────────────────────────────────────────────────

    private static IScheduler StartedScheduler(int jobs, int triggers)
    {
        var scheduler = Substitute.For<IScheduler>();
        scheduler.IsStarted.Returns(true);
        scheduler.IsShutdown.Returns(false);

        scheduler
            .GetJobKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, jobs).Select(i => new JobKey($"job{i}")).ToList());

        scheduler
            .GetTriggerKeys(Arg.Any<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, triggers).Select(i => new TriggerKey($"trigger{i}")).ToList());

        return scheduler;
    }

    private static SourceEntityStatus Source(string code, bool active, string? lastRunStatus, DateTime? gap)
        => new(
            Id: 1,
            Code: code,
            DisplayName: code,
            EntityPath: code,
            Transport: "PiSql",
            IsActive: active,
            LastRun: lastRunStatus is null
                ? null
                : new CollectionRunStatus(DateTime.UtcNow, lastRunStatus, 10),
            OldestGap: gap);

    private static ICollectionStore StoreWith(params SourceEntityStatus[] sources)
    {
        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns(sources.ToList());

        return store;
    }
}
