// tests/Ecr.Infrastructure.Tests/Jobs/PeriodOpenedNotificationTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
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
/// Щойно відкритий період (<c>Scheduled → Open</c>) дає подію <see cref="NotificationEventKind.PeriodOpened"/>
/// з проєктом і періодом у тексті — привід нагадати «заповніть документ» (відповідь людини 01.10, п. 6).
/// </summary>
/// <remarks>
/// ⚠ <c>PeriodStateJob</c> обходить УСІ активні проєкти спільної бази, тож повідомлення
/// відбираються за кодом проєкту цього тесту, а не рахуються загалом.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodOpenedNotificationTests(SqlServerFixture sql)
{
    private static readonly DateTime FirstRun = new(2026, 1, 25, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>20 лютого січень уже відкрито (відкриття — 15 лютого місцевого).</summary>
    private static readonly DateTime StateRun = new(2026, 2, 20, 6, 0, 0, DateTimeKind.Utc);

    private static PeriodPolicy Policy()
        => new(EcrCode.Create("OPN45"), openOffsetDays: 45, graceOffsetDays: 30,
            hardCloseOffsetDays: 60, yearGraceOffsetDays: 45);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкриття_періоду_розсилає_PeriodOpened_раз_із_проєктом_і_періодом_у_тексті()
    {
        var (db, chain, code) = await ArrangeAsync();
        await using var scope = db;

        var sender = new RecordingSender();
        var job = Job(db, Dispatcher(new PlanStore(NotificationEventKind.PeriodOpened), sender), StateRun);

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        var mine = sender.Messages.Where(m => m.Subject.Contains(code, StringComparison.Ordinal)).ToList();
        var message = Assert.Single(mine);
        Assert.Contains(chain.PeriodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), message.Subject, StringComparison.Ordinal);
        Assert.Contains(code, message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{project}", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{period}", message.Body, StringComparison.Ordinal);

        // Період уже Open: наступний прогін нічого не відкриває — і не нагадує вдруге.
        db.ChangeTracker.Clear();
        await Job(db, Dispatcher(new PlanStore(NotificationEventKind.PeriodOpened), sender), StateRun.AddHours(1))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        Assert.Single(sender.Messages, m => m.Subject.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Недоступна_розсилка_не_валить_прогін_і_не_повертає_період_у_Scheduled()
    {
        var (db, chain, _) = await ArrangeAsync();
        await using var scope = db;

        var broken = Substitute.For<INotificationDispatchStore>();
        broken.GetPlanAsync(Arg.Any<CancellationToken>()).Returns<NotificationDispatchPlan>(_ => throw new InvalidOperationException("db down"));

        await Job(db, Dispatcher(broken, new RecordingSender()), StateRun)
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        db.ChangeTracker.Clear();
        var state = await db.Periods
            .Where(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value)
            .Select(p => p.State)
            .SingleAsync();
        Assert.Equal(PeriodState.Open, state);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_правила_нічого_не_шлеться_ні_про_відкриття_ні_про_пільговий_строк()
    {
        var (db, chain, code) = await ArrangeAsync();
        await using var scope = db;

        var sender = new RecordingSender();
        await Job(db, Dispatcher(new PlanStore(), sender), StateRun)
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        var graceAt = await GraceAtAsync(db, chain);
        await Job(db, Dispatcher(new PlanStore(), sender), graceAt.AddHours(1))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        Assert.DoesNotContain(sender.Messages, m => m.Subject.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пільговий_строк_періоду_розсилає_PeriodGraceStarted_раз_за_фіксованим_годинником()
    {
        var (db, chain, code) = await ArrangeAsync();
        await using var scope = db;

        var sender = new RecordingSender();
        var plan = new PlanStore(NotificationEventKind.PeriodGraceStarted);

        // Відкриття: правила на PeriodOpened немає — тиша.
        await Job(db, Dispatcher(plan, sender), StateRun)
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        Assert.DoesNotContain(sender.Messages, m => m.Subject.Contains(code, StringComparison.Ordinal));

        var graceAt = await GraceAtAsync(db, chain);

        // Раніше за межу пільгового строку — досі тиша.
        db.ChangeTracker.Clear();
        await Job(db, Dispatcher(plan, sender), graceAt.AddHours(-1))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        Assert.DoesNotContain(sender.Messages, m => m.Subject.Contains(code, StringComparison.Ordinal));

        // Після межі: один лист; повторний прогін не дублює.
        db.ChangeTracker.Clear();
        await Job(db, Dispatcher(plan, sender), graceAt.AddHours(1))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        db.ChangeTracker.Clear();
        await Job(db, Dispatcher(plan, sender), graceAt.AddHours(2))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        var message = Assert.Single(sender.Messages, m => m.Subject.Contains(code, StringComparison.Ordinal));
        Assert.StartsWith("Past deadline", message.Subject, StringComparison.Ordinal);
        Assert.Contains(code, message.Body, StringComparison.Ordinal);
    }

    private static async Task<DateTime> GraceAtAsync(EcrDbContext db, TestDocument chain)
    {
        db.ChangeTracker.Clear();
        return await db.Periods
            .Where(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value)
            .Select(p => p.ComputedGraceAt)
            .SingleAsync();
    }

    private async Task<(EcrDbContext Db, TestDocument Chain, string Code)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var db = builder.CreateContext();

        var project = await db.Projects.SingleAsync(p => p.Id == chain.ProjectId);
        project.Activate(FirstRun);

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.RecomputeBoundaries(Policy(), SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());
        await db.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(PeriodState.Scheduled, period.State);

        return (db, chain, project.Code);
    }

    private static PeriodStateJob Job(EcrDbContext db, NotificationDispatcher dispatcher, DateTime at)
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new UiStringCatalog(
            "en",
            1,
            new Dictionary<string, string>
            {
                ["notifications.periodOpened.subject"] = "Period {period} opened: {project}",
                ["notifications.periodOpened.body"] = "Fill in {project} for {period}.",
                ["notifications.periodGraceStarted.subject"] = "Past deadline {period}: {project}",
                ["notifications.periodGraceStarted.body"] = "Grace window for {project}, {period}.",
            }));

        return new PeriodStateJob(
            db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(at),
            new MaterializationScheduler(db, Substitute.For<IBackgroundJobScheduler>()),
            notifications: dispatcher, catalog: catalog);
    }

    private static NotificationDispatcher Dispatcher(INotificationDispatchStore store, RecordingSender sender)
        => new(store, [sender], new TestClock(StateRun));

    /// <summary>Правило «PeriodOpened → канал» для одного пошти-каналу в пам'яті (Id = 0 в обох).</summary>
    private sealed class PlanStore(params NotificationEventKind[] kinds) : INotificationDispatchStore
    {
        public Task<NotificationDispatchPlan> GetPlanAsync(CancellationToken ct)
            => Task.FromResult(new NotificationDispatchPlan(
                "r1",
                [new NotificationChannel(NotificationChannelKind.Smtp, "Mail", "{}", StateRun, null)],
                [.. kinds.Select(k => new NotificationRule(k, 0, NotificationSeverity.Info))]));

        public Task<bool> WasSentSinceAsync(int channelId, string eventKey, DateTime since, CancellationToken ct)
            => Task.FromResult(false);

        public Task AppendDeliveryAsync(NotificationDelivery delivery, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingSender : INotificationChannelSender
    {
        public List<NotificationMessage> Messages { get; } = [];

        public NotificationChannelKind Kind => NotificationChannelKind.Smtp;

        public Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
