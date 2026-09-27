// tests/Ecr.Application.Tests/Security/IntegrationWriterEditRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Право запису інтеграції (<see cref="AccessProfile.IsIntegrationWriter"/>):
/// <c>Write</c> без грантів — ПІСЛЯ всіх заборон, і нічого понад запис.
/// </summary>
/// <remarks>
/// ⛔ P0. Без цього права матеріалізація PI не писала в жоден проєкт:
/// у <c>svc-integration</c> немає грантів (і не має бути — сідовий грант
/// прив'язаний до проєкту, а новий проєкт мовчки лишався б без даних).
/// Небезпека протилежна: право, що обходить заборони, писало б у закритий
/// період і в подану форму. Тому кожна заборона тут — окремий рядок.
/// </remarks>
public sealed class IntegrationWriterEditRulesTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public void Інтеграція_без_жодного_гранта_пише_у_відкритий_період()
    {
        var decision = EditRules.CanEdit(Integration(), AccessBuilder.Cell());

        Assert.True(decision.IsAllowed);

        // Контроль: той самий профіль БЕЗ ознаки — звичайний запис без грантів.
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanEdit(new AccessBuilder().Build(), AccessBuilder.Cell()).Reason);
    }

    /// <summary>Кожна заборона комірки діє на інтеграцію так само, як на людину.</summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ (г): поставити <c>if (profile.IsIntegrationWriter) return Allow()</c>
    /// на початок <see cref="EditRules.CanEdit"/> (прапорець ДО заборон) — червоніє
    /// кожен рядок, першим <c>PeriodClosed</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    [InlineData("closed", EditDenyReason.PeriodClosed)]
    [InlineData("scheduled", EditDenyReason.PeriodNotOpenYet)]
    [InlineData("submitted", EditDenyReason.DocumentSubmitted)]
    [InlineData("approved", EditDenyReason.DocumentApproved)]
    [InlineData("computed", EditDenyReason.CalculatedCell)]
    [InlineData("columnReadOnly", EditDenyReason.ColumnReadOnly)]
    [InlineData("rowReadOnly", EditDenyReason.RowReadOnly)]
    [InlineData("outOfWindow", EditDenyReason.OutOfAccessWindow)]
    [InlineData("archived", EditDenyReason.ProjectArchived)]
    [InlineData("archiving", EditDenyReason.ArchivingInProgress)]
    public void Заборони_комірки_діють_на_інтеграцію(string condition, EditDenyReason expected)
    {
        var cell = condition switch
        {
            "closed" => AccessBuilder.Cell(period: PeriodState.Closed),
            "scheduled" => AccessBuilder.Cell(period: PeriodState.Scheduled),
            "submitted" => AccessBuilder.Cell(sheet: DocumentStatus.Submitted),
            "approved" => AccessBuilder.Cell(sheet: DocumentStatus.Approved),
            "computed" => AccessBuilder.Cell(computed: true),
            "columnReadOnly" => AccessBuilder.Cell(columnReadOnly: true),
            "rowReadOnly" => AccessBuilder.Cell(rowReadOnly: true),
            "outOfWindow" => AccessBuilder.Cell(outOfWindow: true),
            "archived" => AccessBuilder.Cell(project: ProjectStatus.Archived),
            "archiving" => AccessBuilder.Cell(archiving: true),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        var decision = EditRules.CanEdit(Integration(), cell);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public void Явна_заборона_на_проєкт_вимикає_запис_інтеграції()
    {
        var denied = Integration(new AccessBuilder().Deny(ResourceKind.Project, AccessBuilder.ProjectId));

        var decision = EditRules.CanEdit(denied, AccessBuilder.Cell());

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public void Рівень_інтеграції_рівно_Write_навіть_із_ширшим_грантом()
    {
        var withManage = Integration(
            new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage));

        Assert.Equal(GrantLevel.Write, EditRules.Effective(withManage, AccessBuilder.Cell()));
        Assert.Equal(GrantLevel.Write, EditRules.Effective(Integration(), AccessBuilder.Cell()));
    }

    /// <summary>(в) Подання, затвердження, повернення — дії людини.</summary>
    /// <remarks>
    /// ⚠ Профіль навмисно з грантом <c>Manage</c>: доводить, що відмову дає
    /// ознака інтеграції, а не брак рівня.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public void Подання_затвердження_і_повернення_від_інтеграції_відхиляються()
    {
        var profile = Integration(
            new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage));

        var submit = EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false);
        var approve = EditRules.CanApprove(profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted));
        var reopen = EditRules.CanReopen(profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted));

        Assert.False(submit.IsAllowed);
        Assert.False(approve.IsAllowed);
        Assert.False(reopen.IsAllowed);
        Assert.All([submit, approve, reopen], d => Assert.Equal(EditDenyReason.NoGrant, d.Reason));

        // Контроль: той самий грант без ознаки подає, затверджує й повертає.
        var human = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage).Build();
        Assert.True(EditRules.CanSubmit(human, AccessBuilder.Cell(), hasBlockingErrors: false).IsAllowed);
        Assert.True(EditRules.CanApprove(human, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).IsAllowed);
        Assert.True(EditRules.CanReopen(human, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).IsAllowed);
    }

    /// <summary>Профіль автора задачі інтеграції поверх звичайного профілю.</summary>
    private static AccessProfile Integration(AccessBuilder? builder = null)
    {
        var own = (builder ?? new AccessBuilder()).Build();

        return new AccessProfile
        {
            CacheKey = own.CacheKey + "|integration-job",
            UserId = own.UserId,
            SecurityStamp = own.SecurityStamp,
            Permissions = own.Permissions,
            Grants = own.Grants,
            Denies = own.Denies,
            RoleIds = own.RoleIds,
            IsIntegrationWriter = true,
        };
    }
}
