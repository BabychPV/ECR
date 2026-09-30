// tests/Ecr.Infrastructure.Tests/Security/GroupRoleGrantRevocationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
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
/// Грант ролі, яку людина має ЛИШЕ через групу AD: заміна грантів цієї ролі
/// діє на вже закешований профіль на НАСТУПНОМУ рішенні — без сплину TTL і
/// без ручного скидання кешу.
/// </summary>
/// <remarks>
/// ⛔ Штамп тут нічого не рятує: <c>RotateStampsForRoleAsync</c> крутить
/// його лише прямим носіям ролі, а членів групи система поіменно не знає.
/// Кеш тут один і той самий на всі кроки й ніхто не кличе
/// <c>InvalidateProfileAsync</c> — так само, як на іншому інстансі, де
/// адміністратор нічого не міняв.
/// </remarks>
[Collection("SqlServer")]
public sealed class GroupRoleGrantRevocationTests(SqlServerFixture sql) : IDisposable
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Знятий_із_групової_ролі_грант_зникає_на_наступному_рішенні_без_TTL()
    {
        var groupSid = $"S-1-5-21-{Random.Shared.Next(1, int.MaxValue)}-778-9003";
        var projectA = Random.Shared.Next(1_000_000, int.MaxValue / 2);
        var projectB = projectA + 1;
        int subjectId, roleId;

        await using (var setup = sql.CreateContext())
        {
            var subject = User.CreateDomain(
                $"grg.{Guid.NewGuid():N}"[..20], "Group grant member", $"S-1-5-21-{Guid.NewGuid():N}"[..40], DateTime.UtcNow);
            var role = new Role(EcrCode.Create($"GG{Guid.NewGuid():N}"[..12]), Text("Group grant role"));
            setup.Users.Add(subject);
            setup.Roles.Add(role);
            await setup.SaveChangesAsync(CancellationToken.None);

            (subjectId, roleId) = (subject.Id, role.Id);

            // Роль — лише на групу; прямого призначення людині немає.
            var assignment = new RoleAssignment(roleId, userId: null, principalSid: groupSid);
            assignment.SetValidity(null, new DateOnly(2099, 1, 1));
            setup.RoleAssignments.Add(assignment);
            setup.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Project, projectA, GrantLevel.Write, isDeny: false));
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        var session = Substitute.For<ICurrentUser>();
        session.UserId.Returns(subjectId);
        session.GroupSids.Returns([groupSid]);

        await using (var db = sql.CreateContext())
        {
            var before = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.Equal(GrantLevel.Write, before.LevelFor(ResourceKind.Project, projectA));
        }

        // Крок 1: заміна тією самою КІЛЬКІСТЮ грантів — інший ресурс. Рухається
        // лише найбільший Id; кількість та сама.
        await ReplaceGrantsAsync(roleId, [new ResourceGrantDto(ResourceKind.Project, projectB, GrantLevel.Read, IsDeny: false)]);

        await using (var db = sql.CreateContext())
        {
            var afterSwap = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.Equal(
                (GrantLevel.None, GrantLevel.Read),
                (afterSwap.LevelFor(ResourceKind.Project, projectA), afterSwap.LevelFor(ResourceKind.Project, projectB)));
        }

        // Крок 2: зняти все без вставки. Найбільший Id зникає разом із рядком,
        // рухається кількість.
        await ReplaceGrantsAsync(roleId, []);

        await using (var db = sql.CreateContext())
        {
            var afterRevoke = await Service(db, session).BuildProfileAsync(subjectId, CancellationToken.None);
            Assert.Equal(GrantLevel.None, afterRevoke.LevelFor(ResourceKind.Project, projectB));
        }
    }

    /// <summary>Заміна грантів шляхом сховища, яким ходить <c>ReplaceGrantsHandler</c>.</summary>
    private async Task ReplaceGrantsAsync(int roleId, IReadOnlyList<ResourceGrantDto> grants)
    {
        await using var db = sql.CreateContext();
        var store = new UserStore(db);
        await store.ReplaceGrantsAsync(roleId, grants, CancellationToken.None);

        // Прямих носіїв немає — ротувати нема кого, і саме в цьому дефект.
        Assert.Equal(0, await store.RotateStampsForRoleAsync(roleId, CancellationToken.None));
        await db.SaveChangesAsync(CancellationToken.None);
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
