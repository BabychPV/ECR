// tests/Ecr.Application.Tests/Security/SubmitPeriodStateRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// D-441 (AN-128): подати можна лише період у стані <c>Open</c> або <c>Grace</c>. До цього
/// <c>EditRules.CanSubmit</c> відмовляв лише для <c>Closed</c>, і ще не відкритий
/// (<c>Scheduled</c>) період подавався.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: прибрати гілку <c>case PeriodState.Scheduled</c> у <c>EditRules.CanSubmit</c> —
/// червоніє «Scheduled — відмова PeriodNotOpenYet» (на старому коді рішення було <c>Allow</c>).
/// </remarks>
public sealed class SubmitPeriodStateRulesTests
{
    private static AccessProfile Submitter()
        => new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Submit)
            .Build();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Scheduled_відмова_PeriodNotOpenYet_ДО_було_Allow()
    {
        var decision = EditRules.CanSubmit(
            Submitter(), AccessBuilder.Cell(period: PeriodState.Scheduled), hasBlockingErrors: false);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.PeriodNotOpenYet, decision.Reason);
    }

    [Theory]
    [InlineData(PeriodState.Open)]
    [InlineData(PeriodState.Grace)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Open_і_Grace_подаються_як_і_раніше(PeriodState state)
    {
        var decision = EditRules.CanSubmit(Submitter(), AccessBuilder.Cell(period: state), hasBlockingErrors: false);

        Assert.True(decision.IsAllowed, decision.Reason.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Closed_як_і_раніше_PeriodClosed()
    {
        var decision = EditRules.CanSubmit(
            Submitter(), AccessBuilder.Cell(period: PeriodState.Closed), hasBlockingErrors: false);

        Assert.Equal(EditDenyReason.PeriodClosed, decision.Reason);
    }

    /// <summary>
    /// Стан періоду перевіряється ДО стану аркуша й гранта — та сама черговість, що й у <c>CanEdit</c>:
    /// «ще не відкрито» — причина, яку бачить кожен, незалежно від рівня.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Scheduled_перемагає_брак_гранта()
    {
        var reader = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Build();

        var decision = EditRules.CanSubmit(
            reader, AccessBuilder.Cell(period: PeriodState.Scheduled), hasBlockingErrors: false);

        Assert.Equal(EditDenyReason.PeriodNotOpenYet, decision.Reason);
    }
}
