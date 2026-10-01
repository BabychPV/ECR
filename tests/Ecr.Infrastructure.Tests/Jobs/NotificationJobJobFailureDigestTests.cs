// tests/Ecr.Infrastructure.Tests/Jobs/NotificationJobJobFailureDigestTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>ФВ-12.4/12.5</c> (<c>Q-149</c>, REQ-CLOSURE №36): кінцевий провал перерахунку, перерахунку
/// формул, імпорту, експорту й знімка звіту потрапляє у зведення <see cref="NotificationJob"/> і в
/// чергу сповіщень; успіх, скасування, чужі задачі й провали до початку вікна — ні.
/// </summary>
/// <remarks>
/// ⚠ База спільна на колекцію, а <c>NotificationJob.SinceAsync</c> бере «відколи» з НАЙПІЗНІШОГО
/// за <c>StartedAt</c> запису <c>notification</c>. Тому тест спершу прибирає такі записи, а наприкінці
/// — свій, і свої рядки <c>itg.JobProgress</c>: жодного сліду, що зсунув би вікно сусідам.
/// Мутації: прибрати <c>.Concat(jobFailures)</c> — червоний; викинути один код з
/// <see cref="NotificationJob.AlertedJobKinds"/> — червоний на ньому; прибрати фільтр
/// <c>State == "Failed"</c> — червоний на «Succeeded/Cancelled».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-12.4")]
[Trait("Requirement", "ФВ-12.5")]
[Trait("Finding", "Q-149")]
public sealed class NotificationJobJobFailureDigestTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2029, 3, 4, 3, 0, 0, DateTimeKind.Utc);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    public async Task Провал_п_яти_задач_у_зведенні_а_успіх_скасування_чужа_задача_і_старе_ні()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var alerted = NotificationJob.AlertedJobKinds.ToDictionary(k => k.Value, k => k.Key);
        string Id(string kind) => $"{kind}-{tag}";

        // Рівно ті п'ять видів, що вимагає REQ-CLOSURE №36.
        Assert.Equal(
            ["excel-export", "excel-import", "formula-recalculation", "recalculation", "report-snapshot"],
            [.. alerted.Keys.Order(StringComparer.Ordinal)]);

        await using (var setup = CreateContext())
        {
            await CleanAsync(setup, tag);

            foreach (var (kind, code) in alerted)
            {
                var failed = new JobProgress(Id(kind), code, Now.AddMinutes(-10));
                failed.Finish("Failed", $"Збій {kind}.", Now.AddMinutes(-5));
                setup.JobProgresses.Add(failed);
            }

            var ok = new JobProgress($"ok-{tag}", alerted["recalculation"], Now.AddMinutes(-10));
            ok.Finish("Succeeded", null, Now.AddMinutes(-5));
            var cancelled = new JobProgress($"cancelled-{tag}", alerted["excel-export"], Now.AddMinutes(-10));
            cancelled.Finish("Cancelled", null, Now.AddMinutes(-5));
            var foreign = new JobProgress($"foreign-{tag}", typeof(IConsistencyCheckJob).FullName!, Now.AddMinutes(-10));
            foreign.Finish("Failed", "Чужа задача.", Now.AddMinutes(-5));
            var ancient = new JobProgress($"ancient-{tag}", alerted["recalculation"], Now.AddDays(-4));
            ancient.Finish("Failed", "Давній збій.", Now.AddDays(-3));

            setup.JobProgresses.AddRange(ok, cancelled, foreign, ancient);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            var clock = new TestClock(Now);
            await using var db = CreateContext();
            var sender = Substitute.For<INotificationSender>();
            sender.IsConfigured.Returns(false);
            var channels = Substitute.For<INotificationDispatchStore>();
            channels.GetPlanAsync(Arg.Any<CancellationToken>()).Returns(new NotificationDispatchPlan("none", [], []));
            var job = new NotificationJob(
                db, clock, new OutboxDispatcher(db, clock, sender), new NotificationDispatcher(channels, [], clock),
                Substitute.For<IUiStringCatalog>());

            await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

            var run = await db.MaintenanceRuns
                .Where(r => r.JobCode == NotificationJob.Code && r.DetailsJson != null && r.DetailsJson.Contains(tag))
                .SingleAsync();
            Assert.Equal("Degraded", run.Status);

            var items = JsonDocument.Parse(run.DetailsJson!).RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(i => (Kind: i.GetProperty("kind").GetString()!, Subject: i.GetProperty("subject").GetString()!))
                .Where(i => i.Subject.EndsWith(tag, StringComparison.Ordinal))
                .OrderBy(i => i.Subject, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(alerted.Keys.OrderBy(k => Id(k), StringComparer.Ordinal).Select(k => (k, Id(k))), items);

            // Лист про збої іде тим самим шляхом, що й решта, а подія — «збій задачі», не «збір».
            var outbox = await db.NotificationOutbox
                .Where(n => n.EventCode == "maintenance.failures" && n.Body.Contains(tag))
                .SingleAsync();
            Assert.Contains($"[recalculation] {Id("recalculation")}", outbox.Body, StringComparison.Ordinal);
            Assert.DoesNotContain($"ok-{tag}", outbox.Body, StringComparison.Ordinal);
            Assert.DoesNotContain($"cancelled-{tag}", outbox.Body, StringComparison.Ordinal);
            Assert.DoesNotContain($"foreign-{tag}", outbox.Body, StringComparison.Ordinal);
            Assert.DoesNotContain($"ancient-{tag}", outbox.Body, StringComparison.Ordinal);
            Assert.All(
                alerted.Keys,
                kind => Assert.Equal(Ecr.Domain.Entities.Notifications.NotificationEventKind.JobFailed, NotificationJob.EventKindOf(kind)));
        }
        finally
        {
            await using var cleanup = CreateContext();
            await CleanAsync(cleanup, tag);
            await cleanup.MaintenanceRuns.Where(r => r.JobCode == NotificationJob.Code && r.StartedAt == Now).ExecuteDeleteAsync();
            await cleanup.NotificationOutbox.Where(n => n.Body.Contains(tag)).ExecuteDeleteAsync();
        }
    }

    private static async Task CleanAsync(EcrDbContext db, string tag)
    {
        // Без попередніх прогонів зведення вікно = «доба до Now» (FirstRunLookback).
        await db.MaintenanceRuns.Where(r => r.JobCode == NotificationJob.Code).ExecuteDeleteAsync();
        await db.JobProgresses.Where(p => p.JobId.EndsWith(tag)).ExecuteDeleteAsync();
    }
}
