// tests/Ecr.Api.Tests/Health/HealthReadyLoadTests.cs

using System.Net;
using Ecr.Api.Health;
using Ecr.Api.Security;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// P1-03 (AUDIT-2026-10-09b): анонімний <c>/health/ready</c> не підсилює запит — важкі
/// перевірки кешуються з single-flight, а частота проб з однієї адреси обмежена.
/// </summary>
/// <remarks>
/// ⚠ Без бази й без часу: одночасність тримає <see cref="TaskCompletionSource"/>, межу —
/// сам обмежувач (<c>AttemptAcquire</c>), тож результат не залежить від швидкості машини.
/// </remarks>
public sealed class HealthReadyLoadTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("sources", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Сто_одночасних_проб_ready_роблять_один_ListSourceEntities()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<SourceEntityStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var cache = new HealthResultCache();
        var check = new SourcesHealthCheck(
            store, Substitute.For<IUiStringCatalog>(), Substitute.For<ICurrentUser>(), cache: cache);

        // Усі сто проб доходять до кешу, поки перше обчислення ще висить на `gate`.
        var probes = Enumerable.Range(0, 100)
            .Select(_ => check.CheckHealthAsync(Context, CancellationToken.None))
            .ToList();

        gate.SetResult([]);
        var results = await Task.WhenAll(probes);

        // Мутація: прибрати кеш із `SourcesHealthCheck` (або single-flight із
        // `HealthResultCache.GetOrAddAsync`) — тут 100 викликів замість одного.
        await store.Received(1).ListSourceEntitiesAsync(Arg.Any<CancellationToken>());
        Assert.All(results, r => Assert.Equal(HealthStatus.Healthy, r.Status));

        // І в межах TTL наступна проба теж не ходить у сховище.
        await check.CheckHealthAsync(Context, CancellationToken.None);
        await store.Received(1).ListSourceEntitiesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Скасування_ведучої_проби_не_скасовує_решту_вона_рахує_сама()
    {
        var cache = new HealthResultCache();
        var hold = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaderCts = new CancellationTokenSource();

        var leader = cache.GetOrAddAsync("ready:sources:en", ct => hold.Task.WaitAsync(ct), leaderCts.Token);

        var followerComputed = 0;
        var follower = cache.GetOrAddAsync(
            "ready:sources:en",
            _ =>
            {
                Interlocked.Increment(ref followerComputed);
                return Task.FromResult(HealthCheckResult.Degraded("own"));
            },
            CancellationToken.None);

        // Поки ведучий рахує, другий чекає його, а не рахує сам.
        Assert.Equal(0, Volatile.Read(ref followerComputed));

        await leaderCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
        var result = await follower;

        Assert.Equal(1, Volatile.Read(ref followerComputed));
        Assert.Equal("own", result.Description);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Проби_ready_з_однієї_адреси_понад_межу_дають_відмову_а_live_і_інша_адреса_вільні()
    {
        var limiter = GlobalLimiter(("Security:RateLimit:HealthReadyPermitPerMinute", "3"));

        for (var i = 1; i <= 3; i++)
        {
            using var allowed = limiter.AttemptAcquire(Request("/health/ready", "198.51.100.7"));
            Assert.True(allowed.IsAcquired, $"проба №{i}");
        }

        // Мутація: прибрати гілку `IsHealthReady` з глобального обмежувача — тут дозвіл.
        using var rejected = limiter.AttemptAcquire(Request("/health/ready", "198.51.100.7"));
        Assert.False(rejected.IsAcquired);

        using var otherAddress = limiter.AttemptAcquire(Request("/health/ready", "198.51.100.8"));
        Assert.True(otherAddress.IsAcquired);

        // `/health/live` нічого не робить і не обмежується: інакше балансувальник
        // знімав би живий процес через власну частоту проб.
        for (var i = 0; i < 10; i++)
        {
            using var live = limiter.AttemptAcquire(Request("/health/live", "198.51.100.7"));
            Assert.True(live.IsAcquired);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Межа_ready_за_замовчуванням_60_на_хвилину()
    {
        var limiter = GlobalLimiter();

        for (var i = 1; i <= 60; i++)
        {
            using var allowed = limiter.AttemptAcquire(Request("/health/ready", "203.0.113.50"));
            Assert.True(allowed.IsAcquired, $"проба №{i}");
        }

        using var rejected = limiter.AttemptAcquire(Request("/health/ready", "203.0.113.50"));
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Нульова_межа_ready_зупиняє_старт_з_ім_ям_ключа()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Security:RateLimit:HealthReadyPermitPerMinute", "0")])
            .Build();

        var problem = Assert.Single(Ecr.Api.Options.EcrConfigurationValidation.Validate(configuration));
        Assert.Contains("Security:RateLimit:HealthReadyPermitPerMinute", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Важкі_перевірки_ready_приймають_спільний_кеш()
    {
        // Мутація: прибрати параметр `cache` з будь-якої перевірки — DI перестане
        // його передавати, і вона знову рахує на кожну пробу.
        foreach (var type in new[]
                 {
                     typeof(SourcesHealthCheck), typeof(DatabaseHealthCheck),
                     typeof(RecalculationWorkerHealthCheck), typeof(JobsHealthCheck),
                 })
        {
            var ctor = Assert.Single(type.GetConstructors());
            Assert.Contains(ctor.GetParameters(), p => p.ParameterType == typeof(HealthResultCache));
        }
    }

    private static System.Threading.RateLimiting.PartitionedRateLimiter<HttpContext> GlobalLimiter(
        params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddEcrRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
        return Assert.IsAssignableFrom<System.Threading.RateLimiting.PartitionedRateLimiter<HttpContext>>(limiter);
    }

    private static DefaultHttpContext Request(string path, string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }
}
