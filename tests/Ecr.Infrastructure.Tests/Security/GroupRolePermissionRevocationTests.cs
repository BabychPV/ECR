// tests/Ecr.Infrastructure.Tests/Security/GroupRolePermissionRevocationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// (v) 08.11: права власної ролі тепер правляться. Людина, що має роль ЛИШЕ через групу AD, бачить зміну набору прав
/// на НАСТУПНОМУ рішенні — без сплину TTL профілю і без ручного скидання кешу.
/// </summary>
/// <remarks>
/// ⛔ Штамп тут не рятує: <c>RotateStampsForRoleAsync</c> крутить його лише прямим носіям. Заміна тієї самої
/// КІЛЬКОСТІ прав (одне на інше) не зрушує ні призначень, ні грантів — ревізію рухає лише відбиток набору прав.
/// МУТАЦІЯ: прибрати відбиток прав із <c>AccessDecisionService.GroupsFingerprintAsync</c> — червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class GroupRolePermissionRevocationTests(SqlServerFixture sql) : IDisposable
{
    private const string Before = "Security.ManageRoles";
    private const string After = "Security.ManageUsers";

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Замінене_право_групової_ролі_діє_на_наступному_рішенні_без_TTL()
    {
        var groupSid = $"S-1-5-21-{Random.Shared.Next(1, int.MaxValue)}-779-9004";
        int subjectId, roleId;

        await using (var setup = sql.CreateContext())
        {
            var subject = User.CreateDomain(
                $"grp.{Guid.NewGuid():N}"[..20], "Group permission member", $"S-1-5-21-{Guid.NewGuid():N}"[..40], DateTime.UtcNow);
            var role = new Role(EcrCode.Create($"GP{Guid.NewGuid():N}"[..12]), Text("Group permission role"));
            setup.Users.Add(subject);
            setup.Roles.Add(role);
            await setup.SaveChangesAsync(CancellationToken.None);

            (subjectId, roleId) = (subject.Id, role.Id);

            setup.RoleAssignments.Add(new RoleAssignment(roleId, userId: null, principalSid: groupSid));
            setup.RolePermissions.Add(new RolePermission(roleId, Before));
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        var session = Substitute.For<ICurrentUser>();
        session.UserId.Returns(subjectId);
        session.GroupSids.Returns([groupSid]);

        await using (var db = sql.CreateContext())
        {
            var before = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.True(PermissionCheck.IsGranted(before, Before));
            Assert.False(PermissionCheck.IsGranted(before, After));
        }

        await using (var db = sql.CreateContext())
        {
            var store = new UserStore(db);
            await store.ReplaceRolePermissionsAsync(roleId, [After], CancellationToken.None);
            Assert.Equal(0, await store.RotateStampsForRoleAsync(roleId, CancellationToken.None));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = sql.CreateContext())
        {
            var after = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.False(PermissionCheck.IsGranted(after, Before), "зняте право групової ролі лишилося в закешованому профілі");
            Assert.True(PermissionCheck.IsGranted(after, After), "додане право групової ролі не з'явилося");
        }
    }

    private AccessDecisionService Service(EcrDbContext db, ICurrentUser currentUser)
        => new(
            db,
            Substitute.For<IMetadataCache>(),
            new AccessProfileCache(_memory),
            new TestClock(DateTime.UtcNow),
            currentUser,
            Substitute.For<IWorkflowStore>());

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
