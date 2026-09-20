// tests/Ecr.Infrastructure.Tests/Security/GroupRoleRevocationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
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
/// Групове призначення на справжній базі: сховище пише й читає його, а
/// відкликання діє на вже залогіненого члена групи БЕЗ перевходу.
/// </summary>
[Collection("SqlServer")]
public sealed class GroupRoleRevocationTests(SqlServerFixture sql) : IDisposable
{
    private const string GrantedPermission = "Template.View";

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Відкликане_в_групи_право_зникає_на_наступному_рішенні_без_перевходу()
    {
        var groupSid = $"S-1-5-21-{Random.Shared.Next(1, int.MaxValue)}-777-9002";
        int subjectId, roleId, assignmentId;

        await using (var setup = sql.CreateContext())
        {
            var subject = User.CreateDomain(
                $"grp.{Guid.NewGuid():N}"[..20], "Group member", $"S-1-5-21-{Guid.NewGuid():N}"[..40], DateTime.UtcNow);
            var role = new Role(EcrCode.Create($"G{Guid.NewGuid():N}"[..12]), Text("Group revocation role"));
            setup.Users.Add(subject);
            setup.Roles.Add(role);
            await setup.SaveChangesAsync(CancellationToken.None);

            setup.RolePermissions.Add(new RolePermission(role.Id, GrantedPermission));
            await setup.SaveChangesAsync(CancellationToken.None);

            (subjectId, roleId) = (subject.Id, role.Id);
        }

        // Призначення — шляхом сховища, яким ходить обробник.
        await using (var db = sql.CreateContext())
        {
            var store = new UserStore(db);
            var assignment = new RoleAssignment(roleId, userId: null, principalSid: groupSid);
            assignment.SetValidity(null, new DateOnly(2099, 1, 1));
            store.AddGroupAssignment(assignment);
            await db.SaveChangesAsync(CancellationToken.None);
            assignmentId = assignment.Id;

            var listed = (await store.ListGroupRoleAssignmentsAsync(CancellationToken.None))
                .Single(a => a.Id == assignmentId);
            Assert.Equal((roleId, groupSid, new DateOnly(2099, 1, 1)), (listed.RoleId, listed.PrincipalSid, listed.ValidTo!.Value));
        }

        // Сесія члена групи: SID уже в квитку, профіль ліг у кеш із правом.
        var session = Substitute.For<ICurrentUser>();
        session.UserId.Returns(subjectId);
        session.GroupSids.Returns([groupSid]);

        await using (var db = sql.CreateContext())
        {
            var before = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.Contains(GrantedPermission, before.Permissions);
        }

        await using (var db = sql.CreateContext())
        {
            var store = new UserStore(db);
            var found = await store.FindGroupAssignmentAsync(assignmentId, CancellationToken.None);
            Assert.NotNull(found);
            store.RemoveGroupAssignment(found);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // ⛔ Той самий кеш, той самий штамп, той самий квиток — і права вже немає.
        await using (var db = sql.CreateContext())
        {
            var after = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.DoesNotContain(GrantedPermission, after.Permissions);
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
