// tests/Ecr.Infrastructure.Tests/Security/PermissionHoldersStoreTests.cs
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// «Хто ще може керувати користувачами» на справжній базі — основа заборони
/// блокувати останнього адміністратора (директива №15, BE-12).
/// </summary>
/// <remarks>
/// ⚠ База спільна для набору, тож абсолютне число не стабільне: кожен запис
/// перевіряється різницею «усі» мінус «усі, крім нього».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermissionHoldersStoreTests(SqlServerFixture sql)
{
    private const string Permission = "Security.ManageUsers";
    private static readonly DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-12")]
    public async Task Рахуються_лише_активні_незаблоковані_носії_чинного_особистого_призначення()
    {
        await using var db = Context();

        var role = new Role(EcrCode.Create($"PH_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission(role.Id, Permission));

        var holder = Add(db, "hld");
        var locked = Add(db, "lck");
        var expired = Add(db, "exp");
        var none = Add(db, "non");
        locked.LockByAdministrator();
        await db.SaveChangesAsync();

        db.RoleAssignments.Add(new RoleAssignment(role.Id, holder.Id, principalSid: null));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, locked.Id, principalSid: null));
        var past = new RoleAssignment(role.Id, expired.Id, principalSid: null);
        past.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1));
        db.RoleAssignments.Add(past);
        await db.SaveChangesAsync();

        var store = new UserStore(db);
        var all = await store.CountActivePermissionHoldersAsync(Permission, null, Now, CancellationToken.None);

        async Task<int> Contribution(User user)
            => all - await store.CountActivePermissionHoldersAsync(Permission, user.Id, Now, CancellationToken.None);

        Assert.Equal(1, await Contribution(holder));
        Assert.Equal(0, await Contribution(locked));
        Assert.Equal(0, await Contribution(expired));
        Assert.Equal(0, await Contribution(none));
    }

    private User Add(EcrDbContext db, string prefix)
    {
        var user = new User($"{prefix}_{_tag}", prefix, AuthProvider.Local);
        user.SetPassword("hash");
        db.Users.Add(user);
        return user;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
