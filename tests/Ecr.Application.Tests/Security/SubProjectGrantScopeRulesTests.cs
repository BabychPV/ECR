// tests/Ecr.Application.Tests/Security/SubProjectGrantScopeRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// S2 (enterprise-аудит безпеки, 2026-09-28): гранти на аркуш, таблицю й
/// колонку діють лише в проєкті, який користувач бачить (грант на проєкт ≥ Read).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати передумову проєкту в
/// <see cref="EditRules.Effective"/> → червоніють усі «без гранта на проєкт»
/// нижче (дрібніший грант знову діє сам). Контрольні тести тримають, що фікс
/// не став «None завжди».
///
/// ⚠ <c>AccessBuilder.Cell()</c> описує комірку проєкту
/// <see cref="AccessBuilder.ProjectId"/>; «проєкт A» тут — будь-який інший.
/// </remarks>
public sealed class SubProjectGrantScopeRulesTests
{
    private const int OtherProject = AccessBuilder.ProjectId + 1;

    public static TheoryData<ResourceKind, int> SubProjectScopes => new()
    {
        { ResourceKind.Sheet, AccessBuilder.SheetId },
        { ResourceKind.Table, AccessBuilder.TableId },
        { ResourceKind.Column, AccessBuilder.ColumnId },
    };

    [Theory]
    [MemberData(nameof(SubProjectScopes))]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S2")]
    public void Без_гранта_на_проєкт_дрібніший_грант_не_дає_запису(ResourceKind kind, int id)
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, OtherProject, GrantLevel.Manage)
            .Grant(kind, id, GrantLevel.Manage)
            .Build();

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, AccessBuilder.Cell()));

        var decision = EditRules.CanEdit(profile, AccessBuilder.Cell());
        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    [Theory]
    [MemberData(nameof(SubProjectScopes))]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S2")]
    public void Без_гранта_на_проєкт_дрібніший_грант_не_дає_подання_затвердження_повернення(ResourceKind kind, int id)
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, OtherProject, GrantLevel.Read)
            .Grant(kind, id, GrantLevel.Manage)
            .Build();

        Assert.Equal(
            EditDenyReason.NoGrant,
            EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false).Reason);
        Assert.Equal(
            EditDenyReason.NoGrant,
            EditRules.CanApprove(profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).Reason);
        Assert.Equal(
            EditDenyReason.NoGrant,
            EditRules.CanReopen(profile, AccessBuilder.Cell(sheet: DocumentStatus.Approved)).Reason);
    }

    /// <summary>Грант на проєкт є, але рівня <c>None</c> — видимості він не дає.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S2")]
    public void Грант_на_проєкт_рівня_None_не_є_передумовою()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.None)
            .Grant(ResourceKind.Sheet, AccessBuilder.SheetId, GrantLevel.Approve)
            .Build();

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, AccessBuilder.Cell()));
    }

    /// <summary>Контроль: під видимим проєктом аркуш ПІДНІМАЄ рівень, як і раніше.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S2")]
    public void Під_видимим_проєктом_грант_на_аркуш_піднімає_рівень()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Grant(ResourceKind.Sheet, AccessBuilder.SheetId, GrantLevel.Approve)
            .Build();

        Assert.Equal(GrantLevel.Approve, EditRules.Effective(profile, AccessBuilder.Cell()));
        Assert.True(EditRules.CanApprove(profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).IsAllowed);
    }

    /// <summary>Контроль: під видимим проєктом колонка ЗВУЖУЄ рівень, як і раніше.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S2")]
    public void Під_видимим_проєктом_грант_на_колонку_звужує_рівень()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
            .Grant(ResourceKind.Column, AccessBuilder.ColumnId, GrantLevel.Read)
            .Build();

        Assert.Equal(GrantLevel.Read, EditRules.Effective(profile, AccessBuilder.Cell()));
    }

    /// <summary>Інваріант інтеграції: запис без грантів, у межах заборон — не змінився.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "S2")]
    public void Інтеграція_й_далі_пише_без_гранта_на_проєкт_але_не_попри_заборону()
    {
        var integration = new AccessProfile
        {
            CacheKey = "svc|integration-job",
            UserId = 2,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(StringComparer.Ordinal),
            Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
            Denies = new HashSet<string>(StringComparer.Ordinal),
            RoleIds = new HashSet<int>(),
            IsIntegrationWriter = true,
        };

        Assert.Equal(GrantLevel.Write, EditRules.Effective(integration, AccessBuilder.Cell()));
        Assert.True(EditRules.CanEdit(integration, AccessBuilder.Cell()).IsAllowed);

        var denied = new AccessProfile
        {
            CacheKey = integration.CacheKey,
            UserId = integration.UserId,
            SecurityStamp = integration.SecurityStamp,
            Permissions = integration.Permissions,
            Grants = integration.Grants,
            Denies = new HashSet<string>(StringComparer.Ordinal) { $"{ResourceKind.Project}:{AccessBuilder.ProjectId}" },
            RoleIds = integration.RoleIds,
            IsIntegrationWriter = true,
        };

        Assert.Equal(GrantLevel.None, EditRules.Effective(denied, AccessBuilder.Cell()));
    }
}
