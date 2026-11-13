// tests/Ecr.Application.Tests/Calculations/CalculationTriggerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Автоперерахунок після запису без людини (<see cref="CalculationTrigger"/>, HSE301 A4, V-5 → <c>D-174</c>).
/// </summary>
/// <remarks>
/// ⚠ Тут — правило «куди можна» і форма постановки. Що черга лишає ОДНУ задачу
/// на кілька викликів — доводить справжній планувальник
/// (<c>AutoRecalcAfterMaterializeTests</c>).
/// </remarks>
public sealed class CalculationTriggerTests
{
    private const long DocumentId = 4501;
    private static readonly PeriodKey Period = new(202601);
    private static readonly int[] IncludedThree = [1, 2, 3];

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IDocumentSheetCopyStore _sheetCopies = Substitute.For<IDocumentSheetCopyStore>();

    public CalculationTriggerTests()
    {
        _jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~auto");
        _workflow.GetSheetsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());
        _documents.GetIncludedSheetIdsAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _sheetCopies.ListGroupsAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());
    }

    private CalculationTrigger Trigger() => new(_jobs, _periods, _workflow, _documents, _sheetCopies);

    private void State(PeriodState? state)
        => _periods.FindPeriodStateAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>()).Returns(state);

    [Theory]
    [InlineData(PeriodState.Open)]
    [InlineData(PeriodState.Grace)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Відкритий_період_ставить_перерахунок_злиттям_зі_своєю_ціллю_без_автора(PeriodState state)
    {
        State(state);

        var jobId = await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal("IRecalculationJob~auto", jobId);

        // ⛔ ✎ B83: ціль — своя (`AutoTargetOf`), не ціль кнопки й нічних дочірніх (`TargetOf`): спільна ціль зливала б
        // постановку з чужою задачею, що чекає, і позначка поданих губилась би. Автор — `null`: системна задача.
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: `EnqueueCoalescedAsync` → `EnqueueExclusiveAsync` у
        // тригері → `Received(1)` нижче червоний, а витіснення виконуваного
        // перерахунку «гарячого» документа повертається.
        await _jobs.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.AutoTargetOf(DocumentId, Period),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        Assert.NotEqual(
            RecalculateDocumentHandler.TargetOf(DocumentId, Period), CalculationTrigger.AutoTargetOf(DocumentId, Period));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(
            default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B83")]
    public async Task Payload_без_автора_без_аркуша_з_позначкою_поданих()
    {
        State(PeriodState.Open);
        object? payload = null;
        _jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => payload = p), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~auto");

        await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        // ⚠ Форма payload — та, що читає `RecalculationJob` (`RecalculationRequest`):
        // без `SheetDefId` рахується весь документ, без `TriggeredByUserId` —
        // це системна задача; `recalculateSubmitted` — подані пишуться з журналом і позначкою (B83).
        var json = System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Equal(
            $$"""{"documentId":{{DocumentId}},"periodKey":{{Period.Value}},"triggeredByUserId":null,"sheetDefId":null,"recalculateSubmitted":true}""",
            json);
    }

    [Theory]
    [InlineData(PeriodState.Closed)]
    [InlineData(PeriodState.Scheduled)]
    [InlineData(null)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Закритий_ще_не_відкритий_чи_невідомий_період_нуль_задач(PeriodState? state)
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перевірку `RecalculationWritePolicy` у
        // тригері → `Closed` ставить задачу, яка впаде в `RecalculationJob`
        // і лишить у журналі провалений перерахунок на кожен пізній збір.
        State(state);

        var jobId = await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.Null(jobId);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(
            default!, default, default, default);
    }

    /// <summary>
    /// ⛔ B83 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): автоперерахунок після збору PI на
    /// документі з затвердженим, поданим і чернетковим аркушем ставиться — з позначкою поданих і своєю ціллю (затверджений
    /// пропускає сама задача, <c>RecalculationJob.SkippedScopesAsync</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): повернути в <c>RequestAsync</c> стару перевірку «будь-який поданий/затверджений —
    /// <c>RecalculationWritePolicy.Check(state, submitted)</c>» — <c>null</c>, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B83")]
    public async Task Збір_на_документі_з_затвердженим_поданим_і_чернеткою_ставиться_з_позначкою_поданих()
    {
        State(PeriodState.Open);
        Sheets(Approved(1), Submitted(2), Draft(3));
        object? payload = null;
        _jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => payload = p), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~collected");

        var jobId = await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal("IRecalculationJob~collected", jobId);
        await _jobs.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.AutoTargetOf(DocumentId, Period),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        var json = System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Contains("\"recalculateSubmitted\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"triggeredByUserId\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B83")]
    public async Task Збір_на_документі_лише_з_поданим_аркушем_ставиться()
    {
        State(PeriodState.Open);
        Sheets(Submitted(1));

        Assert.Equal("IRecalculationJob~auto", await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B83")]
    public async Task Збір_на_документі_де_затверджено_все_нуль_задач()
    {
        State(PeriodState.Open);
        Sheets(Approved(1), Approved(2));

        Assert.Null(await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(
            default!, default, default, default);
    }

    /// <summary>
    /// ⛔ B63 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): залежний документ із затвердженим,
    /// поданим і чернетковим аркушем ставиться на перерахунок — з позначкою поданих і своєю ціллю.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально, B63): тіло <c>RequestDependentsAsync</c> → тодішній <c>RequestAsync</c> (будь-який
    /// поданий/затверджений — відмова) — <c>null</c>, червоний. ✎ B83: <c>RequestAsync</c> тепер за тим самим правилом.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B63")]
    public async Task Залежний_із_затвердженим_поданим_і_чернеткою_ставиться_з_позначкою_поданих()
    {
        State(PeriodState.Open);
        Sheets(Approved(1), Submitted(2), Draft(3));
        object? payload = null;
        _jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => payload = p), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~dep");

        var jobId = await Trigger().RequestDependentsAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal("IRecalculationJob~dep", jobId);
        await _jobs.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsTargetOf(DocumentId, Period),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        var json = System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Equal(
            $$"""{"documentId":{{DocumentId}},"periodKey":{{Period.Value}},"triggeredByUserId":null,"sheetDefId":null,"recalculateSubmitted":true}""",
            json);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B63")]
    public async Task Залежний_де_затверджено_все_нуль_задач()
    {
        State(PeriodState.Open);
        Sheets(Approved(1), Approved(2));

        Assert.Null(await Trigger().RequestDependentsAsync(DocumentId, Period, CancellationToken.None));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(
            default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B63")]
    public async Task Залежний_зі_складом_затверджені_стани_але_аркуш_складу_без_стану_ставиться()
    {
        // Аркуш 3 складу ще не подавався (стану немає) — він чернетка, його треба рахувати.
        State(PeriodState.Open);
        Sheets(Approved(1), Approved(2));
        _documents.GetIncludedSheetIdsAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(IncludedThree);

        var jobId = await Trigger().RequestDependentsAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal("IRecalculationJob~auto", jobId);
    }

    [Theory]
    [InlineData(PeriodState.Closed)]
    [InlineData(PeriodState.Scheduled)]
    [InlineData(null)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B63")]
    public async Task Залежний_у_закритому_чи_не_відкритому_періоді_нуль_задач(PeriodState? state)
    {
        State(state);
        Sheets(Draft(1));

        Assert.Null(await Trigger().RequestDependentsAsync(DocumentId, Period, CancellationToken.None));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(
            default!, default, default, default);
    }

    private void Sheets(params ApprovalState[] states)
        => _workflow.GetSheetsAsync(DocumentId, Period, Arg.Any<CancellationToken>()).Returns(states);

    private static ApprovalState Draft(int sheetDefId, long? sheetCopyId = null)
        => new(DocumentId, sheetDefId, Period.Value, sheetCopyId);

    private static ApprovalState Submitted(int sheetDefId, long? sheetCopyId = null)
    {
        var state = Draft(sheetDefId, sheetCopyId);
        state.Submit(userId: 9, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        return state;
    }

    private static ApprovalState Approved(int sheetDefId, long? sheetCopyId = null)
    {
        var state = Submitted(sheetDefId, sheetCopyId);
        state.Approve(userId: 10, new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
        return state;
    }
}
