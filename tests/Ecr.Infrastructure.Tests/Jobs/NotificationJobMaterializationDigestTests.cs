// tests/Ecr.Infrastructure.Tests/Jobs/NotificationJobMaterializationDigestTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-235: провал <c>MaterializeCollectedDataJob</c> має потрапити у зведення
/// <see cref="NotificationJob"/>, а не загубитися в <c>itg.JobProgress</c>.
/// </summary>
/// <remarks>
/// ⛔ Регресія, яку тест ловить: до цього <c>NotificationJob</c> зводив ЛИШЕ
/// <c>itg.CollectionRun</c> (це збір) і <c>itg.MaintenanceRun</c> (задачі, що
/// йдуть через <c>ScheduleAsync</c>). Матеріалізація — ні те, ні те: вона
/// ставиться через <c>EnqueueAsync&lt;IMaterializeCollectedDataJob&gt;</c> на
/// кожен документ+таблицю окремо (`CollectionJob.EnqueueMaterializationAsync`),
/// і єдиний слід її провалу — рядок <c>itg.JobProgress</c> зі станом
/// <c>Failed</c>, який щогодинне зведення жодного разу не читало. Точки
/// зібрано, у комірки вони не потрапили — і жодна людина не дізналася б про
/// це з зведення, лише з ручного перегляду екрана прогресу задач.
///
/// ⚠ База тут спільна на увесь <c>[Collection("SqlServer")]</c> (`SqlServerFixture`
/// перестворюється один раз на прогін, не на тест): <c>NotificationJob.SinceAsync</c>
/// бере "відколи" з ОСТАННЬОГО за <c>StartedAt</c> запису <c>itg.MaintenanceRun</c>
/// з кодом <c>notification</c> — а такий запис лишає й ІНШИЙ тест цього самого
/// класу. Тому перевірки нижче знаходять СВІЙ запис за вмістом (<c>jobId</c> у
/// <c>DetailsJson</c>), а не «останній за часом»: порядок виконання двох тестів
/// не гарантований, і пошук «останнього» ловив би чужий запис.
/// </remarks>
[Collection("SqlServer")]
public sealed class NotificationJobMaterializationDigestTests(SqlServerFixture sql)
{
    private static readonly string MaterializeJobCode = typeof(IMaterializeCollectedDataJob).FullName!;

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-235")]
    public async Task Провалена_матеріалізація_потрапляє_у_зведення_і_чергу_сповіщень()
    {
        // ⚠ Далеко в майбутньому і ПІЗНІШЕ за час сусіднього тесту навмисно:
        // інший тест цього класу теж пише `itg.MaintenanceRun` із кодом
        // `notification` у ту саму базу, і `SinceAsync` бере "відколи" з
        // ОСТАННЬОГО такого запису. Якщо цей момент — НАЙПІЗНІШИЙ серед усіх
        // запусків класу, вікно `[since, now)` включає власний `JobProgress`
        // ЗАВЖДИ, незалежно від того, який тест виконався першим: або
        // попереднього запису ще немає (тоді since = now - доба), або він є,
        // але його `StartedAt` — раніше за це `now` (тест нижче навмисно бере
        // ще ранішу дату). Коли тут стояли близькі дати 2026 року, порядок
        // виконання міг посунути вікно так, що власний `JobProgress` випадав
        // із нього ще до того, як зведення встигало його прочитати.
        var now = new DateTime(2031, 6, 1, 3, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);
        const string jobId = "IMaterializeCollectedDataJob-q234test-fail";

        await using (var setup = CreateContext())
        {
            // ⚠ Той самий шлях, яким `QuartzJobAdapter.Execute` позначає
            // провал ПІСЛЯ вичерпання ретраїв (`FinishAsync` → `JobProgress.Finish`):
            // жодного власного запису, лише те, що реально лишає прод-код.
            var progress = new JobProgress(jobId, MaterializeJobCode, now.AddMinutes(-10));
            progress.Finish("Failed", "Мапінг цілить рядок, якого немає в таблиці.", now.AddMinutes(-5));

            setup.JobProgresses.Add(progress);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = CreateContext();
        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(false); // ⚠ Транспорт не налаштований (`P-13`): подія лишається Pending, не зникає.
        var dispatcher = new OutboxDispatcher(db, clock, sender);
        var job = new NotificationJob(db, clock, dispatcher);

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        // Пошук за ВМІСТОМ, а не «останній за часом» (див. коментар класу):
        // саме той запис, куди мав потрапити ЦЕЙ jobId.
        var run = await db.MaintenanceRuns
            .Where(r => r.JobCode == NotificationJob.Code
                        && r.DetailsJson != null && r.DetailsJson.Contains(jobId))
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(run);
        Assert.Equal("Degraded", run!.Status);
        Assert.Contains(NotificationJob.MaterializationKind, run.DetailsJson!, StringComparison.Ordinal);

        var outboxItem = await db.NotificationOutbox
            .Where(n => n.EventCode == "maintenance.failures" && n.Body.Contains(jobId))
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(outboxItem);
        Assert.Contains(NotificationJob.MaterializationKind, outboxItem!.Body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-235")]
    public async Task Успішна_матеріалізація_у_зведення_не_потрапляє()
    {
        var now = new DateTime(2030, 2, 20, 4, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);
        const string jobId = "IMaterializeCollectedDataJob-q234test-ok";

        await using (var setup = CreateContext())
        {
            var progress = new JobProgress(jobId, MaterializeJobCode, now.AddMinutes(-10));
            progress.Finish("Succeeded", null, now.AddMinutes(-5));

            setup.JobProgresses.Add(progress);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = CreateContext();
        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(false);
        var dispatcher = new OutboxDispatcher(db, clock, sender);
        var job = new NotificationJob(db, clock, dispatcher);

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⚠ Успіх не має шуміти у зведенні: тільки провали. Перевіряємо ВСІ
        // записи зведення (не «останній») — порядок виконання тестів не
        // гарантований, і саме це раніше й ламало сусідній тест.
        var runsWithThisJob = await db.MaintenanceRuns
            .Where(r => r.JobCode == NotificationJob.Code
                        && r.DetailsJson != null && r.DetailsJson.Contains(jobId))
            .ToListAsync();

        Assert.Empty(runsWithThisJob);
    }
}
