// tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationCoalesceQuartzTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Quartz.Spi;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// O1 у режимі Quartz (типовому): інкрементні задачі формул однієї цілі
/// зливаються, і задача, взята адаптером, несе комірки ВСІХ злитих постановок.
/// </summary>
/// <remarks>
/// Мутації: (1) <c>QuartzJobScheduler.EnqueueCoalescedAsync</c> без гілки злиття
/// масиву (звичайне <c>CoalesceAsync</c>) — червоний «дві постановки» (виконано
/// лише рядок 1); (2) адаптер бере payload з <c>JobDataMap</c>, а не з
/// <c>QuartzPayloadMerges.Take</c> — той самий тест червоний.
/// </remarks>
public sealed class FormulaRecalculationCoalesceQuartzTests : IAsyncLifetime
{
    private const string Target = "doc1-p202601-formula-u7";

    private readonly List<IScheduler> schedulers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var scheduler in schedulers)
        {
            await scheduler.Shutdown(waitForJobsToComplete: false).ConfigureAwait(false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Дві_постановки_одна_задача_і_адаптер_виконує_комірки_обох()
    {
        var recorder = new RecordingFormulaJob();
        var (jobs, quartz) = await SchedulerAsync(recorder);

        var first = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(Target, Payload(1), CancellationToken.None);
        var second = await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(Target, Payload(2, 3), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal([first], await KeysAsync(quartz));

        await quartz.Start();
        var executed = await recorder.Executed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([1L, 2L, 3L], RowIdsOf(executed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task П_ятдесят_постановок_одна_задача_з_усіма_комірками()
    {
        var recorder = new RecordingFormulaJob();
        var (jobs, quartz) = await SchedulerAsync(recorder);

        for (var i = 1; i <= 50; i++)
        {
            await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(Target, Payload(i), CancellationToken.None);
        }

        Assert.Single(await KeysAsync(quartz));

        await quartz.Start();
        var executed = await recorder.Executed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), RowIdsOf(executed));
    }

    // ── Світ ─────────────────────────────────────────────────────────────────

    private static object Payload(params long[] rowIds)
        => new
        {
            DocumentId = 1L,
            TableInstanceId = 1L,
            PeriodKey = 202601,
            Cells = rowIds.Select(r => new { RowId = r, ColumnDefId = 7 }).ToArray(),
        };

    private static List<long> RowIdsOf(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);
        return [.. json.RootElement.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("rowId").GetInt64()).Order()];
    }

    private async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> SchedulerAsync(RecordingFormulaJob recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton<IFormulaRecalculationJob>(recorder);
        var provider = services.BuildServiceProvider();

        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        var scheduler = await factory.GetScheduler().ConfigureAwait(false);
        scheduler.JobFactory = new AdapterFactory(provider);
        schedulers.Add(scheduler);

        return (new QuartzJobScheduler(factory), scheduler);
    }

    private static async Task<List<string>> KeysAsync(IScheduler scheduler)
        => [.. (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()).ConfigureAwait(false)).Select(k => k.Name)];

    private sealed class AdapterFactory(IServiceProvider provider) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Задача формул, що лише запам'ятовує payload, з яким її виконали.</summary>
    private sealed class RecordingFormulaJob : IFormulaRecalculationJob
    {
        public TaskCompletionSource<string?> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            Executed.TrySetResult(payload as string);
            return Task.CompletedTask;
        }
    }
}
