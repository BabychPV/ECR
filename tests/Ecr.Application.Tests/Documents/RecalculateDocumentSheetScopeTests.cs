using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// RC14 (приймальна №10, P2-3): глухий кут «один аркуш затверджено, на іншому — чернетка». Перерахунок
/// ОБЛАСТІ (<c>sheetDefId</c>) чернеткового аркуша не відхиляється через ПОДАНИЙ СУСІДНІЙ аркуш; поданий
/// аркуш області, закритий період і схований аркуш — відхиляються, як і раніше.
/// </summary>
/// <remarks>
/// Мутація (локально): у <c>RecalculateDocumentHandler</c> прибрати фільтр за <c>sheetDefId</c> перед
/// <c>RecalculationWritePolicy.Check</c> — червоніє перший тест.
/// </remarks>
public sealed class RecalculateDocumentSheetScopeTests
{
    private const long Document = 900;
    private const int Project = 1;
    private const int Period = 202610;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_чернеткового_аркуша_не_відхиляється_через_поданий_сусідній()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null);

        var jobId = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 1, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_самого_затвердженого_аркуша_відхиляється_старим_входом()
    {
        // ✎ UAT-recalc-ux: затверджений аркуш області — «рахувати нічого»; старий вхід (без переліку) лишає 4221.
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, approved: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 3, CancellationToken.None));

        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_усього_документа_з_одним_поданим_аркушем_ставиться_в_чергу_а_не_відхиляється()
    {
        // RC15 (P2-A): подані/затверджені аркуші задача ПРОПУСКАЄ, а не відмовляє цілому запиту; відмова
        // «усі аркуші подані» (немає що рахувати) — у задачі, яка знає склад аркушів періоду.
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null);

        var jobId = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Поданий_але_не_затверджений_аркуш_області_перераховується()
    {
        // ✎ UAT-recalc-ux (слово людини 07.11: «тільки погоджені не мають перераховуватися»).
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null);

        var outcome = await fixture.Handler.RequestAsync(Document, new PeriodKey(Period), sheetDefId: 3, CancellationToken.None);

        Assert.Equal("job-1", outcome.JobId);
        Assert.Empty(outcome.Skipped);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Затверджено_все_відповідь_без_задачі_з_переліком_аркушів_складу()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, alsoSubmittedSheet: 1, approved: true);
        var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "S" });
        IReadOnlyList<SkippedSheet> rows =
        [
            new(Document, "D", null, 1, "S1", name, Period, "Approved"),
            new(Document, "D", null, 3, "S3", name, Period, "Approved"),
            new(Document, "D", null, 7, "S7", name, Period, "Approved"),   // поза складом документа — не називається
        ];
        fixture.Workflow.ListApprovedSheetsAsync(null, Document, Period, RecalculationSkips.MaxListed + 1, Arg.Any<CancellationToken>())
               .Returns(rows);

        var outcome = await fixture.Handler.RequestAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None);

        Assert.True(outcome.NothingToRecalculate);
        Assert.Equal([1, 3], outcome.Skipped.Select(s => s.SheetDefId));
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F13-p218")]
    public async Task Затверджені_оригінали_але_копія_чернетка_документ_і_область_ставляться_в_чергу()
    {
        // F13 p218 (правило людини 07.11): затверджено оригінали обох аркушів складу, але копія аркуша 3 — чернетка.
        // Рахувати є що (копію): ні документ, ні область аркуша 3 не відмовляють 4221 і не відповідають «нічого».
        // Мутація: повернути `IsSubmitted` до «будь-який затверджений рядок аркуша» — обидва виклики дають 4221.
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, alsoSubmittedSheet: 1, approved: true, copyOfSheet3: 77);

        var whole = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None);
        var scoped = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 3, CancellationToken.None);

        Assert.Equal("job-1", whole);
        Assert.Equal("job-1", scoped);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F13-p218")]
    public async Task Затверджені_оригінал_і_копія_область_відхиляється_як_раніше()
    {
        // Легітимна відмова лишається: затверджено все в області (оригінал і копія) — рахувати нічого.
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, approved: true, copyOfSheet3: 77, copyApproved: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 3, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Перерахунок_усього_документа_у_закритому_періоді_відхиляється_як_раніше()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, periodState: PeriodState.Closed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Область_у_закритому_періоді_відхиляється_навіть_без_поданих_аркушів()
    {
        var fixture = Arrange(submittedSheet: null, hiddenSheet: null, periodState: PeriodState.Closed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 1, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Схований_аркуш_як_область_дає_404_а_не_стан_і_нічого_не_ставить_у_чергу()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: 1);

        await Assert.ThrowsAsync<NotFoundException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: 1, CancellationToken.None));

        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default);
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_усього_документа_де_затверджені_усі_аркуші_відхиляється_швидко()
    {
        var fixture = Arrange(submittedSheet: 3, hiddenSheet: null, alsoSubmittedSheet: 1, approved: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Recalculate_whole_document_with_denied_sheet_is_refused_without_state_oracle()
    {
        // N1-06: перерахунок усього документа пише в таблиці схованого аркуша, а 202/4221 розкривали б його
        // стан. Однакова 403 — і коли схований аркуш у чернетці (раніше 202), і коли подані ВСІ аркуші
        // (раніше 4221 sheetsSubmitted); нічого не ставиться в чергу.
        var draft = Arrange(submittedSheet: null, hiddenSheet: 1);
        var submitted = Arrange(submittedSheet: 1, hiddenSheet: 1, alsoSubmittedSheet: 3);

        var inDraft = await Assert.ThrowsAsync<AccessDeniedException>(
            () => draft.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));
        var inSubmitted = await Assert.ThrowsAsync<AccessDeniedException>(
            () => submitted.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", inDraft.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0403.recalcNeedsFullRead", inDraft.Details?["messageKey"]);
        Assert.Equal(inDraft.ErrorCode, inSubmitted.ErrorCode);
        Assert.Equal(inDraft.Details?["messageKey"], inSubmitted.Details?["messageKey"]);
        Assert.Equal(inDraft.Message, inSubmitted.Message);

        foreach (var fixture in new[] { draft, submitted })
        {
            await fixture.Jobs.DidNotReceiveWithAnyArgs()
                .EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default);
            await fixture.Jobs.DidNotReceiveWithAnyArgs()
                .EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Перерахунок_усього_документа_без_схованих_аркушів_не_зачіпається_перевіркою_читання()
    {
        // Контроль: читач без заборон на аркуші склад документа перераховує як і раніше.
        var fixture = Arrange(submittedSheet: null, hiddenSheet: null);

        var jobId = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "AN-91-N2")]
    public async Task Різні_області_одного_документа_й_періоду_мають_різні_цілі_черги()
    {
        // AN-91 (N2): черга поглинає постановку наявною Queued на ТУ САМУ ціль і лишає її payload. Ціль без
        // аркуша зливала б запит аркуша B (чи автоперерахунок усього документа) у чергову задачу аркуша A —
        // B лишався б застарілим. Мутація: у обробнику `ScopedTargetOf(documentId, periodKey, sheetDefId)` →
        // `TargetOf(documentId, periodKey)` — тест червоніє (усі три цілі збігаються).
        var fixture = Arrange(submittedSheet: null, hiddenSheet: null);
        var period = new PeriodKey(Period);

        await fixture.Handler.HandleAsync(Document, period, sheetDefId: 1, CancellationToken.None);
        await fixture.Handler.HandleAsync(Document, period, sheetDefId: 3, CancellationToken.None);
        await fixture.Handler.HandleAsync(Document, period, sheetDefId: null, CancellationToken.None);

        var targets = fixture.Jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
            .Select(c => (string)c.GetArguments()[0]!)
            .ToList();

        Assert.Equal(3, targets.Count);
        Assert.Equal(3, targets.Distinct(StringComparer.Ordinal).Count());
        // Увесь документ — та сама ціль, що в автоперерахунку, правки шапки й нічного розкладу.
        Assert.Equal(RecalculateDocumentHandler.TargetOf(Document, period), targets[2]);
        Assert.Equal(RecalculateDocumentHandler.ScopedTargetOf(Document, period, 1), targets[0]);
        Assert.Equal(RecalculateDocumentHandler.ScopedTargetOf(Document, period, 3), targets[1]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "AN-91-N2")]
    public void Ціль_без_області_збігається_з_двопараметровою_а_з_областю_не_є_її_префіксом_іншої_області()
    {
        var period = new PeriodKey(Period);

        Assert.Equal(
            RecalculateDocumentHandler.TargetOf(Document, period),
            RecalculateDocumentHandler.ScopedTargetOf(Document, period, null));
        // Аркуш 1 і аркуш 12 — різні цілі (Quartz зіставляє за префіксом із роздільником `~`, але
        // ключ не повинен залежати від цього).
        Assert.NotEqual(
            RecalculateDocumentHandler.ScopedTargetOf(Document, period, 1),
            RecalculateDocumentHandler.ScopedTargetOf(Document, period, 12));
    }

    /// <remarks>
    /// B81 (F13 #877): запобіжник B69 в обробнику не мав модульного тесту. Архівований (чи той, що архівується) проєкт —
    /// відмова <c>err.ECR-CALC-4221.projectArchived</c> ДО стану періоду, і нічого не ставиться в чергу, навіть коли
    /// місяць відкритий (виняток «відкрито для документа» піднімає закритий місяць до Grace). Мутація: прибрати перевірку
    /// <c>IsDocumentProjectArchivedOrArchivingAsync</c> в <c>RecalculateDocumentHandler</c> — задача ставиться, червоний.
    /// Контроль: той самий документ без архівації ставиться в чергу.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B81-4")]
    public async Task Архівований_проєкт_відхиляється_до_стану_періоду_і_нічого_не_ставить_у_чергу(int? sheetDefId)
    {
        var fixture = Arrange(submittedSheet: null, hiddenSheet: null, periodState: PeriodState.Grace, projectArchived: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId, CancellationToken.None));

        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4221.projectArchived", error.Details?["messageKey"]);
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default);
        await fixture.Jobs.DidNotReceiveWithAnyArgs()
            .EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default);

        var control = Arrange(submittedSheet: null, hiddenSheet: null, periodState: PeriodState.Grace);
        Assert.Equal("job-1", await control.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId, CancellationToken.None));
    }

    private static Fixture Arrange(
        int? submittedSheet, int? hiddenSheet, PeriodState periodState = PeriodState.Open, int? alsoSubmittedSheet = null,
        bool approved = false, long? copyOfSheet3 = null, bool copyApproved = false,
        ITemplateLayoutRegistry? layouts = null, Action<IPeriodStore>? configurePeriods = null,
        IProjectVersionTimeline? timeline = null, bool projectArchived = false)
    {
        var builder = new AccessBuilder { UserId = 9 }
            .Permission(RecalculateDocumentHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (hiddenSheet is { } hidden)
        {
            builder.Deny(ResourceKind.Sheet, hidden);
        }

        var profile = builder.Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.DocumentProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, SnapshotOfTwoSheets()));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(Document, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        documents.GetIncludedSheetIdsAsync(Document, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>([1, 3]));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Document, Period, Arg.Any<CancellationToken>()).Returns((PeriodState?)periodState);
        periods.IsDocumentProjectArchivedOrArchivingAsync(Document, Arg.Any<CancellationToken>()).Returns(projectArchived);
        configurePeriods?.Invoke(periods);

        var states = new List<ApprovalState>();
        if (submittedSheet is { } submitted)
        {
            var state = new ApprovalState(Document, submitted, Period);
            state.Submit(userId: 5, DateTime.UtcNow);
            if (approved)
            {
                state.Approve(userId: 6, DateTime.UtcNow);
            }

            states.Add(state);
        }

        if (alsoSubmittedSheet is { } also)
        {
            var state = new ApprovalState(Document, also, Period);
            state.Submit(userId: 5, DateTime.UtcNow);
            if (approved)
            {
                state.Approve(userId: 6, DateTime.UtcNow);
            }

            states.Add(state);
        }

        var copies = Substitute.For<IDocumentSheetCopyStore>();
        IReadOnlyList<SheetCopyGroupInfo> groups = copyOfSheet3 is { } copyId
            ? [new SheetCopyGroupInfo(3, null, [new SheetCopyOrdinal(copyId, 2)])]
            : [];
        copies.ListGroupsAsync(Document, Arg.Any<CancellationToken>()).Returns(groups);

        if (copyOfSheet3 is { } approvedCopy && copyApproved)
        {
            var state = new ApprovalState(Document, 3, Period, approvedCopy);
            state.Submit(userId: 5, DateTime.UtcNow);
            state.Approve(userId: 6, DateTime.UtcNow);
            states.Add(state);
        }

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(states);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");
        jobs.EnqueueExclusiveAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        return new Fixture(
            new RecalculateDocumentHandler(jobs, access, user, documents, periods, workflow, copies, layouts, timeline), jobs, workflow);
    }

    /// <summary>
    /// ✎ LAND-YEAR B24 (D-NEW-3): закритий ЯКІР (найменший ключ календаря року, тут 202610) річної розкладки у
    /// відкритому році — кнопка «Перерахувати» ставить задачу (вона пише не-місячне під агрегатом), а не 4221.
    /// </summary>
    /// <remarks>⛔ Мутація: <c>ClosedYearAnchorWritesAsync</c> → <c>false</c> — 4221 periodClosed, тест червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-474")]
    public async Task B24_закритий_якір_річної_розкладки_у_відкритому_році_ставиться_в_чергу()
    {
        var fixture = ArrangeYear(yearLayout: true, PeriodState.Closed, PeriodState.Open, PeriodState.Open);

        var jobId = await fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    /// <summary>Контроль: версія <c>PerMonthCopies</c> — закритий місяць відмовляє, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-474")]
    public async Task B24_контроль_PerMonthCopies_закритий_місяць_відмовляє()
    {
        var fixture = ArrangeYear(yearLayout: false, PeriodState.Closed, PeriodState.Open, PeriodState.Open);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    /// <summary>Контроль: річна розкладка, але закритий увесь рік — відмова, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-474")]
    public async Task B24_контроль_закритий_увесь_рік_відмовляє()
    {
        var fixture = ArrangeYear(yearLayout: true, PeriodState.Closed, PeriodState.Closed, PeriodState.Closed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    /// <summary>Контроль: закритий місяць, що НЕ якір року (є раніший 202609), — відмова, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-474")]
    public async Task B24_контроль_закритий_не_якір_відмовляє()
    {
        var fixture = ArrangeYear(yearLayout: true, PeriodState.Closed, PeriodState.Open, PeriodState.Open, earlier: true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fixture.Handler.HandleAsync(Document, new PeriodKey(Period), sheetDefId: null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    /// <summary>
    /// Календар року проєкту: <see cref="Period"/> (202610) і два наступні місяці зі станами; <paramref name="earlier"/> —
    /// ще й відкритий 202609 (тоді якір — він).
    /// </summary>
    private static Fixture ArrangeYear(
        bool yearLayout, PeriodState anchor, PeriodState november, PeriodState december, bool earlier = false)
    {
        var layouts = Substitute.For<ITemplateLayoutRegistry>();
        layouts.HasAny.Returns(true);
        layouts.IsYearVersion(7).Returns(yearLayout);

        var timeline = Substitute.For<IProjectVersionTimeline>();
        timeline.VersionOfAsync(AccessBuilder.ProjectId, Period, Arg.Any<CancellationToken>()).Returns(7);

        return Arrange(
            submittedSheet: null, hiddenSheet: null, periodState: anchor, layouts: layouts, timeline: timeline,
            configurePeriods: periods =>
            {
                var calendar = new List<PeriodStateRef>();
                if (earlier)
                {
                    calendar.Add(new PeriodStateRef(202609, PeriodState.Open));
                }

                calendar.Add(new PeriodStateRef(Period, anchor));
                calendar.Add(new PeriodStateRef(202611, november));
                calendar.Add(new PeriodStateRef(202612, december));
                periods.GetPeriodStatesAsync(AccessBuilder.ProjectId, null, Arg.Any<CancellationToken>())
                       .Returns((IReadOnlyList<PeriodStateRef>)calendar);
            });
    }

    private static TemplateVersionSnapshot SnapshotOfTwoSheets()
    {
        var template = new TemplateBuilder();
        var s1 = template.Sheet("S1");
        template.Table(s1, "T1");
        var s2 = template.Sheet("S2");
        template.Table(s2, "T2");
        return template.Build();
    }

    private sealed record Fixture(RecalculateDocumentHandler Handler, IBackgroundJobScheduler Jobs, IWorkflowStore Workflow);
}
