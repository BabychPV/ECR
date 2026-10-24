// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.SheetMail.cs
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// ✎ D-451: лист «подано/повернуто» не є умовою дії — збій пошти не відкочує подання чи повернення і не
/// перетворює успіх на помилку.
/// </summary>
public sealed partial class WorkflowTransactionTests
{
    private const string MailMarker = "пошта недоступна";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-451")]
    public async Task Збій_листа_про_подання_не_зачіпає_подання()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        var handler = Submit(world, db, Access(), notifier: FailingNotifier(db, world));

        // ⛔ Мутація: прибрати `try/catch` у `SheetWorkflowNotifier.SafelyAsync` — `HandleAsync` кидає
        // `InvalidOperationException(MailMarker)` після закоміченого подання, клієнт бачить 500, падає.
        await handler.HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(true);

        // ⛔ Мутація: перенести виклик повідомлювача ВСЕРЕДИНУ транзакції без перехоплення — подання відкотилося б.
        Assert.Equal(1, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Equal(0, await OutboxAsync(world).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-451")]
    public async Task Збій_листа_про_повернення_не_зачіпає_відхилення()
    {
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        await using var db = CreateContext();
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);
        var documents = Substitute.For<IDocumentStore>();
        documents.FindProjectIdAsync(world.DocumentId, Arg.Any<CancellationToken>()).Returns(world.ProjectId);

        var handler = new ApproveSheetHandler(
            new WorkflowStore(db), Access(), new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), Approver(), new TestClock(Now), new AuditWriter(db), documents,
            snapshotRefresh: null, notifier: FailingNotifier(db, world));

        await handler.HandleAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: false, reason: "Fix row 3",
                CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Rejected, await StatusAsync(world).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-451")]
    public async Task Подання_з_увімкненим_правилом_кладе_лист_у_чергу_тим_самим_контекстом()
    {
        // Контроль до тестів вище: без збою лист справді доходить до черги (інакше «нічого не впало» було б
        // зеленим і на повідомлювачі, що не робить нічого).
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        var store = Store(world);
        store.RoleHoldersAsync(RoleId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([UserId, ApproverId]);

        await Submit(world, db, Access(), notifier: Notifier(db, store))
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(true);

        // Подавач (UserId) — без листа; погоджувач — один рядок черги.
        Assert.Equal(1, await OutboxAsync(world).ConfigureAwait(true));
    }

    // ────────────────────────────── збірка ────────────────────────────

    private static SheetWorkflowNotifier FailingNotifier(EcrDbContext db, World world)
    {
        var store = Store(world);
        store.RoleHoldersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(MailMarker));
        store.ParticipantsAsync(
                Arg.Any<long>(), Arg.Any<string>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<GrantLevel>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(MailMarker));

        return Notifier(db, store);
    }

    private static ISheetWorkflowMailStore Store(World world)
    {
        var store = Substitute.For<ISheetWorkflowMailStore>();
        store.GetFactsAsync(world.DocumentId, world.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SheetMailFacts(world.ProjectId, Key(world), null, "S", null, "202601", 1, "Olena"));
        store.ParticipantsAsync(
                Arg.Any<long>(), Arg.Any<string>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<GrantLevel>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<int>>([]);
        store.MailboxesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<SheetMailbox>)
                [.. call.Arg<IReadOnlyCollection<int>>().Select(id => new SheetMailbox(id, $"u{id}@x.kz", "en"))]);
        store.SheetLink(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<string>()).Returns("/documents/1");
        return store;
    }

    private static SheetWorkflowNotifier Notifier(EcrDbContext db, ISheetWorkflowMailStore store)
    {
        var rules = Substitute.For<INotificationDispatchStore>();
        var channel = new NotificationChannel(NotificationChannelKind.Smtp, "mail", "{}", Now, byUserId: null);
        rules.GetPlanAsync(Arg.Any<CancellationToken>())
            .Returns(new NotificationDispatchPlan(
                "r", [channel],
                [
                    new NotificationRule(NotificationEventKind.SheetSubmitted, channel.Id, NotificationSeverity.Info),
                    new NotificationRule(NotificationEventKind.SheetReturned, channel.Id, NotificationSeverity.Info),
                ]));

        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new UiStringCatalog(
                call.Arg<string>(), 1,
                new Dictionary<string, string>
                {
                    // ⚠ Тема = ключ документа світу: база спільна на збірку, і лічильник черги мусить бачити лише свій.
                    [SheetWorkflowNotifier.SubmittedSubjectKey] = "{document}",
                    [SheetWorkflowNotifier.ReturnedSubjectKey] = "{document}",
                }));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        return new SheetWorkflowNotifier(
            rules, store, Access(), documents, catalog,
            new Ecr.Infrastructure.Integration.NotificationOutboxStore(db, new TestClock(Now)), new UnitOfWork(db));
    }

    private static string Key(World world)
        => "MAIL-" + world.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Рядки черги листів аркуша саме цього світу.</summary>
    private async Task<int> OutboxAsync(World world)
    {
        await using var db = CreateContext();
        var key = Key(world);
        return await db.NotificationOutbox.AsNoTracking()
            .CountAsync(n => n.Subject == key
                             && (n.EventCode == SheetWorkflowNotifier.SubmittedEventCode
                                 || n.EventCode == SheetWorkflowNotifier.ReturnedEventCode))
            .ConfigureAwait(false);
    }
}
