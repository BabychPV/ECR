// tests/Ecr.Infrastructure.Tests/Security/RoleLifecycleStoreTests.cs
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Сховище ролей на справжній базі: лічильник залежних, перейменування й
/// видалення разом із набором прав (директива №15, BE-14).
/// </summary>
/// <remarks>
/// ⚠ Обробники перевірені на фікстурі в пам'яті, а вона не знає про
/// <c>FK_RolePerm_Role</c>: видалення ролі, що лишає рядки прав, у пам'яті
/// зелене, а в базі падає. Тому цей шлях пройдено тут.
/// </remarks>
[Collection("SqlServer")]
public sealed class RoleLifecycleStoreTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_рахує_залежних_перейменовується_і_видаляється_разом_із_правами()
    {
        int roleId;

        await using (var db = Context())
        {
            // Права беруться з того, що є в базі: сід тут не гарантований, а
            // вигаданий код упав би на `FK_RolePerm_Perm`.
            var permissions = await db.Permissions.OrderBy(p => p.Id).Select(p => p.Id).Take(2).ToListAsync();

            roleId = await new UserStore(db).AddRoleAsync(
                new Role(EcrCode.Create($"LIFE_{_tag}"), Text("Life")), permissions, CancellationToken.None);

            db.RoleAssignments.Add(new RoleAssignment(roleId, userId: null, principalSid: $"S-1-5-21-{_tag}"));
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var store = new UserStore(db);

            var usage = await store.CountRoleUsageAsync(roleId, CancellationToken.None);
            Assert.Equal(1, usage.Assignments);
            Assert.Equal(0, usage.Grants);

            await store.RenameRoleAsync(roleId, $"LIFE2_{_tag}", name: null, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var role = await db.Roles.SingleAsync(r => r.Id == roleId);
            Assert.Equal($"LIFE2_{_tag}", role.Code);
            Assert.Equal("Life", role.NameL10n.Get("en"));

            db.RoleAssignments.RemoveRange(db.RoleAssignments.Where(a => a.RoleId == roleId));
            await db.SaveChangesAsync();

            await new UserStore(db).RemoveRoleAsync(roleId, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = Context();
        Assert.False(await check.Roles.AnyAsync(r => r.Id == roleId));
        Assert.False(await check.RolePermissions.AnyAsync(p => p.RoleId == roleId));
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
