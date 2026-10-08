// tests/Ecr.Infrastructure.Tests/Jobs/CoalescedRequeueScopeQuartzTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// N-5: відкладена перепостановка після злиття НЕ залежить від життя scope запиту,
/// що її замовив. <see cref="QuartzJobScheduler"/> у застосунку scoped (його сховище
/// прогресу — на <c>DbContext</c> запиту), а запит закінчується раніше за задачу:
/// замикання перепостановки, що тримало планувальник запиту, у момент завершення задачі
/// писало прогрес у знищений контекст (<see cref="ObjectDisposedException"/>), слухач
/// ковтав виняток, і зміна, прийнята під час прогону, лишалась непорахованою.
/// </summary>
/// <remarks>
/// Тести без пауз на «дочекатися»: сигнали — <see cref="TaskCompletionSource"/>, таймаут
/// лише запобіжний. Доказ червоного — тест падає на коді до фіксу (порядок комітів).
/// </remarks>
public sealed class CoalescedRequeueScopeQuartzTests : IAsyncLifetime
{
    private const string Target = "doc5-p202601";

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

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
    public async Task Перепостановка_переживає_знищення_scope_запиту_що_злив()
    {
        var env = await StartAsync(failQueueCalls: []);

        using (var scope1 = env.Provider.CreateScope())
        {
            await scope1.ServiceProvider.GetRequiredService<QuartzJobScheduler>()
                .EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        }

        await quartz!.Start();
        await env.Job.Started.Task.WaitAsync(Safety);

        // Запит PATCH: зливається з виконуваною задачею й завершується (scope знищено) ДО її кінця.
        using (var scope2 = env.Provider.CreateScope())
        {
            await scope2.ServiceProvider.GetRequiredService<QuartzJobScheduler>()
                .EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 2 }, CancellationToken.None);
        }

        env.Job.Release.TrySetResult();

        await env.AwaitOrErrorAsync(env.SecondQueued.Task);

        Assert.Equal(2, env.QueuedIds.Count);
        Assert.Empty(env.Log.Errors);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Кілька_злиттів_із_різних_знищених_scope_дають_одну_перепостановку_з_останнім_payload()
    {
        var env = await StartAsync(failQueueCalls: []);

        using (var scope1 = env.Provider.CreateScope())
        {
            await scope1.ServiceProvider.GetRequiredService<QuartzJobScheduler>()
                .EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        }

        await quartz!.Start();
        await env.Job.Started.Task.WaitAsync(Safety);

        for (var n = 2; n <= 4; n++)
        {
            using var scope = env.Provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<QuartzJobScheduler>()
                .EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n }, CancellationToken.None);
        }

        env.Job.Release.TrySetResult();

        await env.AwaitOrErrorAsync(env.Job.SecondRan.Task);

        // Усі задачі, що виконалися: перша й одна перепостановка з payload ОСТАННЬОГО злиття.
        Assert.Equal(["{\"n\":1}", "{\"n\":4}"], env.Job.Payloads);
        Assert.Equal(2, env.QueuedIds.Count);
        Assert.Empty(env.Log.Errors);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Транзієнтний_збій_перепостановки_повторюється_і_зміна_не_губиться()
    {
        // Виклик QueueAsync №2 (перша спроба перепостановки) падає, №3 проходить.
        var env = await StartAsync(failQueueCalls: [2]);

        using (var scope1 = env.Provider.CreateScope())
        {
            await scope1.ServiceProvider.GetRequiredService<QuartzJobScheduler>()
                .EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 1 }, CancellationToken.None);
        }

        await quartz!.Start();
        await env.Job.Started.Task.WaitAsync(Safety);

        using (var scope2 = env.Provider.CreateScope())
        {
            await scope2.ServiceProvider.GetRequiredService<QuartzJobScheduler>()
                .EnqueueCoalescedAsync<IRecalculationJob>(Target, new { n = 2 }, CancellationToken.None);
        }

        env.Job.Release.TrySetResult();

        await env.AwaitOrErrorAsync(env.Job.SecondRan.Task);

        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], env.Job.Payloads);
        Assert.Empty(env.Log.Errors);
    }

    private async Task<Env> StartAsync(int[] failQueueCalls)
    {
        var job = new GatedJob();
        var log = new ListLogger();
        var queuedIds = new List<string>();
        var secondQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(
            new TestClock(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton<IRecalculationJob>(job);
        services.AddSingleton<ILogger<QuartzJobScheduler>>(log);

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "2",
        });
        quartz = await factory.GetScheduler();
        services.AddSingleton<ISchedulerFactory>(factory);

        // Як у застосунку: сховище прогресу — scoped і «вмирає» разом зі scope (DbContext запиту).
        services.AddScoped<ScopeProbe>();
        services.AddScoped<IJobProgressStore>(sp =>
        {
            var probe = sp.GetRequiredService<ScopeProbe>();
            var store = Substitute.For<IJobProgressStore>();
            store.QueueAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(),
                    Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<long?>())
                .Returns(call =>
                {
                    probe.ThrowIfDisposed();
                    int n;
                    lock (queuedIds)
                    {
                        queuedIds.Add(call.ArgAt<string>(0));
                        n = ++calls;
                    }

                    if (failQueueCalls.Contains(n))
                    {
                        throw new InvalidOperationException("Транзієнтна відмова бази.");
                    }

                    if (n == 2)
                    {
                        secondQueued.TrySetResult();
                    }

                    return Task.CompletedTask;
                });
            return store;
        });

        // Реєстрація дзеркалить DependencyInjection: scoped, із кореневою фабрикою scope.
        services.AddScoped(sp => new QuartzJobScheduler(
            sp.GetService<ISchedulerFactory>(),
            sp.GetService<IJobProgressStore>(),
            sp.GetService<Ecr.Domain.Abstractions.IClock>(),
            null,
            sp.GetService<ILogger<QuartzJobScheduler>>(),
            sp.GetService<IServiceScopeFactory>()));

        var provider = services.BuildServiceProvider();
        quartz.JobFactory = new AdapterFactory(provider);

        return new Env(provider, job, log, queuedIds, secondQueued);
    }

    private sealed record Env(
        ServiceProvider Provider, GatedJob Job, ListLogger Log, List<string> QueuedIdsRaw, TaskCompletionSource SecondQueued)
    {
        public List<string> QueuedIds
        {
            get { lock (QueuedIdsRaw) { return [.. QueuedIdsRaw]; } }
        }

        /// <summary>Чекає сигнал, але раніше за таймаут падає з текстом помилки слухача (збій перепостановки).</summary>
        public async Task AwaitOrErrorAsync(Task signal)
        {
            var first = await Task.WhenAny(signal, Log.FirstError.Task).WaitAsync(Safety);
            Assert.True(ReferenceEquals(first, signal), "слухач злиття записав помилку: " + string.Join(" | ", Log.Errors));
            await signal;
        }
    }

    private sealed class ScopeProbe : IDisposable
    {
        private volatile bool disposed;

        public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

        public void Dispose() => disposed = true;
    }

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Перше виконання чекає бар'єра, решта проходять одразу; пам'ятає payload кожного.</summary>
    private sealed class GatedJob : IRecalculationJob
    {
        private readonly object gate = new();
        private readonly List<string?> payloads = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string?> Payloads
        {
            get { lock (gate) { return [.. payloads]; } }
        }

        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            int count;
            lock (gate)
            {
                payloads.Add(payload as string);
                count = payloads.Count;
            }

            if (count == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                SecondRan.TrySetResult();
            }
        }
    }

    private sealed class ListLogger : ILogger<QuartzJobScheduler>
    {
        private readonly List<string> errors = [];

        public TaskCompletionSource FirstError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Errors
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
                    errors.Add(formatter(state, exception));
                }

                FirstError.TrySetResult();
            }
        }
    }
}
