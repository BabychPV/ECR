// tests/Ecr.Infrastructure.Tests/Jobs/JobQueueDepthMetricTests.cs
using System.Diagnostics.Metrics;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Observability;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>ecr.jobs.queue_depth</c> (B5.10): gauge читає лише кеш, який раз на інтервал оновлює
/// <see cref="JobQueueDepthSampler"/>; значення збігається з рядками черги в базі.
/// </summary>
/// <remarks>
/// ⚠ Кеш статичний (як і Meter), тож усі тести — в одному класі колекції <c>SqlServer</c>
/// (послідовна): паралельний запис у кеш інших класів неможливий.
/// Мутація: у <c>JobQueueDepthSampler.RefreshAsync</c> замість кешу читати базу в callback
/// gauge (прибрати кеш) — перший тест червоний (запитів 5, а не 1).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class JobQueueDepthMetricTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Читання_gauge_не_ходить_у_джерело_а_віддає_кеш_останнього_оновлення()
    {
        var source = new CountingSource([new QueueDepthPoint(JobLanes.Default, "Queued", 7)]);
        var sampler = new JobQueueDepthSampler(
            source, new JobQueueDepthOptions(TimeSpan.FromMinutes(5)), NullLogger<JobQueueDepthSampler>.Instance);

        await sampler.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, source.Calls);

        for (var i = 0; i < 5; i++)
        {
            var seen = Observe();
            Assert.Equal(7, seen[(JobLanes.Default, "Queued")]);
            Assert.Equal(0, seen[(JobLanes.Recalc, "Running")]);
        }

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Значення_збігається_з_кількістю_задач_у_черзі_за_лейном_і_станом()
    {
        await EnqueueAsync();
        await EnqueueAsync();
        await EnqueueAsync();
        await using (var host = NewHost())
        {
            await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Recalc, "{}", Target()), CancellationToken.None);
            Assert.NotNull(await host.ClaimAsync());
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        await using var provider = services.BuildServiceProvider();
        var sampler = new JobQueueDepthSampler(
            new DbJobQueueDepthSource(provider.GetRequiredService<IServiceScopeFactory>()),
            new JobQueueDepthOptions(TimeSpan.FromMinutes(5)),
            NullLogger<JobQueueDepthSampler>.Instance);

        await sampler.RefreshAsync(CancellationToken.None);

        var seen = Observe();
        Assert.Equal(2, seen[(JobLanes.Default, "Queued")]);
        Assert.Equal(1, seen[(JobLanes.Default, "Running")]);
        Assert.Equal(1, seen[(JobLanes.Recalc, "Queued")]);
        Assert.Equal(0, seen[(JobLanes.Recalc, "Running")]);
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public async Task Служба_оновлює_кеш_одразу_на_старті_а_збій_джерела_кеш_не_ламає()
    {
        var source = new CountingSource([new QueueDepthPoint(JobLanes.Recalc, "Running", 3)]);
        var sampler = new JobQueueDepthSampler(
            source, new JobQueueDepthOptions(TimeSpan.FromMinutes(5)), NullLogger<JobQueueDepthSampler>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        await source.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await sampler.StopAsync(CancellationToken.None);
        Assert.Equal(3, Observe()[(JobLanes.Recalc, "Running")]);

        source.Fail = true;
        await sampler.RefreshAsync(CancellationToken.None);

        Assert.Equal(3, Observe()[(JobLanes.Recalc, "Running")]);
    }

    private static Dictionary<(string Lane, string State), long> Observe()
    {
        var seen = new Dictionary<(string, string), long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == InfrastructureMetrics.MeterName
                && instrument.Name == InfrastructureMetrics.JobsQueueDepth)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? lane = null;
            string? state = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "lane")
                {
                    lane = (string?)tag.Value;
                }
                else if (tag.Key == "state")
                {
                    state = (string?)tag.Value;
                }
            }

            seen[(lane!, state!)] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return seen;
    }

    private sealed class CountingSource(IReadOnlyList<QueueDepthPoint> points) : IJobQueueDepthSource
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public bool Fail { get; set; }

        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<QueueDepthPoint>> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (Fail)
            {
                throw new InvalidOperationException("база недоступна");
            }

            FirstRead.TrySetResult();
            return Task.FromResult(points);
        }
    }
}
