// tests/Ecr.Application.Tests/Workflow/SheetWorkflowNotifierTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// ✎ D-451: листи «Аркуш подано» / «Аркуш повернуто» — кому, коли і що буває, коли пошта падає.
/// </summary>
/// <remarks>
/// ⚠ Рішення про доступ — підставні (<see cref="IAccessDecisionService"/>): предмет — що ПОВІДОМЛЮВАЧ питає саме
/// їх і ні в чому їм не перечить, а не самі правила доступу (їх стережуть тести <c>EditRules</c>/<c>AccessDecisionService</c>).
/// </remarks>
public sealed class SheetWorkflowNotifierTests
{
    private const long DocumentId = 501;
    private const int SheetId = AccessBuilder.SheetId;
    private const int ProjectId = AccessBuilder.ProjectId;
    private const int StepRoleId = 77;
    private const int OtherRoleId = 78;
    private const int Submitter = 9;
    private const int Owner = 30;
    private static readonly PeriodKey Period = new(202601);
    private static readonly DateTime At = new(2026, 2, 3, 10, 15, 0, DateTimeKind.Utc);

    private readonly INotificationDispatchStore _rules = Substitute.For<INotificationDispatchStore>();
    private readonly ISheetWorkflowMailStore _store = Substitute.For<ISheetWorkflowMailStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IUiStringCatalog _catalog = Substitute.For<IUiStringCatalog>();
    private readonly INotificationOutbox _outbox = Substitute.For<INotificationOutbox>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    /// <summary>Хто що отримав: адреса → (тема, тіло).</summary>
    private readonly List<(string EventCode, string Subject, string Body, string Recipients)> _queued = [];

