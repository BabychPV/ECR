// tests/Ecr.Api.Tests/RecurringTriggersInTestHostTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Сторож тестової інфраструктури: у типовому <see cref="EcrApiFactory"/> розклади (cron) стоять у
/// планувальнику, але НЕ спрацьовують; явний запуск задачі й разова постановка виконуються.
/// </summary>
/// <remarks>
/// ⛔ Корінь, який сторож тримає закритим. Тестовий застосунок піднімає справжній Quartz і
/// <c>RecurringScheduleService</c>, тож щогодинний <c>NotificationJob</c> (hh:05) і нічні перевірки
/// (02:15) виконувались у будь-якому хості, що прожив цю межу, — проти спільної бази колекції.
/// 23.10 (прогін 40007519316, api-2) зведення failures = 2, sent = 0 пожовтило <c>/health/ready</c>
/// і завалило <c>HealthTests</c> лише тому, що той ішов після 19:05.
/// <para>
/// ⚠ Доказ поведінковий, а не «слухач зареєстровано»: зонд ставиться cron «щосекунди» і має
/// ФАКТИЧНО спрацювати й бути відхиленим (запис у <see cref="RecurringTriggerVeto.Vetoed"/>), а
/// задача — не виконатися. Без вето (прибрати <c>RecurringTriggerVeto.Register</c> з
/// <see cref="EcrApiFactory"/>) перший тест червоний: слухача немає, вето не настає, зонд виконується.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RecurringTriggersInTestHostTests(SqlServerFixture sql)
{
    /// <summary>Скільки чекати подію планувальника (старт, спрацювання).</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Лічильник прогонів зонда — один на застосунок.</summary>
    public sealed class ProbeRuns
    {
        private int count;

        /// <summary>Скільки разів зонд виконався.</summary>
        public int Count => Volatile.Read(ref count);

        /// <summary>Фіксує прогін.</summary>
        public void Hit() => Interlocked.Increment(ref count);
    }

    /// <summary>Задача-зонд: лише рахує прогони. Резолвиться адаптером Quartz за повним іменем типу.</summary>
    public sealed class ProbeJob(ProbeRuns runs) : IBackgroundJob
    {
        /// <inheritdoc />
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            runs.Hit();
            return Task.CompletedTask;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розклади_стоять_але_cron_у_тестовому_застосунку_не_спрацьовує()
    {
        using var app = NewApp();
        var scheduler = await StartedSchedulerAsync(app);

        // Стан, якого вимагає JobsHealthCheck: планувальник запущено, не в Standby, розклади є.
        Assert.True(scheduler.IsStarted, "Планувальник тестового застосунку не запущено.");
        Assert.False(scheduler.InStandbyMode, "Планувальник у Standby — разові задачі теж не виконувались би.");

        var recurring = await RecurringTriggersAsync(scheduler);
        var notification = Assert.Single(
            recurring, t => t.JobKey.Name.StartsWith("NotificationJob:", StringComparison.Ordinal));
        Assert.IsAssignableFrom<ICronTrigger>(notification);

        var veto = app.Services.GetRequiredService<RecurringTriggerVeto>();
        Assert.Same(veto, scheduler.ListenerManager.GetTriggerListener(RecurringTriggerVeto.ListenerName));

        var runs = app.Services.GetRequiredService<ProbeRuns>();
        var (jobKey, triggerKey) = await ScheduleProbeEverySecondAsync(app, scheduler);

        try
        {
            // ⚠ Чекаємо САМЕ спрацювання (вето записує його синхронно, ДО виконання задачі), а не
            // «кілька секунд тиші»: інакше повільний раннер дав би зелене без жодного спрацювання.
            await WaitUntilAsync(() => veto.Vetoed.Contains(triggerKey), () => "cron-тригер зонда так і не спрацював");

            Assert.Equal(0, runs.Count);
        }
        finally
        {
            await scheduler.DeleteJob(jobKey);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Явний_запуск_задачі_за_розкладом_і_разова_постановка_виконуються()
    {
        using var app = NewApp();
        var scheduler = await StartedSchedulerAsync(app);
        var runs = app.Services.GetRequiredService<ProbeRuns>();

        var (jobKey, _) = await ScheduleProbeEverySecondAsync(app, scheduler);

        try
        {
            // Явний запуск задачі, що стоїть за розкладом (IScheduler.TriggerJob — простий тригер).
            await scheduler.TriggerJob(jobKey);
            await WaitUntilAsync(() => runs.Count >= 1, () => $"явний TriggerJob не виконав зонд. {app.ErrorsText}");

            // Разова постановка тим самим портом, що й «перевірити зараз»/перерахунок.
            var before = runs.Count;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
                    .EnqueueAsync<ProbeJob>(null, CancellationToken.None);
            }

            await WaitUntilAsync(() => runs.Count > before, () => $"разова постановка не виконала зонд. {app.ErrorsText}");
        }
        finally
        {
            await scheduler.DeleteJob(jobKey);
        }
    }

    private EcrApiFactory NewApp() => new(sql)
    {
        ExtraServices = services =>
        {
            services.AddSingleton<ProbeRuns>();
            services.AddScoped<ProbeJob>();
        },
    };

    /// <summary>Планувальник застосунку після того, як <c>RecurringScheduleService</c> поставив розклади.</summary>
    private static async Task<IScheduler> StartedSchedulerAsync(EcrApiFactory app)
    {
        // CreateClient піднімає хост; розклади ставляться після ApplicationStarted, асинхронно.
        using var client = app.CreateClient();

        var scheduler = await app.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

        await WaitUntilAsync(
            async () => (await RecurringTriggersAsync(scheduler))
                .Any(t => t.JobKey.Name.StartsWith("NotificationJob:", StringComparison.Ordinal)),
            () => $"RecurringScheduleService не поставив розклади. {app.ErrorsText}");

        return scheduler;
    }

    /// <summary>Тригери задач, поставлених як розклад (<c>QuartzJobScheduler.ScheduleAsync</c>).</summary>
    private static async Task<IReadOnlyCollection<ITrigger>> RecurringTriggersAsync(IScheduler scheduler)
    {
        var result = new List<ITrigger>();

        foreach (var key in await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()))
        {
            if (await scheduler.GetTrigger(key) is not { } trigger)
            {
                continue;
            }

            if (await scheduler.GetJobDetail(trigger.JobKey) is { } detail
                && QuartzJobScheduler.IsRecurring(detail))
            {
                result.Add(trigger);
            }
        }

        return result;
    }

    /// <summary>Ставить зонд розкладом «щосекунди» через порт застосунку; ключі задачі й тригера.</summary>
    private static async Task<(JobKey Job, TriggerKey Trigger)> ScheduleProbeEverySecondAsync(
        EcrApiFactory app, IScheduler scheduler)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>()
                .ScheduleAsync<ProbeJob>("* * * * * ?", null, CancellationToken.None);
        }

        var jobKey = Assert.Single(
            await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()),
            k => k.Name.StartsWith(nameof(ProbeJob) + ":", StringComparison.Ordinal));
        var trigger = Assert.Single(await scheduler.GetTriggersOfJob(jobKey));
        Assert.IsAssignableFrom<ICronTrigger>(trigger);

        return (jobKey, trigger.Key);
    }

    private static Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
        => WaitUntilAsync(() => Task.FromResult(condition()), failure);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"За {Patience.TotalSeconds:0} с: {failure()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
