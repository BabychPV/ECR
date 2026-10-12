// tests/Ecr.Application.Tests/Security/RejectedSheetEditRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// ФВ-5.15 / D-219: відхилений аркуш (<c>Rejected</c>) для правил доступу — те саме, що
/// <c>Draft</c>: його редагують, подають знову, але не затверджують і не «відкривають».
/// </summary>
/// <remarks>
/// ⛔ До цього файлу жоден тест не віддавав у <see cref="EditRules"/> стан <c>Rejected</c>:
/// <c>AccessBuilder.Cell(sheet: …)</c> ніколи не діставав його значення, тож додати
/// <c>case DocumentStatus.Rejected: Deny(…)</c> у <see cref="EditRules.CanEdit"/> (відхилений
/// аркуш став би «замороженим» — автор не може виправити те, що від нього просили виправити)
/// не помітив би жоден гейт.
///
/// Мутаційні докази (у detached-worktree, не комітяться): (а) у <c>EditRules.CanEdit</c> додати
/// <c>case DocumentStatus.Rejected: return Deny(DocumentSubmitted)</c> — червоніють
/// «Rejected_редагується…» і «Rejected_рішення_збігаються_з_Draft…»; (б) у <c>CanSubmit</c>
/// додати <c>or DocumentStatus.Rejected</c> до відмови за станом — червоніють
/// «Rejected_подається_знову…» і порівняння з Draft; (в) у <c>CanApprove</c> замінити
/// <c>!= Submitted</c> на <c>is Draft</c> — «Rejected_не_затверджується…».
/// </remarks>
public sealed class RejectedSheetEditRulesTests
{
    private static AccessProfile Level(GrantLevel level)
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, level).Build();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Decision", "D-219")]
    public void Rejected_редагується_як_Draft_і_закритий_період_усе_одно_блокує()
    {
        var writer = Level(GrantLevel.Write);

        Assert.True(
            EditRules.CanEdit(writer, AccessBuilder.Cell(sheet: DocumentStatus.Rejected)).IsAllowed,
            "відхилений аркуш мусить лишатися редагованим: автор виправляє те, що від нього просили");

        // Контроль «до/після»: Submitted/Approved ті самі правила блокують, Rejected — ні.
        Assert.Equal(
            EditDenyReason.DocumentSubmitted,
            EditRules.CanEdit(writer, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).Reason);
        Assert.Equal(
            EditDenyReason.DocumentApproved,
            EditRules.CanEdit(writer, AccessBuilder.Cell(sheet: DocumentStatus.Approved)).Reason);

        // ⚠ Rejected не відкриває обхід інших умов: закритий період, архів і гранти діють як завжди.
        Assert.Equal(
            EditDenyReason.PeriodClosed,
            EditRules.CanEdit(
                writer, AccessBuilder.Cell(period: PeriodState.Closed, sheet: DocumentStatus.Rejected)).Reason);
        Assert.Equal(
            EditDenyReason.ProjectArchived,
            EditRules.CanEdit(
                writer, AccessBuilder.Cell(project: ProjectStatus.Archived, sheet: DocumentStatus.Rejected)).Reason);
        Assert.Equal(
            EditDenyReason.InsufficientGrantLevel,
            EditRules.CanEdit(
                Level(GrantLevel.Read), AccessBuilder.Cell(sheet: DocumentStatus.Rejected)).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Decision", "D-219")]
    public void Rejected_подається_знову_а_Submitted_і_Approved_ні()
    {
        var submitter = Level(GrantLevel.Submit);

        Assert.True(
            EditRules.CanSubmit(submitter, AccessBuilder.Cell(sheet: DocumentStatus.Rejected), hasBlockingErrors: false)
                .IsAllowed,
            "відхилений аркуш подається знову (ApprovalState.Submit допускає Draft і Rejected)");

        Assert.Equal(
            EditDenyReason.DocumentSubmitted,
            EditRules.CanSubmit(submitter, AccessBuilder.Cell(sheet: DocumentStatus.Submitted), false).Reason);
        Assert.Equal(
            EditDenyReason.DocumentApproved,
            EditRules.CanSubmit(submitter, AccessBuilder.Cell(sheet: DocumentStatus.Approved), false).Reason);

        // ⚠ Блокувальні помилки валідації зупиняють і повторне подання відхиленого.
        var blocked = EditRules.CanSubmit(
            submitter, AccessBuilder.Cell(sheet: DocumentStatus.Rejected), hasBlockingErrors: true);
        Assert.False(blocked.IsAllowed);
        Assert.Equal(EditDenyReason.BusinessRule, blocked.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Decision", "D-219")]
    public void Rejected_не_затверджується_і_не_повертається_в_роботу_бо_вже_не_подане()
    {
        var approver = Level(GrantLevel.Approve);
        var rejected = AccessBuilder.Cell(sheet: DocumentStatus.Rejected);

        var approve = EditRules.CanApprove(approver, rejected);
        Assert.False(approve.IsAllowed);
        Assert.Equal(EditDenyReason.BusinessRule, approve.Reason);

        var reopen = EditRules.CanReopen(approver, rejected);
        Assert.False(reopen.IsAllowed);
        Assert.Equal(EditDenyReason.BusinessRule, reopen.Reason);

        // Контроль: Submitted затверджується тим самим профілем — відмова вище саме за станом.
        Assert.True(EditRules.CanApprove(approver, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).IsAllowed);
    }

    /// <summary>
    /// D-219 дослівно: «Rejected як Draft». Усі чотири рішення на Rejected збігаються з Draft за
    /// будь-яких грантів — так мутація, що чіпає лише гілку Rejected, не проходить мовчки.
    /// </summary>
    [Theory]
    [InlineData(GrantLevel.Read)]
    [InlineData(GrantLevel.Write)]
    [InlineData(GrantLevel.Submit)]
    [InlineData(GrantLevel.Approve)]
    [InlineData(GrantLevel.Manage)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.15")]
    [Trait("Decision", "D-219")]
    public void Rejected_рішення_збігаються_з_Draft_за_будь_якого_рівня_гранта(GrantLevel level)
    {
        var profile = Level(level);
        var draft = AccessBuilder.Cell(sheet: DocumentStatus.Draft);
        var rejected = AccessBuilder.Cell(sheet: DocumentStatus.Rejected);

        Assert.Equal(EditRules.CanEdit(profile, draft), EditRules.CanEdit(profile, rejected));
        Assert.Equal(
            EditRules.CanSubmit(profile, draft, hasBlockingErrors: false),
            EditRules.CanSubmit(profile, rejected, hasBlockingErrors: false));
        Assert.Equal(EditRules.CanApprove(profile, draft).IsAllowed, EditRules.CanApprove(profile, rejected).IsAllowed);
        Assert.Equal(EditRules.CanReopen(profile, draft).IsAllowed, EditRules.CanReopen(profile, rejected).IsAllowed);
    }
}
