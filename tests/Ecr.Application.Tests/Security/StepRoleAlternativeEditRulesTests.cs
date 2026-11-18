// tests/Ecr.Application.Tests/Security/StepRoleAlternativeEditRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// ✎ B155: ОДНЕ правило кроку для затвердження й повернення в роботу — роль кроку АБО альтернативна (загальний
/// <c>Approver</c> на маршруті шаблону); грант <c>Approve</c> лишається обов'язковим.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ <c>EditRules.HoldsStepRole</c>: прибрати <c>held.Contains(roleId)</c> — червоніють «носій ролі кроку»;
/// прибрати гілку альтернативи — червоніють «носій альтернативи»; повернути <c>true</c> — червоніють «ні того, ні
/// іншого».
/// </remarks>
public sealed class StepRoleAlternativeEditRulesTests
{
    private const int StepRole = 501;
    private const int General = 502;
    private const int Other = 503;

    private static AccessProfile Holder(params int[] roles)
    {
        var builder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve);
        foreach (var role in roles)
        {
            builder.Role(role);
        }

        return builder.Build();
    }

    public static TheoryData<DocumentStatus> States => new() { DocumentStatus.Submitted, DocumentStatus.Approved };

    [Fact]
    [Trait("Decision", "B155")]
    public void Затвердження_носій_ролі_кроку_або_альтернативи_так_інший_ні()
    {
        var submitted = AccessBuilder.Cell(sheet: DocumentStatus.Submitted);

        Assert.True(EditRules.CanApprove(Holder(StepRole), submitted, StepRole, General).IsAllowed, "носій ролі кроку");
        Assert.True(EditRules.CanApprove(Holder(General), submitted, StepRole, General).IsAllowed, "носій альтернативи");
        Assert.False(EditRules.CanApprove(Holder(Other), submitted, StepRole, General).IsAllowed, "ні того, ні іншого");

        // Без альтернативи (маршрут проєкту) — загальний не учасник кроку.
        Assert.False(EditRules.CanApprove(Holder(General), submitted, StepRole).IsAllowed, "без альтернативи");
    }

    [Theory]
    [MemberData(nameof(States))]
    [Trait("Decision", "B155")]
    public void Повернення_в_роботу_те_саме_правило_кроку(DocumentStatus state)
    {
        var sheet = AccessBuilder.Cell(sheet: state);

        Assert.True(EditRules.CanReopen(Holder(StepRole), sheet, StepRole, General).IsAllowed, "носій ролі кроку");
        Assert.True(EditRules.CanReopen(Holder(General), sheet, StepRole, General).IsAllowed, "носій альтернативи");
        Assert.False(EditRules.CanReopen(Holder(Other), sheet, StepRole, General).IsAllowed, "ні того, ні іншого");
        Assert.False(EditRules.CanReopen(Holder(General), sheet, StepRole).IsAllowed, "без альтернативи");
    }

    [Fact]
    [Trait("Decision", "B155")]
    public void Альтернатива_не_замінює_гранта_Approve()
    {
        var submitted = AccessBuilder.Cell(sheet: DocumentStatus.Submitted);
        var noGrant = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Role(General).Build();

        Assert.False(EditRules.CanApprove(noGrant, submitted, StepRole, General).IsAllowed);
        Assert.False(EditRules.CanReopen(noGrant, submitted, StepRole, General).IsAllowed);
    }
}
