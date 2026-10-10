// tests/Ecr.Infrastructure.Tests/Security/BootstrapDomainAdminStoreTests.cs
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
/// «Чи з'явився доменний адміністратор» на справжній базі — умова вимкнення
/// bootstrap-запису (ФВ-6.18, D-97, S1-01 аудиту 5).
/// </summary>
/// <remarks>
/// ⚠ Відповідь глобальна («чи є хоч хтось»), а база спільна для набору. Тому
/// тест бере ВЛАСНЕ право-зонд і живе в транзакції, яку не комітить: чужі
/// записи цього права не мають, а каталог прав після тесту лишається як був.
/// </remarks>
[Collection("SqlServer")]
public sealed class BootstrapDomainAdminStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    private int _sidCounter;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.18")]
    [Trait("Finding", "S1-01")]
    public async Task Адміністратором_рахується_лише_той_хто_входив_і_має_чинне_безобласне_призначення_активної_ролі()
    {
        await using var db = Context();
        await using var tx = await db.Database.BeginTransactionAsync();

        var permission = $"Probe.BootstrapAdmin{_tag}";
        db.Permissions.Add(new Permission(
            permission, "Security", new LocalizedText(new Dictionary<string, string> { ["en"] = "Probe" }), isDangerous: true));

        var role = NewRole("BA");
        var deadRole = NewRole("BD");
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        db.RolePermissions.Add(new RolePermission(deadRole.Id, permission));
        db.Entry(deadRole).Property(nameof(Role.IsActive)).CurrentValue = false;
        await db.SaveChangesAsync();

        var store = new UserStore(db);

        // Кожен «майже адміністратор» додається по одному, і після кожного
        // відповідь лишається «ні»: інакше один зайвий збіг сховав би інші.
        async Task AssertNoAdmin(string because)
            => Assert.False(
                await store.HasActiveDomainAdminAsync(permission, Now, CancellationToken.None),
                because);

        await AssertNoAdmin("ще нікого немає");

        // ⛔ Головний випадок S1-01: запис створено вручну (`POST /users`), SID не
        // підтверджено жодним входом — можливо, з друкарською помилкою.
        Assign(role, await Domain("nsi", signedIn: false));
        await db.SaveChangesAsync();
        await AssertNoAdmin("запис не входив через Windows — SID нічим не підтверджено");

        var future = Assign(role, await Domain("fut", signedIn: true));
        future.SetValidity(DateOnly.FromDateTime(Now).AddDays(1), null);
        await db.SaveChangesAsync();
        await AssertNoAdmin("призначення діє лише з завтра");

        var expired = Assign(role, await Domain("exp", signedIn: true));
        expired.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1));
        await db.SaveChangesAsync();
        await AssertNoAdmin("призначення прострочене");

        var scoped = Assign(role, await Domain("scp", signedIn: true));
        scoped.SetScope(RoleAssignmentScope.Create([1]));
        await db.SaveChangesAsync();
        await AssertNoAdmin("роль з областю не дає глобального Security.*");

        var locked = await Domain("lck", signedIn: true);
        locked.LockByAdministrator();
        Assign(role, locked);
        await db.SaveChangesAsync();
        await AssertNoAdmin("запис заблоковано адміністратором");

        Assign(deadRole, await Domain("ina", signedIn: true));
        await db.SaveChangesAsync();
        await AssertNoAdmin("роль вимкнена");

        var local = new User($"loc_{_tag}", "loc", AuthProvider.Local);
        local.SetPassword("hash");
        local.RegisterSuccessfulLogin(Now.AddDays(-1));
        db.Users.Add(local);
        await db.SaveChangesAsync();
        Assign(role, local);
        await db.SaveChangesAsync();
        await AssertNoAdmin("локальний запис доменним адміністратором не є");

        // А справжній доменний адміністратор — рахується.
        Assign(role, await Domain("adm", signedIn: true));
        await db.SaveChangesAsync();
        Assert.True(await store.HasActiveDomainAdminAsync(permission, Now, CancellationToken.None));

        Role NewRole(string prefix)
        {
            var created = new Role(
                EcrCode.Create($"{prefix}_{_tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
            db.Roles.Add(created);
            return created;
        }

        async Task<User> Domain(string prefix, bool signedIn)
        {
            var user = User.CreateDomain(
                $"CORP\\{prefix}_{_tag}",
                prefix,
                $"S-1-5-21-{Convert.ToUInt32(_tag, 16)}-{++_sidCounter}",
                Now.AddDays(-2));
            if (signedIn)
            {
                user.RegisterSuccessfulLogin(Now.AddDays(-1));
            }

            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        RoleAssignment Assign(Role target, User user)
        {
            var assignment = new RoleAssignment(target.Id, user.Id, principalSid: null);
            db.RoleAssignments.Add(assignment);
            return assignment;
        }
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
