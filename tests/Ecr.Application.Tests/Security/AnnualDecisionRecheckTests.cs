// tests/Ecr.Application.Tests/Security/AnnualDecisionRecheckTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// ✎ B79 (Q9): повторна перевірка річного рішення під замками аркушів року — правило для записувачів історії дозволу.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (локально): (1) прибрати гілку <c>DependentInput</c> — «Стовпець_із_залежними…» дає іншу причину, а
/// «…поданий_не_блокує» червоніє; (2) прибрати виняток тексту якоря — «Текст_якоря…»; (3) замість відмови без замка
/// повертатись мовчки — «Річне_рішення_без_замка…»; (4) не брати замки (стани = Draft) — усі відмови зеленіють хибно,
/// червоніють «Стовпець_із_залежними…» і «Без_залежних…»; (5) прибрати ранній вихід для нерічного рішення —
/// «Нерічне_рішення…».
/// </remarks>
public sealed class AnnualDecisionRecheckTests
{
    private const long DocumentId = 71;
    private const int SheetDefId = 5;
    private const int Jan = 209901;
    private const int Feb = 209902;

    private static readonly IReadOnlyList<int> Year = [Feb, Jan];

    private static ISheetEditGate Gate(DocumentStatus january, DocumentStatus february, long? copyId = null)
    {
        var gate = Substitute.For<ISheetEditGate>();
        if (copyId is { } copy)
        {
            gate.EnterCopyEditAsync(DocumentId, SheetDefId, new PeriodKey(Jan), copy, Arg.Any<CancellationToken>()).Returns(january);
            gate.EnterCopyEditAsync(DocumentId, SheetDefId, new PeriodKey(Feb), copy, Arg.Any<CancellationToken>()).Returns(february);
        }
        else
        {
            gate.EnterEditAsync(DocumentId, SheetDefId, new PeriodKey(Jan), Arg.Any<CancellationToken>()).Returns(january);
            gate.EnterEditAsync(DocumentId, SheetDefId, new PeriodKey(Feb), Arg.Any<CancellationToken>()).Returns(february);
        }

        return gate;
    }

    private static EditDecision Annual(bool dependent = false, bool text = false)
        => EditDecision.Allow() with { AnnualPeriods = Year, DependentInput = dependent, AnnualTextOnly = text };

    private static Task RunAsync(ISheetEditGate? gate, EditDecision decision, long? copyId = null)
        => AnnualDecisionRecheck.EnsureAsync(gate, DocumentId, SheetDefId, copyId, decision, 1, CancellationToken.None);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Стовпець_із_залежними_затверджений_місяць_під_замком_відхиляє_з_переліком()
    {
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => RunAsync(Gate(DocumentStatus.Approved, DocumentStatus.Draft), Annual(dependent: true)));

        Assert.Equal(nameof(EditDenyReason.DependentMonthsLocked), denied.Details?["reason"]);
        Assert.Equal(new[] { Jan }, Assert.IsType<int[]>(denied.Details?[DependentMonthsRule.MonthsDetail]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Стовпець_із_залежними_поданий_рік_не_блокує_а_без_залежних_DocumentSubmitted()
    {
        await RunAsync(Gate(DocumentStatus.Submitted, DocumentStatus.Submitted), Annual(dependent: true));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => RunAsync(Gate(DocumentStatus.Submitted, DocumentStatus.Submitted), Annual()));
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), denied.Details?["reason"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Без_залежних_агрегат_року_один_затверджений_і_чернетка_пише_усі_затверджені_DocumentApproved()
    {
        await RunAsync(Gate(DocumentStatus.Approved, DocumentStatus.Draft), Annual());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => RunAsync(Gate(DocumentStatus.Approved, DocumentStatus.Approved), Annual()));
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", denied.Details?["messageKey"]);
        Assert.Equal(nameof(EditDenyReason.DocumentApproved), denied.Details?["reason"]);
        Assert.False(denied.Details!.ContainsKey(DependentMonthsRule.MonthsDetail));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Текст_якоря_поданий_рік_пише_затверджений_ні()
    {
        await RunAsync(Gate(DocumentStatus.Submitted, DocumentStatus.Submitted), Annual(text: true));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => RunAsync(Gate(DocumentStatus.Approved, DocumentStatus.Approved), Annual(text: true)));
        Assert.Equal(nameof(EditDenyReason.DocumentApproved), denied.Details?["reason"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Нерічне_рішення_замків_не_бере_і_без_замка_не_падає()
    {
        var gate = Substitute.For<ISheetEditGate>();

        await RunAsync(gate, EditDecision.Allow());
        await RunAsync(null, EditDecision.Allow());

        Assert.Empty(gate.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Річне_рішення_без_замка_відмовляє_а_не_пропускає()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(null, Annual(dependent: true)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public async Task Замки_беруться_за_зростанням_періоду_і_для_копії_аркуша_її_власні()
    {
        var gate = Gate(DocumentStatus.Approved, DocumentStatus.Draft, copyId: 9);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => RunAsync(gate, Annual(dependent: true), copyId: 9));

        Assert.Equal(nameof(EditDenyReason.DependentMonthsLocked), denied.Details?["reason"]);
        Received.InOrder(() =>
        {
            gate.EnterCopyEditAsync(DocumentId, SheetDefId, new PeriodKey(Jan), 9, Arg.Any<CancellationToken>());
            gate.EnterCopyEditAsync(DocumentId, SheetDefId, new PeriodKey(Feb), 9, Arg.Any<CancellationToken>());
        });
        await gate.DidNotReceive().EnterEditAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }
}
