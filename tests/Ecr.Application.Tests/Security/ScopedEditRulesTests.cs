// tests/Ecr.Application.Tests/Security/ScopedEditRulesTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Правила доступу над профілем з ролями, обмеженими областю дії (ФВ-6.14):
/// шар проєкту діє лише в своєму проєкті.
/// </summary>
public sealed class ScopedEditRulesTests
{
    private const int OtherProject = 11;
    private const int ScopedRole = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Грант_на_аркуш_з_шару_проєкту_діє_лише_в_ньому()
    {
        var profile = Scoped(
            new AccessBuilder()
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
                .Grant(ResourceKind.Project, OtherProject, GrantLevel.Read),
            AccessBuilder.ProjectId,
            grants: new() { [$"{ResourceKind.Sheet}:{AccessBuilder.SheetId}"] = GrantLevel.Approve });

        var here = AccessBuilder.Cell(sheet: DocumentStatus.Submitted);
        var there = here with { ProjectId = OtherProject };

        Assert.Equal(GrantLevel.Approve, EditRules.Effective(profile, here));
        Assert.Equal(GrantLevel.Read, EditRules.Effective(profile, there));
        Assert.True(EditRules.CanApprove(profile, here).IsAllowed);
        Assert.False(EditRules.CanApprove(profile, there).IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Заборона_з_шару_проєкту_виграє_лише_в_ньому()
    {
        var profile = Scoped(
            new AccessBuilder()
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
                .Grant(ResourceKind.Project, OtherProject, GrantLevel.Write),
            AccessBuilder.ProjectId,
            denies: [$"{ResourceKind.Column}:{AccessBuilder.ColumnId}"]);

        var here = AccessBuilder.Cell();

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, here));
        Assert.Equal(GrantLevel.Write, EditRules.Effective(profile, here with { ProjectId = OtherProject }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Шар_проєкту_не_відкриває_невидимий_проєкт()
    {
        // ⚠ S2 діє й на шар: грант на аркуш без видимого проєкту не дає нічого.
        var profile = Scoped(
            new AccessBuilder(),
            AccessBuilder.ProjectId,
            grants: new() { [$"{ResourceKind.Sheet}:{AccessBuilder.SheetId}"] = GrantLevel.Approve });

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, AccessBuilder.Cell()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Роль_кроку_маршруту_з_областю_чинна_лише_в_її_проєкті()
    {
        var profile = Scoped(
            new AccessBuilder()
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve)
                .Grant(ResourceKind.Project, OtherProject, GrantLevel.Approve),
            AccessBuilder.ProjectId);

        var here = AccessBuilder.Cell(sheet: DocumentStatus.Submitted);

        Assert.True(EditRules.CanApprove(profile, here, requiredRoleId: ScopedRole).IsAllowed);
        Assert.False(EditRules.CanApprove(profile, here with { ProjectId = OtherProject }, requiredRoleId: ScopedRole).IsAllowed);
        Assert.Contains(ScopedRole, profile.RoleIds);
    }

    /// <summary>Профіль без областей: <see cref="AccessProfile.RoleIdsIn"/> — це <see cref="AccessProfile.RoleIds"/>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Без_областей_ролі_в_проєкті_ті_самі()
    {
        var profile = new AccessBuilder().Role(3).Build();

        Assert.Same(profile.RoleIds, profile.RoleIdsIn(AccessBuilder.ProjectId));
    }

    /// <summary>Профіль з роллю <see cref="ScopedRole"/>, чия область — <paramref name="projectId"/>.</summary>
    private static AccessProfile Scoped(
        AccessBuilder unscoped, int projectId,
        Dictionary<string, GrantLevel>? grants = null, HashSet<string>? denies = null)
    {
        var baseline = unscoped.Build();

        return new AccessProfile
        {
            CacheKey = baseline.CacheKey,
            UserId = baseline.UserId,
            SecurityStamp = baseline.SecurityStamp,
            Permissions = baseline.Permissions,
            Grants = baseline.Grants,
            Denies = baseline.Denies,
            RoleIds = new HashSet<int>(baseline.RoleIds) { ScopedRole },
            UnscopedRoleIds = baseline.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [projectId] = new(
                    grants ?? new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                    denies ?? new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<int>(baseline.RoleIds) { ScopedRole },
                    new HashSet<string>(StringComparer.Ordinal)),
            },
        };
    }
}