    public SheetWorkflowNotifierTests()
    {
        Enable(NotificationEventKind.SheetSubmitted, NotificationEventKind.SheetReturned);

        _store.GetFactsAsync(DocumentId, SheetId, Period, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SheetMailFacts(
                ProjectId, "DOC-1", new LocalizedText(new Dictionary<string, string> { ["en"] = "Boiler house" }),
                "S1", new LocalizedText(new Dictionary<string, string> { ["en"] = "Emissions", ["ru"] = "Выбросы" }),
                "202601 (2026-01-01 – 2026-01-31)", Owner, "Olena"));
        _store.SheetLink(DocumentId, Period, "S1").Returns("https://ecr.example/documents/501?periodKey=202601&sheet=S1");
        _store.MailboxesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<SheetMailbox>)
                [.. call.Arg<IReadOnlyCollection<int>>().Order().Select(id => new SheetMailbox(id, $"u{id}@x.kz", "en"))]);
        _store.ParticipantsAsync(
                Arg.Any<long>(), Arg.Any<string>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<GrantLevel>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<int>>([]);

        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new AccessBuilder { UserId = call.Arg<int>() }.Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanApproveAsync(Arg.Any<AccessProfile>(), DocumentId, SheetId, Period, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CurrentApprovalStepAsync(DocumentId, SheetId, Period, Arg.Any<CancellationToken>())
            .Returns(new ApprovalStepView(StepId: 5, Ordinal: 2, RoleId: StepRoleId, NextStepId: 6, TotalSteps: 3));
        _documents.HasSheetAsync(DocumentId, SheetId, Arg.Any<CancellationToken>()).Returns(true);

        _catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new UiStringCatalog(call.Arg<string>(), 1, Strings(call.Arg<string>())));

        _outbox.EnqueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call => _queued.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string?>(3)!)));
    }

    // ───────────────────────────── «подано» ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Подання_шле_погоджувачам_поточного_кроку_без_подавача_і_без_тих_кому_рішення_відмовляє()
    {
        // Носії ролі кроку: подавач (9), двоє погоджувачів (20, 21) і 22, кому рішення відмовляє (роль кроку звужена
        // іншим аркушем — D-214: передфільтр її не бачить, а `CanApproveAsync` — бачить).
        _store.RoleHoldersAsync(StepRoleId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([Submitter, 20, 21, 22]);
        // D-444: учасник із наданням Approve (23) — кандидат; роль кроку перевіряє рішення.
        _store.ParticipantsAsync(DocumentId, "S1", Period, Arg.Is<IReadOnlyCollection<GrantLevel>>(l => l.SequenceEqual(new[] { GrantLevel.Approve })), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<int>>([23, 24]);
        DenyApprove(22, 24);

        await Notifier().SubmittedAsync(DocumentId, SheetId, Period, Submitter, At, CancellationToken.None);

        var item = Assert.Single(_queued);
        Assert.Equal(SheetWorkflowNotifier.SubmittedEventCode, item.EventCode);

        // ⛔ Мутація: прибрати `candidates.Remove(submitterId)` — подавач (9) отримує лист про власне подання, падає.
        // ⛔ Мутація: прибрати перевірку `CanApproveAsync` — 22 і 24 у переліку, падає.
        Assert.Equal("u20@x.kz,u21@x.kz,u23@x.kz", item.Recipients);

        // ⚠ Лише поточний крок: роль ІНШОГО кроку не питалась, як і «усі носії Approve» (це — для аркуша без маршруту).
        await _store.DidNotReceive().RoleHoldersAsync(OtherRoleId, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ApproverRoleHoldersAsync(Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Подання_без_маршруту_бере_носіїв_ролей_Approve_і_все_одно_питає_рішення()
    {
        _access.CurrentApprovalStepAsync(DocumentId, SheetId, Period, Arg.Any<CancellationToken>())
            .Returns((ApprovalStepView?)null);
        _store.ApproverRoleHoldersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([20, 40]);
        DenyApprove(40);

        await Notifier().SubmittedAsync(DocumentId, SheetId, Period, Submitter, At, CancellationToken.None);

        Assert.Equal("u20@x.kz", Assert.Single(_queued).Recipients);
        await _store.DidNotReceive().RoleHoldersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Текст_листа_мовою_адресата_з_документом_аркушем_періодом_автором_часом_і_посиланням()
    {
        _store.RoleHoldersAsync(StepRoleId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([20, 21]);
        _store.MailboxesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SheetMailbox>>([new SheetMailbox(20, "a@x.kz", "en"), new SheetMailbox(21, "b@x.kz", "ru")]);

        await Notifier().SubmittedAsync(DocumentId, SheetId, Period, Submitter, At, CancellationToken.None);

        Assert.Equal(2, _queued.Count);
        var en = Assert.Single(_queued, q => q.Recipients == "a@x.kz");
        var ru = Assert.Single(_queued, q => q.Recipients == "b@x.kz");

        Assert.Equal("en: DOC-1 — Boiler house | Emissions | 202601 (2026-01-01 – 2026-01-31)", en.Subject);
        Assert.Equal(
            "en: Olena 2026-02-03 10:15 UTC https://ecr.example/documents/501?periodKey=202601&sheet=S1",
            en.Body);

        // ⚠ Назва аркуша — мовою адресата; назви документа російською немає — запасна англійська.
        Assert.Equal("ru: DOC-1 — Boiler house | Выбросы | 202601 (2026-01-01 – 2026-01-31)", ru.Subject);
    }

    // ───────────────────────────── «повернуто» ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Повернення_шле_власнику_і_учасникам_Write_Submit_без_того_хто_повернув()
    {
        const int returner = 40;
        _store.ParticipantsAsync(
                DocumentId, "S1", Period,
                Arg.Is<IReadOnlyCollection<GrantLevel>>(l => l.Order().SequenceEqual(new[] { GrantLevel.Write, GrantLevel.Submit })),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<int>>([31, 32, returner]);

        await Notifier().ReturnedAsync(DocumentId, SheetId, Period, returner, At, "Fix row 3", CancellationToken.None);

        var item = Assert.Single(_queued);
        Assert.Equal(SheetWorkflowNotifier.ReturnedEventCode, item.EventCode);

        // ⛔ Мутація: прибрати `candidates.Remove(returnerId)` — 40 у переліку, падає.
        Assert.Equal("u30@x.kz,u31@x.kz,u32@x.kz", item.Recipients);
        Assert.Contains("Fix row 3", item.Body, StringComparison.Ordinal);

        // ⚠ Повернення не питає «чи може погодити» — адресати не погоджувачі.
        await _access.DidNotReceive().CanApproveAsync(
            Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Власник_що_сам_повернув_листа_не_отримує()
    {
        await Notifier().ReturnedAsync(DocumentId, SheetId, Period, Owner, At, "x", CancellationToken.None);

        Assert.Empty(_queued);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Адресат_якому_документ_невидимий_листа_не_отримує()
    {
        _store.ParticipantsAsync(
                Arg.Any<long>(), Arg.Any<string>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<GrantLevel>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<int>>([31]);
        _access.CanReadDocumentAsync(Arg.Is<AccessProfile>(p => p.UserId == 31), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        await Notifier().ReturnedAsync(DocumentId, SheetId, Period, 40, At, "x", CancellationToken.None);

        // ⛔ Мутація (безпека): прибрати `SeesSheetAsync` у «повернуто» — лист із назвою документа й аркуша йде
        // людині, якій документ невидимий (прострочене надання з роллю без гранта тощо), падає.
        Assert.Equal("u30@x.kz", Assert.Single(_queued).Recipients);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Адресат_якому_аркуш_схований_листа_не_отримує()
    {
        // 31 бачить документ, але аркуш йому заборонено (заборона нижче проєкту → межі читання).
        _store.ParticipantsAsync(
                Arg.Any<long>(), Arg.Any<string>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<GrantLevel>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<int>>([31]);
        _access.BuildProfileAsync(31, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 31 }.Deny(ResourceKind.Sheet, SheetId).Build());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), ProjectId, Snapshot()));

        await Notifier().ReturnedAsync(DocumentId, SheetId, Period, 40, At, "x", CancellationToken.None);

        // ⛔ Мутація (безпека): прибрати `RequireSheetVisibleAsync` із `SeesSheetAsync` — 31 отримує лист про аркуш,
        // якого не бачить, падає.
        Assert.Equal("u30@x.kz", Assert.Single(_queued).Recipients);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Погоджувач_якому_аркуш_схований_листа_про_подання_не_отримує()
    {
        _store.RoleHoldersAsync(StepRoleId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([20, 21]);
        _access.BuildProfileAsync(21, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 21 }.Deny(ResourceKind.Sheet, SheetId).Build());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), ProjectId, Snapshot()));

        await Notifier().SubmittedAsync(DocumentId, SheetId, Period, Submitter, At, CancellationToken.None);

        // ⛔ Мутація (безпека): прибрати `SeesSheetAsync` із «подано» — 21 (рішення підставне «дозволено») у переліку.
        Assert.Equal("u20@x.kz", Assert.Single(_queued).Recipients);
    }

    // ───────────────────────────── вимкнено ─────────────────────────────

    public static TheoryData<string> DisabledConfigurations => ["none", "ruleOff", "channelOff", "teamsOnly", "warningOnly", "otherEvent"];

    [Theory]
    [MemberData(nameof(DisabledConfigurations))]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Без_увімкненого_правила_на_канал_пошти_листа_немає(string configuration)
    {
        var smtp = Channel(1, NotificationChannelKind.Smtp, enabled: configuration != "channelOff");
        var teams = Channel(2, NotificationChannelKind.TeamsWebhook, enabled: true);
        NotificationRule[] rules = configuration switch
        {
            "none" => [],
            "ruleOff" => [new NotificationRule(NotificationEventKind.SheetSubmitted, 1, NotificationSeverity.Info, isEnabled: false)],
            "channelOff" => [new NotificationRule(NotificationEventKind.SheetSubmitted, 1, NotificationSeverity.Info)],
            "teamsOnly" => [new NotificationRule(NotificationEventKind.SheetSubmitted, 2, NotificationSeverity.Info)],
            "warningOnly" => [new NotificationRule(NotificationEventKind.SheetSubmitted, 1, NotificationSeverity.Warning)],
            _ => [new NotificationRule(NotificationEventKind.SheetReturned, 1, NotificationSeverity.Info)],
        };
        _rules.GetPlanAsync(Arg.Any<CancellationToken>())
            .Returns(new NotificationDispatchPlan("r", [smtp, teams], rules));
        _store.RoleHoldersAsync(StepRoleId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([20]);

        await Notifier().SubmittedAsync(DocumentId, SheetId, Period, Submitter, At, CancellationToken.None);

        // ⛔ Мутація: `IsEnabledAsync` → `true` — лист іде за будь-якої конфігурації, падає кожен випадок.
        Assert.Empty(_queued);

        // ⚠ Вимкнене — ще й дешеве: ні фактів, ні кандидатів, ні профілів.
        await _store.DidNotReceive().GetFactsAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _access.DidNotReceive().BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ───────────────────────────── збої ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public async Task Збій_черги_чи_сховища_не_виходить_назовні()
    {
        _store.RoleHoldersAsync(StepRoleId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<int>>([20]);
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        // ⛔ Мутація: прибрати `try/catch` у `SafelyAsync` — виняток летить у обробник подання, падає.
        await Notifier().SubmittedAsync(DocumentId, SheetId, Period, Submitter, At, CancellationToken.None);

        _rules.GetPlanAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("plan"));
        await Notifier().ReturnedAsync(DocumentId, SheetId, Period, 40, At, "x", CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public void Адреси_ріжуться_на_порції_що_вміщаються_в_стовпець_черги()
    {
        var emails = Enumerable.Range(1, 300).Select(i => $"user{i:000}@example.kz").ToList();

        var chunks = SheetWorkflowNotifier.Chunks(emails).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 4000, $"{c.Length}"));
        Assert.Equal(emails, chunks.SelectMany(c => c.Split(',')));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-451")]
    public void Відкликання_автором_листа_не_породжує()
    {
        // ⛔ «Повернуто» — лише повернення погоджувачем (відхилення, «повернути для правок»). Відкликання автором
        // (`RecallSheetHandler`, `ApprovalAction.Recall`) повідомлювача не має взагалі — і не мусить отримати.
        // ⛔ Мутація: додати `SheetWorkflowNotifier` у конструктор `RecallSheetHandler` — падає.
        var parameters = typeof(RecallSheetHandler).GetConstructors().SelectMany(c => c.GetParameters());
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(SheetWorkflowNotifier));

        // Контроль: ті, що мусять слати, повідомлювач мають.
        foreach (var handler in new[] { typeof(SubmitSheetHandler), typeof(ApproveSheetHandler), typeof(ReopenDocumentHandler) })
        {
            Assert.Contains(
                handler.GetConstructors().SelectMany(c => c.GetParameters()),
                p => p.ParameterType == typeof(SheetWorkflowNotifier));
        }
    }

    // ────────────────────────────── збірка ────────────────────────────

    private SheetWorkflowNotifier Notifier()
        => new(_rules, _store, _access, _documents, _catalog, _outbox, _uow);

    private void Enable(params NotificationEventKind[] kinds)
        => _rules.GetPlanAsync(Arg.Any<CancellationToken>())
            .Returns(new NotificationDispatchPlan(
                "r",
                [Channel(1, NotificationChannelKind.Smtp, enabled: true)],
                [.. kinds.Select(k => new NotificationRule(k, 1, NotificationSeverity.Info))]));

    private void DenyApprove(params int[] userIds)
        => _access.CanApproveAsync(
                Arg.Is<AccessProfile>(p => userIds.Contains(p.UserId)), DocumentId, SheetId, Period,
                Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant, "зараз черга не ваша"));

    private static NotificationChannel Channel(int id, NotificationChannelKind kind, bool enabled)
    {
        var channel = new NotificationChannel(kind, $"c{id}", "{}", At, byUserId: null);
        channel.Update(channel.Name, channel.SettingsJson, enabled, At, byUserId: null);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(channel, id);
        return channel;
    }

    private static Dictionary<string, string> Strings(string language) => new(StringComparer.Ordinal)
    {
        [SheetWorkflowNotifier.SubmittedSubjectKey] = language + ": {document} | {sheet} | {period}",
        [SheetWorkflowNotifier.SubmittedBodyKey] = language + ": {by} {at} {link}",
        [SheetWorkflowNotifier.ReturnedSubjectKey] = language + ": {document} | {sheet}",
        [SheetWorkflowNotifier.ReturnedBodyKey] = language + ": {by} {reason} {link}",
    };

    private static TemplateVersionSnapshot Snapshot()
    {
        var sheet = new SheetDef(
            1, EcrCode.Create("S1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Emissions" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, SheetId);

        return new TemplateVersionSnapshot(
            1, PresentationRevision: 1, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }
}
