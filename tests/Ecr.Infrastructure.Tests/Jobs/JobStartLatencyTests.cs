using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Затримка «поставили в чергу → задача почала виконуватись» доходить до
/// метрики (<c>ФВ-12.2</c>, <c>tz/08</c> §8.3).
/// </summary>
/// <remarks>
/// ⛔ ПРИВІД. `ФВ-12.2` задає межу 10 с (авто) і 5 с (ручний запуск), а міряло
/// цю величину до 2026-09-18 НІЩО: у `tz/08` числа не було, і серед критеріїв
/// гейта BR-07 його теж не було.
///
/// ⚠ Момент постановки в базі Є (`QueueAsync` створює рядок зі станом
/// `Queued`), але він не переживає старту: `Begin` перезаписує `UpdatedAt`.
/// Тому мітка їде в `JobDataMap` — і саме її шлях від постановника до
/// виконавця перевіряє четвертий тест нижче.
///
/// ⚠ Реальний Quartz-таймер тут не потрібен і шкідливий — з тієї самої
/// причини, що в <c>QuartzJobAdapterRetryTests</c>: чекати секунди в
/// модульному тесті означало б або сповільнити прогін, або зробити його
/// крихким до годинника машини. Замість цього <see cref="QuartzJobAdapter"/>
/// викликається напряму з контекстом, у якому мітка постановки вже стоїть.
/// </remarks>
public sealed class JobStartLatencyTests
{
    private const string JobId = "latency-job-1";

    private static readonly DateTime EnqueuedAt = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Порожня задача: предмет тут — не її робота, а чекання перед нею.</summary>
    private sealed class NoopJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-12.1")]
    public async Task Затримка_старту_доходить_до_метрики()
    {
        var metrics = Substitute.For<IJobStartMetrics>();

        // Задача чекала в черзі 3.5 секунди.
        await ExecuteAsync(metrics, startedAt: EnqueuedAt.AddMilliseconds(3500), withStamp: true);

        metrics.Received(1).RecordStartLatency(3500d, typeof(NoopJob).FullName!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-12.1")]
    public async Task Без_мітки_постановки_метрика_НЕ_пишеться()
    {
        var metrics = Substitute.For<IJobStartMetrics>();

        // ⛔ Задача за розкладом: її ніхто не «ставив», момент задає крон.
        // Нуль у гістограмі був би не «швидко», а вигадкою — і зіпсував би
        // перцентиль саме тим, що виглядає як ідеальний замір.
        await ExecuteAsync(metrics, startedAt: EnqueuedAt.AddMilliseconds(3500), withStamp: false);

        metrics.DidNotReceiveWithAnyArgs().RecordStartLatency(default, default!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-12.1")]
    public async Task Розбіжність_годинників_не_дає_відʼємної_затримки()
    {
        var metrics = Substitute.For<IJobStartMetrics>();

        // Годинник виконавця відстає від годинника постановника на секунду.
        await ExecuteAsync(metrics, startedAt: EnqueuedAt.AddMilliseconds(-1000), withStamp: true);

        metrics.DidNotReceiveWithAnyArgs().RecordStartLatency(default, default!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-12.1")]
    public async Task Мітку_ставить_САМ_планувальник_а_не_тест()
    {
        // ⛔ Три тести вище беруть ключ мітки з тієї самої константи, що й
        // продукт, — тож розбіжність «хто пише» проти «хто читає» вони
        // побачити НЕ МОЖУТЬ: перейменування константи лишає їх зеленими
        // (перевірено мутацією). Цей тест закриває саме той зазор: мітку в
        // деталь кладе справжній `EnqueueAsync`, а дістає її справжній
        // адаптер.
        var factory = new Quartz.Impl.StdSchedulerFactory(
            new System.Collections.Specialized.NameValueCollection
            {
                ["quartz.scheduler.instanceName"] = $"ecr-latency-{Guid.NewGuid():N}",
                ["quartz.threadPool.threadCount"] = "1",
            });

        // ⚠ Планувальник НЕ запускається: предмет — вміст деталі, а не її
        // виконання. Запущений таймер додав би секунди очікування й крихкість
        // до годинника машини.
        var scheduler = await factory.GetScheduler();
        var jobs = new QuartzJobScheduler(factory, progress: null, clock: new TestClock(EnqueuedAt));

        var jobId = await jobs.EnqueueAsync<IFormulaRecalculationJob>(new { DocumentId = 1L }, CancellationToken.None);
        var detail = await scheduler.GetJobDetail(new JobKey(jobId));

        Assert.NotNull(detail);

        var metrics = Substitute.For<IJobStartMetrics>();
        var services = new ServiceCollection();
        services.AddSingleton(metrics);
        services.AddSingleton<IClock>(new TestClock(EnqueuedAt.AddMilliseconds(1200)));
        services.AddScoped<IFormulaRecalculationJob, NoopFormulaJob>();

        await using var provider = services.BuildServiceProvider();
        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(detail);
        context.Trigger.Returns(Substitute.For<ITrigger>());
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);
        context.Trigger.JobDataMap.Returns(new JobDataMap());

        await adapter.Execute(context);

        metrics.Received(1).RecordStartLatency(1200d, typeof(IFormulaRecalculationJob).FullName!);
    }

    /// <summary>Порожня задача під маркером — щоб адаптер її розв'язав.</summary>
    private sealed class NoopFormulaJob : IFormulaRecalculationJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static async Task ExecuteAsync(IJobStartMetrics metrics, DateTime startedAt, bool withStamp)
    {
        var services = new ServiceCollection();
        services.AddSingleton(metrics);
        services.AddSingleton<IClock>(new TestClock(startedAt));
        services.AddScoped<NoopJob>();

        await using var provider = services.BuildServiceProvider();
        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(NoopJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        if (withStamp)
        {
            jobData.Put(QuartzJobScheduler.EnqueuedAtKey, EnqueuedAt.Ticks);
        }

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey(JobId));
        jobDetail.JobDataMap.Returns(jobData);

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap());

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        await adapter.Execute(context);
    }
}
