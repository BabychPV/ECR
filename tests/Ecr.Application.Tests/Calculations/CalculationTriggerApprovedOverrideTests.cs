// tests/Ecr.Application.Tests/Calculations/CalculationTriggerApprovedOverrideTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// B80 N5 (D-554, рішення людини 19.11): перекриття «й затверджені залежні» в <see cref="CalculationTrigger"/> —
/// окрема ціль черги (не зливається з постановкою без перекриття), «усе затверджено» не відмовляє, закритий період — так.
/// </summary>
/// <remarks>
/// Мутації: у <c>WritableAsync</c> прибрати <c>ignoreAllApproved</c> — червоніє «усе затверджено, перекриття ставить»;
/// замінити <c>DependentsApprovedTargetOf</c> на <c>DependentsTargetOf</c> у <c>RequestDependentsAsync</c> — червоніє «ціль своя».
/// </remarks>
public sealed class CalculationTriggerApprovedOverrideTests
{
    private const long DocumentId = 4601;
    private static readonly PeriodKey Period = new(202601);
    private static readonly ApprovedRecalculationOverride Override = new(9, "Акт 12");

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IDocumentSheetCopyStore _sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
    private object? _payload;

    public CalculationTriggerApprovedOverrideTests()
    {
        _jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Do<object?>(p => _payload = p), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~x");
        var approved = new ApprovalState(DocumentId, 5, Period.Value);
        approved.Submit(1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        approved.Approve(2, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _workflow.GetSheetsAsync(DocumentId, Period, Arg.Any<CancellationToken>()).Returns([approved]);
        _documents.GetIncludedSheetIdsAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _sheetCopies.ListGroupsAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());
    }

    private CalculationTrigger Trigger() => new(_jobs, _periods, _workflow, _documents, _sheetCopies);

    private void State(PeriodState? state)
        => _periods.FindPeriodStateAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>()).Returns(state);

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Усе_затверджено_без_перекриття_нічого_не_ставить_а_з_перекриттям_ставить_на_свою_ціль()
    {
        State(PeriodState.Open);

        Assert.Null(await Trigger().RequestDependentsAsync(DocumentId, Period, sourceHop: 1, CancellationToken.None));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);

        var jobId = await Trigger().RequestDependentsAsync(DocumentId, Period, sourceHop: 1, Override, CancellationToken.None);

        Assert.Equal("IRecalculationJob~x", jobId);
        await _jobs.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.DependentsApprovedTargetOf(DocumentId, Period), Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        Assert.NotEqual(
            CalculationTrigger.DependentsTargetOf(DocumentId, Period), CalculationTrigger.DependentsApprovedTargetOf(DocumentId, Period));
    }

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Payload_перекриття_несе_ознаку_автора_і_причину()
    {
        State(PeriodState.Open);

        await Trigger().RequestDependentsAsync(DocumentId, Period, sourceHop: 2, Override, CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(_payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Equal(
            $$"""{"documentId":{{DocumentId}},"periodKey":{{Period.Value}},"triggeredByUserId":null,"sheetDefId":null,"recalculateSubmitted":true,"sourceHop":2,"includeApproved":true,"overrideUserId":9,"overrideReason":"Акт 12"}""",
            json);
    }

    [Theory]
    [InlineData(PeriodState.Closed)]
    [InlineData(PeriodState.Scheduled)]
    [InlineData(null)]
    [Trait("Directive", "B80")]
    public async Task Закритий_чи_невідомий_період_перекриття_не_обходить(PeriodState? state)
    {
        State(state);

        Assert.Null(await Trigger().RequestDependentsAsync(DocumentId, Period, sourceHop: 1, Override, CancellationToken.None));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(default!, default, default, default);
    }

    [Fact]
    [Trait("Directive", "B80")]
    public void Ціль_задачі_джерела_з_перекриттям_окрема_і_без_перекриття_лишається_як_була()
    {
        Assert.Equal("source-change-b3-y2026-m3", RegistryImpactRecalculationTarget.ForSourceChange(3, 2026, 3));
        Assert.Equal("source-change-b3-y2026-m3-approved", RegistryImpactRecalculationTarget.ForSourceChange(3, 2026, 3, includeApproved: true));
        Assert.Equal("source-change-b3-y2026", RegistryImpactRecalculationTarget.ForSourceChange(3, 2026, null));
        Assert.Equal("source-change-b3-y2026-approved", RegistryImpactRecalculationTarget.ForSourceChange(3, 2026, null, includeApproved: true));
    }
}
