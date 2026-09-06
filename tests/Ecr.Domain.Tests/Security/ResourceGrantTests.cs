// tests/Ecr.Domain.Tests/Security/ResourceGrantTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Security;

/// <summary>
/// `IsDeny` **виграє завжди**, на будь-якому рівні успадкування (ФВ-6.6).
/// </summary>
/// <remarks>
/// Це свідома жорсткість. Альтернатива «конкретніший рівень перемагає» дає
/// ситуації, де людина має доступ і ніхто не може пояснити чому — а пояснити
/// доступ важливіше, ніж зробити його гнучким.
/// </remarks>
public sealed class ResourceGrantTests
{
    private static ResourceGrant Grant(ResourceKind kind, int id, GrantLevel level, bool deny = false)
    {
        var grant = new ResourceGrant(roleId: 1, kind, id, level);
        if (deny)
        {
            typeof(ResourceGrant).GetProperty(nameof(ResourceGrant.IsDeny))!.SetValue(grant, true);
        }

        return grant;
    }

    /// <summary>Плоский профіль із набору грантів — так само, як його будує служба.</summary>
    private static AccessProfile Profile(params ResourceGrant[] grants)
    {
        var allowed = new Dictionary<string, GrantLevel>(StringComparer.Ordinal);
        var denied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var grant in grants)
        {
            var key = $"{grant.ResourceKind}:{grant.ResourceId}";
            if (grant.IsDeny)
            {
                denied.Add(key);
            }
            else
            {
                allowed[key] = grant.Level;
            }
        }

        return new AccessProfile
        {
            CacheKey = "k",
            UserId = 1,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(),
            Grants = allowed,
            Denies = denied,
            RoleIds = new HashSet<int>(),
        };
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Deny_на_проєкті_перекриває_Manage_на_аркуші()
    {
        var profile = Profile(
            Grant(ResourceKind.Sheet, AccessBuilder.SheetId, GrantLevel.Manage),
            Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.None, deny: true));

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, AccessBuilder.Cell()));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Deny_на_колонці_перекриває_Write_на_таблиці()
    {
        var profile = Profile(
            Grant(ResourceKind.Table, AccessBuilder.TableId, GrantLevel.Write),
            Grant(ResourceKind.Column, AccessBuilder.ColumnId, GrantLevel.None, deny: true));

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, AccessBuilder.Cell()));

        // …і сусідня колонка при цьому лишається доступною: заборона точкова.
        Assert.Equal(GrantLevel.Write, EditRules.Effective(profile, AccessBuilder.Cell(columnId: 41)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.13")]
    public void Рівні_упорядковані_від_None_до_Manage()
    {
        // Порядок значень значущий: перевірки пишуться як `>= Write`, і
        // перестановка будь-яких двох тихо змінила б зміст усіх правил.
        GrantLevel[] expected =
        [
            GrantLevel.None, GrantLevel.Read, GrantLevel.Write,
            GrantLevel.Submit, GrantLevel.Approve, GrantLevel.Manage,
        ];

        Assert.Equal(expected, expected.OrderBy(level => (byte)level));
        Assert.True(GrantLevel.Manage > GrantLevel.Approve);
        Assert.True(GrantLevel.Submit > GrantLevel.Write);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Грант_успадковується_від_проєкту_до_колонки()
    {
        var profile = Profile(Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write));

        // Оголошено лише на проєкті — діє на колонку: інакше кожну колонку
        // довелося б перелічувати руками.
        Assert.Equal(GrantLevel.Write, EditRules.Effective(profile, AccessBuilder.Cell()));
        Assert.Equal(GrantLevel.Write, EditRules.Effective(profile, AccessBuilder.Cell(columnId: 999)));
    }
}
