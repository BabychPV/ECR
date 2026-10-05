// tests/Ecr.Infrastructure.Tests/Jobs/QuartzCoalescedRequeueLogTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-10 (аудит 2026-10-03): збій перепостановки «брудної» задачі після злиття —
/// єдиного місця, де злиття з виконуваною задачею не губить зміну (O1), — мусить
/// дійти до журналу служби, а не в <c>Trace</c> без слухачів.
/// </summary>
/// <remarks>Мутаційний доказ — в описі коміту.</remarks>
public sealed class QuartzCoalescedRequeueLogTests : IAsyncLifetime
{
    private const string Target = "doc7-p202601";

    private IScheduler? quartz;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (quartz is not null)
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "L2-10")]
    public async Task Збій_перепостановки_пишеться_в_журнал_Error()
    {
        var job = new GatedJob();
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton<IRecalculationJob>(job);
        await using var provider = services.BuildServiceProvider();

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        quartz = await factory.GetScheduler();
        quartz.JobFactory = new AdapterFactory(provider);

        // Перша постановка проходить; перепостановка після злиття падає на записі прогресу.
        var progress = Substitute.For<IJobProgressStore>();
        var calls = 0;
        progress.QueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(),
                Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<long?>())
            .Returns(_ => Interlocked.Increment(ref calls) == 1
                ? Task.CompletedTask
                : throw new InvalidOperationException("Транзієнтна відмова бази."));

        var log = new ListLogger();
        var jobs = new QuartzJobScheduler(
            factory, progress, provider.GetRequiredService<Ecr.Domain.Abstractions.IClock>(), logger: log);

        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        await quartz.Start();
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Злиття з виконуваною — позначка «брудна», перепостановка після завершення.
        await jobs.EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 2 }, CancellationToken.None);
        job.Release.TrySetResult();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (log.Errors.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var (message, exception) = Assert.Single(log.Errors);
        Assert.Contains(Target, message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception);
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Перше виконання чекає бар'єра.</summary>
    private sealed class GatedJob : IRecalculationJob
    {
        private int runs;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class ListLogger : ILogger<QuartzJobScheduler>
    {
        private readonly List<(string Message, Exception? Exception)> errors = [];

        public List<(string Message, Exception? Exception)> Errors
        {
            get { lock (errors) { return [.. errors]; } }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Error)
            {
                lock (errors)
                {
                    errors.Add((formatter(state, exception), exception));
                }
            }
        }
    }
}
