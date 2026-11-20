// tests/Ecr.Application.Tests/Workflow/SourceImpactCheckTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B80 N5 (D-554): матриця політики зв'язки × підтвердження × перекриття «й затверджені залежні»
/// (<see cref="SourceImpactCheck"/>; рішення людини 19.11).
/// </summary>
/// <remarks>
/// Мутації: прибрати перевірку права (<c>mayRecalculateApproved</c>) — червоніє «без права 403»; прибрати вимогу причини
/// перекриття — червоніє «без причини 422»; зняти умову <c>impact.ApprovedCount &gt; 0</c> в <c>overrideRequested</c> —
/// червоніє «немає затверджених залежних — перекриття не діє»; прибрати гілку <c>Block</c> — червоніє «Block з перекриттям».
/// </remarks>
public sealed class SourceImpactCheckTests
{
    private const long DocumentId = 10;
    private const int SheetDefId = 20;
    private const int PeriodKey = 202603;
    private const int UserId = 7;
    private static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

    private static ISourceImpactAssessor Assessor(DocumentSourcePolicy policy, int approved, int pending = 0)
    {
        var consumers = new List<SourceImpactConsumer>();
        for (var i = 0; i < approved; i++)
        {
            consumers.Add(new SourceImpactConsumer(1, "B", "LAND", 100 + i, "P", "K", PeriodKey, Approved: true, Sheets: []));
        }

        for (var i = 0; i < pending; i++)
        {
            consumers.Add(new SourceImpactConsumer(1, "B", "LAND", 200 + i, "P", "K", PeriodKey, Approved: false, Sheets: []));
        }

        var assessor = Substitute.For<ISourceImpactAssessor>();
        assessor.AssessAsync(DocumentId, SheetDefId, PeriodKey, Arg.Any<SourceApprovalChange>(), Arg.Any<CancellationToken>())
            .Returns(call => new SourceImpact(
                call.Arg<SourceApprovalChange>(), DocumentId, SheetDefId, PeriodKey, policy, [PeriodKey], consumers, "h1"));
        return assessor;
    }

    private static Task<ApprovedRecalculationOverride?> Check(
        ISourceImpactAssessor assessor, SourceImpactConfirmation? confirmation, bool mayRecalculateApproved, IAuditWriter? audit = null)
        => SourceImpactCheck.EnsureConfirmedAsync(
            assessor, audit, DocumentId, SheetDefId, PeriodKey, SourceApprovalChange.Reopened, confirmation, UserId, Now,
            "corr", CancellationToken.None, mayRecalculateApproved);

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Без_перекриття_повертає_null_і_діє_політика_Warn()
    {
        var assessor = Assessor(DocumentSourcePolicy.Warn, approved: 1);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Check(assessor, null, mayRecalculateApproved: true));
        Assert.Equal("ECR-SRC-0409", error.ErrorCode);

        Assert.Null(await Check(assessor, new SourceImpactConfirmation("h1", null), mayRecalculateApproved: true));
    }

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Перекриття_без_права_403_і_нічого_не_пишеться_в_журнал()
    {
        var assessor = Assessor(DocumentSourcePolicy.Warn, approved: 1);
        var audit = Substitute.For<IAuditWriter>();

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Check(assessor, new SourceImpactConfirmation("h1", "причина", RecalculateApproved: true), mayRecalculateApproved: false, audit));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        await audit.DidNotReceiveWithAnyArgs().WriteSecurityEventAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [Trait("Directive", "B80")]
    public async Task Перекриття_без_причини_422_навіть_за_Warn(string? reason)
    {
        var assessor = Assessor(DocumentSourcePolicy.Warn, approved: 1);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Check(assessor, new SourceImpactConfirmation("h1", reason, RecalculateApproved: true), mayRecalculateApproved: true));

        Assert.Equal("ECR-SRC-0422", error.ErrorCode);
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(error.Details);
        Assert.Equal(SourceImpactCheck.RecalculateApprovedReasonRequiredMessageKey, details["messageKey"]);
    }

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Перекриття_з_правом_і_причиною_повертає_хто_і_чому_та_пише_аудит()
    {
        var assessor = Assessor(DocumentSourcePolicy.ConfirmWithReason, approved: 2, pending: 1);
        var audit = Substitute.For<IAuditWriter>();

        var result = await Check(
            assessor, new SourceImpactConfirmation("h1", "  Акт 12  ", RecalculateApproved: true), mayRecalculateApproved: true, audit);

        Assert.Equal(new ApprovedRecalculationOverride(UserId, "Акт 12"), result);
        await audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(r => r.EventType == SourceImpactCheck.ConfirmedEventType
                                              && r.DetailsJson!.Contains("\"recalculateApproved\":true", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Політика_None_з_перекриттям_вимагає_відбитка_і_діє_лише_коли_є_затверджені_залежні()
    {
        // None + є затверджені: перекриття запитане — відбиток обов'язковий (409 без нього), далі діє.
        var withApproved = Assessor(DocumentSourcePolicy.None, approved: 1);
        var stale = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Check(withApproved, new SourceImpactConfirmation("old", "причина", RecalculateApproved: true), mayRecalculateApproved: true));
        Assert.Equal("ECR-SRC-0409", stale.ErrorCode);
        Assert.NotNull(await Check(
            withApproved, new SourceImpactConfirmation("h1", "причина", RecalculateApproved: true), mayRecalculateApproved: true));

        // Немає затверджених залежних — перекриття нічого не дає: null, без перевірки права.
        var onlyPending = Assessor(DocumentSourcePolicy.None, approved: 0, pending: 2);
        Assert.Null(await Check(
            onlyPending, new SourceImpactConfirmation("h1", "причина", RecalculateApproved: true), mayRecalculateApproved: false));
    }

    [Fact]
    [Trait("Directive", "B80")]
    public async Task Block_відмовляє_навіть_з_правом_і_перекриттям()
    {
        var assessor = Assessor(DocumentSourcePolicy.Block, approved: 1);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Check(assessor, new SourceImpactConfirmation("h1", "причина", RecalculateApproved: true), mayRecalculateApproved: true));

        Assert.Equal("ECR-SRC-0422", error.ErrorCode);
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(error.Details);
        Assert.Equal(SourceImpactCheck.BlockedMessageKey, details["messageKey"]);
    }
}
