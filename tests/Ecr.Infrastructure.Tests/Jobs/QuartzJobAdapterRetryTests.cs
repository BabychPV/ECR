// tests/Ecr.Infrastructure.Tests/Jobs/QuartzJobAdapterRetryTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Директива №11, T10 #40: задача без <c>RetryPolicy</c> провалювалась
/// НАЗАВЖДИ на першій-ліпшій транзієнтній помилці (обрив з'єднання з SQL
/// Server, дедлок) — ретрая не було жодного, і оператор мусив ставити нову
/// задачу вручну щоразу.
/// </summary>
/// <remarks>
/// ⛔ D-134: задача, що ЗАВЖДИ провалюється, мусить зупинитися рівно на
/// <see cref="QuartzJobAdapter.MaxRetryAttempts"/> — не менше (передчасна
/// відмова ховала б транзієнтну помилку від легального відновлення) і не
/// більше (лічильник, який не зупиняється, — це чергу, що спамить сама себе
/// вічно на систематично зламаній задачі).
/// <para>
/// ⚠ Реальний Quartz-таймер тут не потрібен і шкідливий: базова затримка
/// ретрая — секунди, і чекати на неї в модульному тесті означало б або
/// уповільнити прогін, або зробити тест крихким до годинника машини. Замість
/// цього <see cref="QuartzJobAdapter.Execute"/> викликається НАПРЯМУ кілька
/// разів поспіль із <c>IJobExecutionContext</c>, що симулює те, що Quartz сам
/// передав би на кожен наступний триґер: лічильник спроби в даних триґера.
/// </para>
/// </remarks>
public sealed class QuartzJobAdapterRetryTests
{
    private const string JobId = "retry-job-1";

    /// <summary>Задача, що провалюється завжди — саме сценарій D-134.</summary>
    private sealed class AlwaysFailingJob : IBackgroundJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
            => throw new InvalidOperationException("Симуляція транзієнтної помилки.");
    }

    private static (QuartzJobAdapter Adapter, IJobProgressStore Progress) Adapter()
    {
        var progress = Substitute.For<IJobProgressStore>();

        var services = new ServiceCollection();
        services.AddSingleton(progress);
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc)));
        services.AddScoped<AlwaysFailingJob>();
        var provider = services.BuildServiceProvider();

        return (new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance), progress);
    }

    /// <summary>Будує контекст виконання так, наче Quartz щойно відпустив N-ту спробу.</summary>
    private static (IJobExecutionContext Context, IScheduler Scheduler) ContextAt(int attempt)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(AlwaysFailingJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey(JobId));
        jobDetail.JobDataMap.Returns(jobData);

        var triggerData = new JobDataMap();
        if (attempt > 0)
        {
            triggerData.Put(QuartzJobScheduler.RetryAttemptKey, attempt.ToString(CultureInfo.InvariantCulture));
        }

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(triggerData);

        var scheduler = Substitute.For<IScheduler>();

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);
        context.CancellationToken.Returns(CancellationToken.None);

        return (context, scheduler);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Провал_у_межах_ліміту_планує_ретрай_і_не_позначає_Failed()
    {
        var (adapter, progress) = Adapter();

        for (var attempt = 0; attempt < QuartzJobAdapter.MaxRetryAttempts; attempt++)
        {
            var (context, scheduler) = ContextAt(attempt);

            // ⚠ Ретрай не кидає JobExecutionException: Quartz не повинен
            // трактувати це як звичайний провал прогону (той дав би повторний
            // виклик листенерів провалу, яких тут не існує) — контроль
            // повністю на нашій стороні (новий триґер, запланований явно).
            await adapter.Execute(context);

            // ⛔ Саме тут ловиться регресія «лічильник не зупиняється»: якби
            // перевірку `attempt < MaxRetryAttempts` прибрали чи послабили,
            // цей виклик усе одно пройшов би без винятку на КОЖНІЙ ітерації —
            // відрізнити безмежний ретрай від межованого можна лише
            // перевіркою нижче, на КРОЦІ, де ліміт мав спрацювати.
            await scheduler.Received(1).ScheduleJob(
                Arg.Is<ITrigger>(t => t.JobDataMap.GetString(QuartzJobScheduler.RetryAttemptKey)
                                       == (attempt + 1).ToString(CultureInfo.InvariantCulture)),
                Arg.Any<CancellationToken>());

            await scheduler.DidNotReceive().DeleteJob(Arg.Any<JobKey>(), Arg.Any<CancellationToken>());
        }

        await progress.DidNotReceive().FinishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Провал_після_вичерпання_ліміту_зупиняється_і_лишає_задачу_для_перезапуску()
    {
        var (adapter, progress) = Adapter();

        // Рівно на межі: усі дозволені ретраї вже відбулись (attempt дорівнює
        // ліміту), і це остання, ЧЕТВЕРТА спроба виконати задачу.
        var (context, scheduler) = ContextAt(QuartzJobAdapter.MaxRetryAttempts);

        var thrown = await Assert.ThrowsAsync<JobExecutionException>(() => adapter.Execute(context));

        Assert.IsType<InvalidOperationException>(thrown.InnerException);

        await progress.Received(1).FinishAsync(
            JobId, "Failed", Arg.Is<string?>(m => m != null && m.Contains("транзієнтної")),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        // ⛔ Жодного нового триґера — саме це і є «зупинка на межі», а не
        // «спроба нескінченно».
        await scheduler.DidNotReceive().ScheduleJob(Arg.Any<ITrigger>(), Arg.Any<CancellationToken>());

        // ⚠ Деталь задачі НЕ видаляється: дурабельна саме на цей випадок, щоб
        // IBackgroundJobScheduler.RestartAsync мав що перезапускати.
        await scheduler.DidNotReceive().DeleteJob(Arg.Any<JobKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-40")]
    public async Task Успіх_прибирає_дурабельну_деталь_задачі()
    {
        var progress = Substitute.For<IJobProgressStore>();
        var succeeding = Substitute.For<IBackgroundJob>();
        succeeding.ExecuteAsync(default, default!, default).ReturnsForAnyArgs(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(progress);
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(
            new TestClock(new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc)));
        services.AddSingleton(succeeding);
        var provider = services.BuildServiceProvider();

        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(IBackgroundJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        var jobDetail = Substitute.For<IJobDetail>();
        var key = new JobKey(JobId);
        jobDetail.Key.Returns(key);
        jobDetail.JobDataMap.Returns(jobData);

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap());

        var scheduler = Substitute.For<IScheduler>();

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(scheduler);
        context.CancellationToken.Returns(CancellationToken.None);

        await adapter.Execute(context);

        await scheduler.Received(1).DeleteJob(key, Arg.Any<CancellationToken>());
    }
}
