// tests/Ecr.Api.Tests/JobsHealthCacheTests.cs
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
/// Анонімний <c>/health/ready</c> не має ходити в сховище на кожну пробу: результат
/// <see cref="JobsHealthCheck"/> живе <see cref="HealthResultCache.Ttl"/>, не довше.
/// </summary>
public sealed class JobsHealthCacheTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("jobs", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Два_виклики_в_межах_TTL_ходять_у_сховище_один_раз_а_після_TTL_удруге()
    {
        var time = new ManualTime();
        var (check, store) = Build(new HealthResultCache(time, TimeSpan.FromSeconds(5)));

        await check.CheckHealthAsync(Context, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        await check.CheckHealthAsync(Context, CancellationToken.None);

        await store.Received(1).SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromSeconds(2));
        await check.CheckHealthAsync(Context, CancellationToken.None);

        await store.Received(2).SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Degraded_не_ховається_довше_за_TTL()
    {
        var time = new ManualTime();
        var (check, store) = Build(new HealthResultCache(time, TimeSpan.FromSeconds(5)));

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(Context, CancellationToken.None)).Status);

        store.SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new StaleJobsSummary(1, DateTime.UtcNow));
        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(Context, CancellationToken.None)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task L1_02_різний_Accept_Language_не_обходить_кеш()
    {
        var time = new ManualTime();
        var cache = new HealthResultCache(time, TimeSpan.FromSeconds(5));
        var calls = 0;

        // Кожне читання мови — новий вигаданий тег, як у шквалі анонімних проб.
        var (check, store) = Build(cache, () => $"q{calls++}");

        for (var i = 0; i < 50; i++)
        {
            await check.CheckHealthAsync(Context, CancellationToken.None);
        }

        await store.Received(1).SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task L1_02_кеш_результатів_має_стелю_записів()
    {
        var cache = new HealthResultCache(new ManualTime(), TimeSpan.FromSeconds(5));
        for (var i = 0; i < HealthResultCache.MaxEntries * 3; i++)
        {
            await cache.GetOrAddAsync($"k{i}", _ => Task.FromResult(HealthCheckResult.Healthy()), CancellationToken.None);
        }

        Assert.True(cache.Count <= HealthResultCache.MaxEntries, $"записів {cache.Count}");
    }

    private static (JobsHealthCheck Check, IJobProgressStore Store) Build(HealthResultCache cache)
        => Build(cache, () => "en");

    private static (JobsHealthCheck Check, IJobProgressStore Store) Build(HealthResultCache cache, Func<string> language)
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
        user.Language.Returns(_ => language());

        var store = Substitute.For<IJobProgressStore>();
        store.SummarizeStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new StaleJobsSummary(0, null));

        var check = new JobsHealthCheck(
            factory, new RegistryCatalog(), user, store, new TestClock(DateTime.UtcNow), cache);
        return (check, store);
    }

    /// <summary>Каталог із реєстром мов: невідома мова — <c>en</c>, як у справжнього сховища.</summary>
    private sealed class RegistryCatalog : IUiStringCatalog
    {
        private readonly FakeUiStringCatalog _inner = new();

        public Task<string> ResolveLanguageAsync(string languageCode, CancellationToken ct)
            => Task.FromResult(languageCode is "en" or "ru" or "kz" ? languageCode : "en");

        public Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct) => _inner.GetAsync(languageCode, ct);

        public Task<UiStringCatalog> GetScopedAsync(string languageCode, UiStringScope scope, CancellationToken ct)
            => _inner.GetScopedAsync(languageCode, scope, ct);

        public Task<int> GetRevisionAsync(CancellationToken ct) => _inner.GetRevisionAsync(ct);

        public Task<IReadOnlyList<LanguageDto>> ListLanguagesAsync(CancellationToken ct) => _inner.ListLanguagesAsync(ct);

        public Task<UiStringWriteResult> SetAsync(UiStringWrite write, CancellationToken ct) => _inner.SetAsync(write, ct);

        public Task<string?> FindKeyAsync(string key, CancellationToken ct) => _inner.FindKeyAsync(key, ct);

        public Task<IReadOnlyList<UiStringRawRow>> ListRawAsync(string languageCode, CancellationToken ct)
            => _inner.ListRawAsync(languageCode, ct);

        public Task<IReadOnlyList<UiStringExportRow>> ListForExportAsync(string languageCode, CancellationToken ct)
            => _inner.ListForExportAsync(languageCode, ct);

        public Task<int> SetManyAsync(IReadOnlyList<UiStringWrite> writes, CancellationToken ct) => _inner.SetManyAsync(writes, ct);

        public Task<bool> LanguageExistsAsync(string languageCode, CancellationToken ct) => _inner.LanguageExistsAsync(languageCode, ct);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2036, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
