using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// UI-33 / R-8: агрегат по ВСІХ аркушах віддається лише читачеві без інструментів, що ховають аркуші.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>SeesAllSheets</c> повертає <c>true</c> одразу — червоніють усі тести з
/// <c>false</c> у відповіді.
/// </remarks>
public sealed class SheetVisibilityTests
{
    private const int Project = AccessBuilder.ProjectId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "R-8")]
    public void Профіль_без_заборон_і_низьких_грантів_бачить_усе()
    {
        var profile = new AccessBuilder().Permission("Report.ViewCampaign").Build();

        Assert.True(SheetVisibility.SeesAllSheets(profile));
    }

    [Theory]
    [InlineData(ResourceKind.Project)]
    [InlineData(ResourceKind.Sheet)]
    [InlineData(ResourceKind.Table)]
    [InlineData(ResourceKind.Column)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "R-8")]
    public void Будь_яка_явна_заборона_ховає_число(ResourceKind kind)
    {
        var profile = new AccessBuilder().Deny(kind, 5).Build();

        Assert.False(SheetVisibility.SeesAllSheets(profile));
    }

    [Theory]
    [InlineData(ResourceKind.Sheet)]
    [InlineData(ResourceKind.Table)]
    [InlineData(ResourceKind.Column)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "R-8")]
    public void Грант_None_на_аркуш_таблицю_колонку_ховає_число_а_на_проєкт_ні(ResourceKind kind)
    {
        Assert.False(SheetVisibility.SeesAllSheets(new AccessBuilder().Grant(kind, 5, GrantLevel.None).Build()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "R-8")]
    public void Грант_None_на_проєкт_огляд_кампанії_не_зачіпає_бо_межі_проєктів_там_немає()
    {
        var profile = new AccessBuilder().Grant(ResourceKind.Project, 5, GrantLevel.None).Build();

        Assert.True(SheetVisibility.SeesAllSheets(profile));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "R-8")]
    public void Заборона_у_ролі_з_областю_або_у_звуженому_шарі_ховає_число()
    {
        var withDeny = Scoped(
            denies: ["Sheet:7"], narrowedDenies: [], narrowedSheets: null);
        var narrowed = Scoped(
            denies: [], narrowedDenies: ["Sheet:8"], narrowedSheets: ["F1"]);

        Assert.False(SheetVisibility.SeesAllSheets(withDeny));
        Assert.False(SheetVisibility.SeesAllSheets(narrowed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "R-8")]
    public void Календар_проєкту_потребує_повного_відкриття_проєкту()
    {
        var withGrant = new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Read).Build();
        var noGrant = new AccessBuilder().Build();

        Assert.True(SheetVisibility.SeesAllSheets(withGrant, Project));

        // Без гранта на цей проєкт (роль, звужена аркушами, відкриває документ іншим шляхом) — число не віддається.
        Assert.False(SheetVisibility.SeesAllSheets(noGrant, Project));
        Assert.False(SheetVisibility.SeesAllSheets(withGrant, Project + 1));
    }

    private static AccessProfile Scoped(string[] denies, string[] narrowedDenies, string[]? narrowedSheets)
    {
        var baseProfile = new AccessBuilder().Build();

        var layer = new NarrowedAccess(
            narrowedSheets is null ? null : new HashSet<string>(narrowedSheets),
            null, null,
            new Dictionary<string, GrantLevel>(),
            new HashSet<string>(narrowedDenies),
            new HashSet<int>(),
            new HashSet<string>());

        return new AccessProfile
        {
            CacheKey = baseProfile.CacheKey,
            UserId = baseProfile.UserId,
            SecurityStamp = baseProfile.SecurityStamp,
            Permissions = baseProfile.Permissions,
            Grants = baseProfile.Grants,
            Denies = baseProfile.Denies,
            RoleIds = baseProfile.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [Project] = new(
                    new Dictionary<string, GrantLevel>(),
                    new HashSet<string>(denies),
                    new HashSet<int>(),
                    new HashSet<string>())
                {
                    Narrowed = [layer],
                },
            },
        };
    }
}
