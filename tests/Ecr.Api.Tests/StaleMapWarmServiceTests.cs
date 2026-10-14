using Ecr.Api.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-430 (LP-45): служба прогріву карти застарілості вмикається/вимикається конфігом і стартує лише після
/// <c>ApplicationStarted</c> (не блокує старт). Мутація: прибрати перевірку <c>EnabledKey</c> у
/// <c>StaleMapWarmService.StartAsync</c> - червоний <c>Вимкнено_конфігом_...</c>.
/// </summary>
public sealed class StaleMapWarmServiceTests
{
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => started.Token;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => started.Cancel();

        public void StopApplication() => stopping.Cancel();

        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
        }
    }

    /// <summary>Фабрика областей, що лише сигналить про виклик і падає (служба має це пережити).</summary>
    private sealed class ProbeScopes : IServiceScopeFactory
    {
        public readonly TaskCompletionSource Called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref calls);
            Called.TrySetResult();
            throw new InvalidOperationException("probe");
        }
    }

    private static IConfiguration Config(string? enabled)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null
                ? []
                : new Dictionary<string, string?> { [StaleMapWarmService.EnabledKey] = enabled })
            .Build();

    [Fact]
    public async Task Вимкнено_конфігом_служба_не_береться_за_роботу()
    {
        var scopes = new ProbeScopes();
        using var lifetime = new Lifetime();
        var service = new StaleMapWarmService(scopes, lifetime, Config("false"), NullLogger<StaleMapWarmService>.Instance);

        await service.StartAsync(CancellationToken.None);
        lifetime.Start();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, scopes.Calls);
    }

    [Fact]
    public async Task Увімкнено_не_блокує_старт_і_береться_до_роботи_лише_після_ApplicationStarted_збій_не_валить()
    {
        var scopes = new ProbeScopes();
        using var lifetime = new Lifetime();
        var service = new StaleMapWarmService(scopes, lifetime, Config(null), NullLogger<StaleMapWarmService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(0, scopes.Calls);

        lifetime.Start();
        await scopes.Called.Task.WaitAsync(TimeSpan.FromSeconds(30));

        lifetime.StopApplication();
        await service.StopAsync(CancellationToken.None);
        Assert.True(scopes.Calls >= 1);
    }
}
