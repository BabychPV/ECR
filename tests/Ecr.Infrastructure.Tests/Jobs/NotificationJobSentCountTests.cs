// tests/Ecr.Infrastructure.Tests/Jobs/NotificationJobSentCountTests.cs
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Notifications;
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
/// CL-5: <c>sent</c> у підсумку <c>jobs.notificationDone</c> рахує і доставлене КАНАЛАМИ, а не
/// лише чергою процесу (<c>Smtp:*</c>). Інакше розсилка лише каналом читалася б як «збоїв N,
/// відправлено 0» — бейдж <c>SucceededWithErrors</c> у <c>/jobs</c> і жовта картка <c>jobs</c>,
/// хоча лист дійшов.
/// </summary>
/// <remarks>
/// ⚠ База спільна на колекцію; вікно зведення — з найпізнішого запису <c>notification</c>, тому
/// тест спершу прибирає такі записи (як <see cref="NotificationJobJobFailureDigestTests"/>), а
/// наприкінці — свої. Мутація (прогнано): <c>var sent = outboxSent + channelSent</c> →
/// <c>var sent = outboxSent</c> — червоний перший тест.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-12.5")]
public sealed class NotificationJobSentCountTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2029, 7, 8, 3, 0, 0, DateTimeKind.Utc);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    public async Task Доставлене_каналом_без_Smtp_рахується_у_sent_і_не_дає_попередження()
    {
        var (count, sent) = await RunAsync(withChannel: true);

        Assert.True(count > 0, $"count={count}");
        Assert.True(sent > 0, $"sent={sent}");
        Assert.Null(JobCompletionWarning.EffectiveStateOf("Succeeded", Envelope(count, sent)));
    }

    [Fact]
    public async Task Без_каналу_і_без_Smtp_відправлено_нуль_і_це_попередження()
    {
        var (count, sent) = await RunAsync(withChannel: false);

        Assert.True(count > 0, $"count={count}");
        Assert.Equal(0, sent);
        Assert.Equal(
            JobCompletionWarning.SucceededWithErrors,
            JobCompletionWarning.EffectiveStateOf("Succeeded", Envelope(count, sent)));
    }

    private static string Envelope(int count, int sent)
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            JobCompletionWarning.NotificationDoneKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [JobCompletionWarning.FailuresParam] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sent"] = sent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pending"] = "0",
            }));

    /// <summary>Один провал перерахунку у вікні, зведення без <c>Smtp:*</c>; повертає <c>failures</c>/<c>sent</c> підсумку.</summary>
    private async Task<(int Count, int Sent)> RunAsync(bool withChannel)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using (var setup = CreateContext())
        {
            await CleanAsync(setup, tag);
            var failed = new JobProgress($"recalc-{tag}", typeof(IRecalculationJob).FullName!, Now.AddMinutes(-10));
            failed.Finish("Failed", $"Збій {tag}.", Now.AddMinutes(-5));
            setup.JobProgresses.Add(failed);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            var clock = new TestClock(Now);
            var channel = new NotificationChannel(NotificationChannelKind.Smtp, $"sent-{tag}", "{}", Now, null);
            var plan = withChannel
                ? new NotificationDispatchPlan(
                    $"sent-{tag}", [channel],
                    [new NotificationRule(NotificationEventKind.JobFailed, channel.Id, NotificationSeverity.Info)])
                : new NotificationDispatchPlan("none", [], []);

            var store = Substitute.For<INotificationDispatchStore>();
            store.GetPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);
            store.WasSentSinceAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(false);

            var channelSender = Substitute.For<INotificationChannelSender>();
            channelSender.Kind.Returns(NotificationChannelKind.Smtp);
            channelSender.SendAsync(Arg.Any<NotificationChannel>(), Arg.Any<NotificationMessage>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var sender = Substitute.For<INotificationSender>();
            sender.IsConfigured.Returns(false);

            var reports = new List<string?>();
            var progress = Substitute.For<IJobProgress>();
            progress.ReportAsync(Arg.Any<int>(), Arg.Do<string?>(reports.Add), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            await using var db = CreateContext();
            var job = new NotificationJob(
                db, clock, new OutboxDispatcher(db, clock, sender),
                new NotificationDispatcher(store, [channelSender], clock),
                Substitute.For<IUiStringCatalog>());

            await job.ExecuteAsync(null, progress, CancellationToken.None);

            var done = reports
                .Select(r => JobProgressMessageCodec.TryDecode(r, out var e) ? e : null)
                .Single(e => e?.Key == JobCompletionWarning.NotificationDoneKey)!;

            return (
                int.Parse(done.Params![JobCompletionWarning.FailuresParam], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(done.Params!["sent"], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await using var cleanup = CreateContext();
            await CleanAsync(cleanup, tag);
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
