// tests/Ecr.Api.Tests/Startup/StoppingInterruptsJobsTests.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using Ecr.Api.Startup;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// U8: зупинка застосунку надсилає скасування задачам, що виконуються.
/// </summary>
/// <remarks>
/// ⚠ Підписка на зупинку — не токен <c>ApplicationStopping</c>, а
/// <see cref="RecurringScheduleService.StopAsync"/> (hosted service, який хост
/// зупиняє РАНІШЕ за <c>QuartzHostedService</c>). Саме він кличе
/// <c>QuartzJobScheduler.InterruptAllAsync</c>. Сам <c>InterruptAllAsync</c>
/// перевіряє <c>JobLifecycleTests</c> (Infrastructure), але викликом напряму —
/// тобто прибраний із зупинки виклик там не видно.
/// <para>
/// Планувальник — справжній Quartz у пам'яті, ЗАПУЩЕНИЙ, задача справді
/// виконується; бази не треба. DI — той самий вигляд, що в застосунку:
/// <see cref="IBackgroundJobScheduler"/> scoped над фабрикою.
/// </para>
/// <para>
/// Мутаційний доказ: у <c>RecurringScheduleService.StopAsync</c> прибрати
/// виклик <c>InterruptRunningJobsAsync</c> → задача не отримує скасування і
/// тримається свої 30 с, тест червоний (прогнано).
/// </para>
/// </remarks>
public sealed class StoppingInterruptsJobsTests
{
    private const string RunKey = "test.runId";

    /// <summary>Стан прогонів за ідентифікатором: Quartz сам створює екземпляр задачі.</summary>
    private static readonly ConcurrentDictionary<string, Run> Runs = new();

    private sealed class Run
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>«Довгий перерахунок»: чекає скасування або 30 с.</summary>
    public sealed class LongJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            var run = Runs[context.MergedJobDataMap.GetString(RunKey)!];
            run.Started.TrySetResult();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                run.Cancelled.TrySetResult();
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "U8")]
    public async Task Зупинка_застосунку_надсилає_скасування_задачі_що_виконується()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-stopping-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var quartz = await factory.GetScheduler();

        var runId = Guid.NewGuid().ToString("N");
        var run = Runs.GetOrAdd(runId, _ => new Run());

        var services = new ServiceCollection();
        services.AddScoped<IBackgroundJobScheduler>(_ => new QuartzJobScheduler(factory));
        await using var provider = services.BuildServiceProvider();

        // ApplicationStarted ніколи не настає: постановка розкладів і цикл
        // прибирання тут не потрібні й не стартують.
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(CancellationToken.None);

        using var service = new RecurringScheduleService(
            provider,
            lifetime,
            NullLogger<RecurringScheduleService>.Instance,
            new ConfigurationBuilder().Build());

        try
        {
            await service.StartAsync(CancellationToken.None);

            await quartz.ScheduleJob(
                JobBuilder.Create<LongJob>().UsingJobData(RunKey, runId).Build(),
                TriggerBuilder.Create().StartNow().Build());
            await quartz.Start();
            await run.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var watch = Stopwatch.StartNew();
            await service.StopAsync(CancellationToken.None);

            // Задача, якій не надіслали сигналу, тримається 30 с — чекаємо 10.
            var cancelled = await Task.WhenAny(run.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(
                cancelled == run.Cancelled.Task,
                "Зупинка застосунку не надіслала скасування задачі, що виконується (InterruptAllAsync не викликано).");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Зупинка забрала {watch.Elapsed}.");
        }
        finally
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
            Runs.TryRemove(runId, out _);
        }
    }
}
