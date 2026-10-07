// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerCorrelationTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// SEC (TIER2): <see cref="JobWorker"/> відкриває <see cref="JobCorrelation"/> тим самим id, що
/// стоїть у scope журналу задачі, — код усередині задачі (текст провалу прогону
/// обслуговування, <c>PeriodStateJob</c>) бачить кореляцію, за якою оператор знайде рядок журналу.
/// </summary>
/// <remarks>
/// Парний до <c>JobAttemptCorrelationTests</c> (там — Quartz-адаптер). Без бази й без годинників:
/// черга — підробка, синхронізація — через <see cref="TaskCompletionSource"/>.
/// Мутація: прибрати <c>using var jobCorrelation = JobCorrelation.Begin(correlationId)</c> у
/// <c>JobWorker.ExecuteClaimedAsync</c> — обидва тести червоні (<c>Seen</c> = <c>null</c>).
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class JobWorkerCorrelationTests
{
    private static readonly string JobCode = typeof(CorrelationProbeJob).FullName!;

    [Fact]
    public async Task Кореляція_постановника_видна_задачі_і_збігається_зі_scope_журналу()
    {
        var (seen, scopeIds) = await RunOneAsync(claimCorrelation: "worker0123456789abcdef0123456789");

        Assert.Equal("worker0123456789abcdef0123456789", seen);
        Assert.Equal(["worker0123456789abcdef0123456789"], scopeIds);
        Assert.Null(JobCorrelation.Current); // не протікає за межі виконання задачі
    }

    [Fact]
    public async Task Без_кореляції_постановника_генерується_нова_і_збігається_зі_scope_журналу()
    {
        var (seen, scopeIds) = await RunOneAsync(claimCorrelation: null);

        // Нова, а не порожня: прогін за розкладом теж має бути знайденим у журналі.
        Assert.Matches("^[0-9a-f]{32}$", seen);
        Assert.Equal([seen!], scopeIds);
        Assert.Null(JobCorrelation.Current);
    }

    private static async Task<(string? Seen, List<string> ScopeIds)> RunOneAsync(string? claimCorrelation)
    {
        var executed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var claimed = new ClaimedJob(
            new JobClaimToken("corr-1", Guid.NewGuid()), JobCode, JobLanes.Default, null, null, 1, 0,
            Reclaimed: false, DateTime.UtcNow.AddMinutes(5), null, claimCorrelation, null, 0);

        var queue = Substitute.For<IJobQueue>();
        var handedOut = 0;
        queue.ClaimAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            Task.FromResult(Interlocked.Increment(ref handedOut) == 1 ? claimed : null));
        queue.RenewAsync(default!, default, default).ReturnsForAnyArgs(Task.FromResult(LeaseState.Held));
        queue.CompleteAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            settled.TrySetResult();
            return Task.FromResult(true);
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => queue);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(executed);
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<CorrelationProbeJob>();
        await using var provider = services.BuildServiceProvider();

        var logger = new ScopeCapturingLogger();
        using var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new JobWorkerOptions
            {
                Lanes = JobLanes.All,
                Role = JobProgressStore.CurrentRole,
                PollInterval = TimeSpan.FromMilliseconds(10),
            },
            new JobQueueSignal(),
            logger);

        await worker.StartAsync(CancellationToken.None);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync(CancellationToken.None);

        return (await executed.Task, logger.CorrelationIds());
    }

    /// <summary>Задача-зонд: запам'ятовує кореляцію, яку бачить усередині виконання.</summary>
    public sealed class CorrelationProbeJob(TaskCompletionSource<string?> executed) : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            executed.TrySetResult(JobCorrelation.Current);
            return Task.CompletedTask;
        }
    }

    /// <summary>Журнал, що зберігає стан відкритих scope (там лежить <c>CorrelationId</c>).</summary>
    private sealed class ScopeCapturingLogger : ILogger<JobWorker>
    {
        private readonly List<object> _scopes = [];

        public List<string> CorrelationIds()
        {
            lock (_scopes)
            {
                return _scopes
                    .OfType<IReadOnlyDictionary<string, object>>()
                    .Where(d => d.ContainsKey("CorrelationId"))
                    .Select(d => (string)d["CorrelationId"])
                    .ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            lock (_scopes)
            {
                _scopes.Add(state);
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
