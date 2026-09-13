using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Статус health-перевірок резолвиться каталогом рядків, а не готовим
/// українським реченням (`Q-304`).
/// </summary>
/// <remarks>
/// ⛔ До цієї картки `/admin/health` показував `"База доступна."`,
/// `"Планувальник працює."`, `"Активних джерел збору немає."` незалежно від
/// мови інтерфейсу (en/ru/kz) — знайдено живим відкриттям сторінки, не
/// тестом. Перевірки резолвяться в scope запиту (`DatabaseHealthCheck` уже
/// бере звідти `EcrDbContext`), тож DI для каталогу є — на відміну від
/// `Ecr.Expressions` (`Q-303`), де DI не було взагалі й локалізувати
/// довелося клієнту.
/// </remarks>
public sealed class HealthCatalogLocalizationTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("test", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Планувальник_живий_показує_текст_із_каталогу_а_не_вбудований_запасний_варіант()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "health.jobs.running", "Custom scheduler message from catalog.");
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var scheduler = StartedScheduler(jobs: 1, triggers: 1);
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var check = new JobsHealthCheck(factory, catalog, user);
        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Custom scheduler message from catalog.", result.Description);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Планувальник_без_запису_в_каталозі_показує_англійський_запасний_варіант_не_ключ_і_не_помилку()
    {
        // ⚠ Порожній каталог, не заповнений: ключа ("health.jobs.running")
        // ніде немає — саме так виглядає прогалина seed до того, як
        // термінолог заведе переклад (D-95).
        var catalog = new FakeUiStringCatalog();
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var scheduler = StartedScheduler(jobs: 1, triggers: 1);
        var factory = Substitute.For<ISchedulerFactory>();
        factory.GetScheduler(Arg.Any<CancellationToken>()).Returns(scheduler);

        var check = new JobsHealthCheck(factory, catalog, user);
        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("The scheduler is running.", result.Description);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Збій_самого_каталогу_не_валить_перевірку_а_дає_запасний_варіант()
    {
        // ⛔ Той самий принцип, що й `ExceptionHandlingMiddleware.
        // LocalizedTitleAsync`: похід за перекладом не має права кинути
        // вдруге, коли перевірка й так уже діагностує відмову (тут —
        // штучну, гіпотетичну відмову самого каталогу).
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetScopedAsync(Arg.Any<string>(), Arg.Any<UiStringScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UiStringCatalog>(new InvalidOperationException("каталог недоступний")));
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var check = new SourcesHealthCheck(sources: null, catalog, user);
        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("The collection store is not registered in the container.", result.Description);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Прогалина_в_покритті_підставляє_число_в_шаблон_каталогу()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "health.sources.gapsCount", "GAPS={count}!");
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SourceEntityStatus(
                Id: 1, Code: "pi-water", DisplayName: "pi-water", EntityPath: "pi-water",
                Transport: "PiSql", IsActive: true, LastRun: null, OldestGap: null),
        ]);

        var check = new SourcesHealthCheck(store, catalog, user);
        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("GAPS=1!", result.Description);
    }

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
}
