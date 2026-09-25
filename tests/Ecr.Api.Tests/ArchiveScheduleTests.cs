using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// `F-13` (UX-PASS, четвертий раунд): фізичне перенесення в архів стоїть у
/// нічному розкладі застосунку, а не лише існує в коді.
/// </summary>
/// <remarks>
/// ⛔ <c>ArchiveJob</c> не запускав НІХТО — маршруту немає, у
/// <c>RecurringScheduleService</c> його не було, — тож кнопка «Archive» лише
/// ставила статус. Що сама задача переносить і що перенесене читається, —
/// <c>ArchiveJobTests</c> (Infrastructure); тут — що її справді поставлено.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchiveScheduleTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F-13")]
    public async Task ArchiveJob_стоїть_у_нічному_розкладі()
    {
        using var app = new EcrApiFactory(sql);
        _ = app.CreateClient();

        var scheduler = await app.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

        // ⚠ Розклади ставляться з `ApplicationStarted` асинхронно — чекаємо, а
        // не читаємо один раз.
        JobKey? key = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (key is null && DateTime.UtcNow < deadline)
        {
            key = (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()))
                .FirstOrDefault(k => k.Name.StartsWith("ArchiveJob:", StringComparison.Ordinal));

            if (key is null)
            {
                await Task.Delay(200);
            }
        }

        // ⛔ Мутація: прибрати `ScheduleAsync<ArchiveJob>` з
        // `RecurringScheduleService` — ключа немає.
        Assert.NotNull(key);

        var trigger = Assert.IsAssignableFrom<ICronTrigger>(Assert.Single(await scheduler.GetTriggersOfJob(key)));
        Assert.Equal(Ecr.Api.Startup.RecurringScheduleService.NightlyCron, trigger.CronExpressionString);
    }
}
