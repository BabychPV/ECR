// tests/Ecr.Infrastructure.Tests/Security/InvalidateProfileOtherUserTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// <c>InvalidateProfileAsync</c> (шлях <c>ProjectOwnershipGrant</c>) мусить скидати запис користувача
/// з ГРУПОВИМ відбитком, навіть коли викликає його інша сесія (ent6 A1).
/// </summary>
[Collection("SqlServer")]
public sealed class InvalidateProfileOtherUserTests(SqlServerFixture sql) : IDisposable
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Скидання_профілю_іншою_сесією_прибирає_запис_з_груповим_відбитком_і_новий_грант_видно_одразу()
    {
        const string groupSid = "S-1-5-21-000111-222333-9555";
        var project = Random.Shared.Next(1_000_000, int.MaxValue / 2);
        int subjectId, adminId, roleId;

        await using (var setup = sql.CreateContext())
        {
            var subject = User.CreateDomain($"inv.{Guid.NewGuid():N}"[..20], "Owner", $"S-1-5-21-{Guid.NewGuid():N}"[..40], DateTime.UtcNow);
            var admin = User.CreateDomain($"adm.{Guid.NewGuid():N}"[..20], "Admin", $"S-1-5-21-{Guid.NewGuid():N}"[..40], DateTime.UtcNow);
            var role = new Role(EcrCode.Create($"IV{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Owner role" }));
            setup.Users.Add(subject);
            setup.Users.Add(admin);
            setup.Roles.Add(role);
            await setup.SaveChangesAsync(CancellationToken.None);
            (subjectId, adminId, roleId) = (subject.Id, admin.Id, role.Id);

            // ПРЯМА роль власника: її гранти відбиток груп не бачить.
            setup.RoleAssignments.Add(new RoleAssignment(roleId, subjectId, null));
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        var own = Substitute.For<ICurrentUser>();
        own.UserId.Returns(subjectId);
        own.GroupSids.Returns([groupSid]);
        var other = Substitute.For<ICurrentUser>();
        other.UserId.Returns(adminId);
        other.GroupSids.Returns([]);

        var cache = new AccessProfileCache(_memory);

        // Профіль власної сесії (з групами) прогрітий у кеші ДО гранту.
        await using (var db = sql.CreateContext())
        {
            var before = await Service(db, own, cache).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.Equal(GrantLevel.None, before.LevelFor(ResourceKind.Project, project));
        }

        // Виданий грант власника (як ProjectOwnershipGrant) — відбиток груп не міняється.
        await using (var grant = sql.CreateContext())
        {
            grant.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Project, project, GrantLevel.Manage));
            await grant.SaveChangesAsync(CancellationToken.None);
        }

        // Скидає ІНША сесія: цільовий ключ за відбитком ЇЇ груп був би порожнім і промахнувся б.
        await using (var db = sql.CreateContext())
        {
            await Service(db, other, cache).InvalidateProfileAsync(subjectId, CancellationToken.None);
        }

        await using (var db = sql.CreateContext())
        {
            var after = await Service(db, own, cache).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.Equal(GrantLevel.Manage, after.LevelFor(ResourceKind.Project, project));
        }
    }

    private static AccessDecisionService Service(Ecr.Infrastructure.Persistence.EcrDbContext db, ICurrentUser user, AccessProfileCache cache)
        => new(db, Substitute.For<IMetadataCache>(), cache, new TestClock(DateTime.UtcNow), user, Substitute.For<IWorkflowStore>());
}
