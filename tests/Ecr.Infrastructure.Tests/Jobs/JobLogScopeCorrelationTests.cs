using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// BE-08 (хвіст AN-67): обидва виконавці відкривають scope журналу з кореляцією постановника - саме за ним оператор
/// зв'язує всі рядки журналу задачі з запитом. Логер-перехоплювач записує стан кожного <c>BeginScope</c>.
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>BeginScope</c> у <c>JobWorker.ExecuteClaimedAsync</c> - 1-й червоний; у
/// <c>QuartzJobAdapter.Execute</c> - 2-й червоний; писати в scope нову кореляцію замість постановника - обидва червоні.
/// </remarks>
public sealed class JobLogScopeCorrelationTests
{
    private const string Correlation = "0123456789abcdef0123456789abcdef";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task JobWorker_scope_журналу_несе_кореляцію_постановника_і_ідентифікатор_задачі()
    {
        var logger = new ScopeCapturingLogger<JobWorker>();
        var jobId = $"scope-{Guid.NewGuid():N}";

        await RunWorkerOnceAsync(jobId, logger);

        var scope = Assert.Single(logger.Scopes, s => s.ContainsKey("CorrelationId"));
        Assert.Equal(Correlation, scope["CorrelationId"]);
        Assert.Equal(jobId, scope["JobId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task QuartzJobAdapter_scope_журналу_несе_кореляцію_з_даних_тригера()
    {
        var logger = new ScopeCapturingLogger<QuartzJobAdapter>();

        await RunQuartzAsync(logger);

        var scope = Assert.Single(logger.Scopes, s => s.ContainsKey("CorrelationId"));
        Assert.Equal(Correlation, scope["CorrelationId"]);
    }

    private static async Task RunQuartzAsync(ILogger<QuartzJobAdapter> logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<OkJob>();
        await using var provider = services.BuildServiceProvider();

        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(OkJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
            { QuartzJobScheduler.CorrelationKey, Correlation },
        };
        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey($"scope-{Guid.NewGuid():N}"));
        jobDetail.JobDataMap.Returns(jobData);
        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap());
        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuartzJobAdapter(provider, logger).Execute(context);
    }

    private static async Task RunWorkerOnceAsync(string jobId, ILogger<JobWorker> logger)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = new ClaimedJob(
            new JobClaimToken(jobId, Guid.NewGuid()), typeof(OkJob).FullName!, JobLanes.Default, null, null, 1, 0,
            Reclaimed: false, DateTime.UtcNow.AddMinutes(5), null, Correlation, null, 0);

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
        queue.FailAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
        {
            settled.TrySetResult();
            return Task.FromResult(true);
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => queue);
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<OkJob>();
        await using var provider = services.BuildServiceProvider();

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
    }

    /// <summary>Логер, що лише записує стан кожного відкритого scope (словник).</summary>
    private sealed class ScopeCapturingLogger<T> : ILogger<T>
    {
        private readonly List<IReadOnlyDictionary<string, object?>> scopes = [];

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Scopes
        {
            get
            {
                lock (scopes)
                {
                    return [.. scopes];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                var copy = pairs.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
                lock (scopes)
                {
                    scopes.Add(copy);
                }
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    public sealed class OkJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
    }
}
