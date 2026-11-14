using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// B100 (рішення людини 13.11, «варіант 1»): ознака <see cref="CellAccessContext.OutOfAccessWindow"/> зупиняє ЛИШЕ
/// правку; подання, затвердження й повернення в роботу її не враховують.
/// </summary>
/// <remarks>
/// Сторож від повернення вікна в дії робочого процесу: додати перевірку ознаки в <c>CanSubmit</c>, <c>CanApprove</c>
/// чи <c>CanReopen</c> → червоне. Пара — правка з тією самою ознакою відмовлена (<c>A08</c> у <c>AccessDecisionTests</c>).
/// </remarks>
public sealed class AccessWindowWorkflowRulesTests
{
    private static AccessProfile Approver()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve).Build();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "B100")]
    public void Поза_вікном_правка_відмовлена_а_подання_затвердження_й_повернення_дозволені()
    {
        var profile = Approver();

        var edit = EditRules.CanEdit(profile, AccessBuilder.Cell(outOfWindow: true));
        Assert.Equal(EditDenyReason.OutOfAccessWindow, edit.Reason);

        Assert.True(EditRules.CanSubmit(
            profile, AccessBuilder.Cell(outOfWindow: true), hasBlockingErrors: false).IsAllowed);
        Assert.True(EditRules.CanApprove(
            profile, AccessBuilder.Cell(outOfWindow: true, sheet: DocumentStatus.Submitted)).IsAllowed);
        Assert.True(EditRules.CanReopen(
            profile, AccessBuilder.Cell(outOfWindow: true, sheet: DocumentStatus.Submitted)).IsAllowed);
        Assert.True(EditRules.CanReopen(
            profile, AccessBuilder.Cell(outOfWindow: true, sheet: DocumentStatus.Approved)).IsAllowed);
    }
}
