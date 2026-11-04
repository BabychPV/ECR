// tests/Ecr.Infrastructure.Tests/Security/CanApproveManyEquivalenceTests.cs
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
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
/// AN-59: пакетне <see cref="AccessDecisionService.CanApproveManyAsync"/> і пакетний
/// <see cref="AccessDecisionService.BuildProfilesAsync"/> дають ТЕ САМЕ, що поштучні <c>CanApproveAsync</c> /
/// <c>BuildProfileAsync</c> — для змішаного набору (з грантом / без ролі / область іншого проєкту / учасники).
/// </summary>
[Collection("SqlServer")]
public sealed class CanApproveManyEquivalenceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static User NewUser(string prefix)
    {
        var user = new User($"{prefix}_{Guid.NewGuid():N}"[..20], prefix, AuthProvider.Local);
        user.SetPassword("not-a-real-hash");

        return user;
    }

    private static AccessDecisionService Service(Ecr.Infrastructure.Persistence.EcrDbContext db, MemoryCache memory)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(1);
        currentUser.GroupSids.Returns([]);

        return new AccessDecisionService(
            db, new MetadataCache(memory, db), new AccessProfileCache(memory), new TestClock(Now), currentUser,
            new WorkflowStore(db));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Пакетне_рішення_і_пакетні_профілі_збігаються_з_поштучними_на_змішаному_наборі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        await builder.OpenPeriodAsync(doc);

        var ids = new Dictionary<string, int>();
        var stepRoleId = 0;
        await using (var db = builder.CreateContext())
        {
            var stepRole = new Role(EcrCode.Create($"EQS_{Guid.NewGuid():N}"[..20]), Text("Step"));
            var otherRole = new Role(EcrCode.Create($"EQO_{Guid.NewGuid():N}"[..20]), Text("Other approver"));
            var managerRole = new Role(EcrCode.Create($"EQM_{Guid.NewGuid():N}"[..20]), Text("Manager"));
            db.Roles.AddRange(stepRole, otherRole, managerRole);
            await db.SaveChangesAsync(CancellationToken.None);
            stepRoleId = stepRole.Id;
            var users = new Dictionary<string, User>
            {
                ["granted"] = NewUser("eq_g"),
                ["otherProject"] = NewUser("eq_p"),
                ["wrongRole"] = NewUser("eq_w"),
                ["noRole"] = NewUser("eq_n"),
                ["participantWithRole"] = NewUser("eq_pr"),
                ["participantNoRole"] = NewUser("eq_pn"),
                ["delegator"] = NewUser("eq_d"),
                ["submitter"] = NewUser("eq_s"),
            };
            db.Users.AddRange(users.Values);
            await db.SaveChangesAsync(CancellationToken.None);
            foreach (var (name, user) in users)
            {
                ids[name] = user.Id;
            }

            db.ResourceGrants.Add(new ResourceGrant(stepRole.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Approve));
            db.ResourceGrants.Add(new ResourceGrant(otherRole.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Approve));
            db.ResourceGrants.Add(new ResourceGrant(managerRole.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Manage));
            db.RoleAssignments.Add(new RoleAssignment(stepRole.Id, ids["granted"], null));
            db.RoleAssignments.Add(new RoleAssignment(otherRole.Id, ids["wrongRole"], null));
            db.RoleAssignments.Add(new RoleAssignment(managerRole.Id, ids["delegator"], null));
            db.RoleAssignments.Add(new RoleAssignment(stepRole.Id, ids["participantWithRole"], null));
            var away = new RoleAssignment(stepRole.Id, ids["otherProject"], null);
            away.SetScope(RoleAssignmentScope.Create([doc.ProjectId + 900_000]));
            db.RoleAssignments.Add(away);

            var route = new ApprovalRoute(EcrCode.Create($"EQR_{Guid.NewGuid():N}"[..20]), Text("Route"), doc.ProjectId);
            route.AddStep(stepRole.Id);
            db.ApprovalRoutes.Add(route);

            var assignment = new EditAssignment(
                doc.DocumentId,
                EditAssignmentSlice.Create(doc.SheetCode, tableCode: null, monthNumber: null, periodKey: null),
                isExclusive: false,
                delegatedByUserId: ids["delegator"],
                handoffId: Guid.NewGuid(),
                createdByUserId: ids["delegator"],
                utcNow: Now,
                level: GrantLevel.Approve);
            assignment.AddRecipient(ids["participantWithRole"], ids["delegator"], Now);
            assignment.AddRecipient(ids["participantNoRole"], ids["delegator"], Now);
            new EditAssignmentStore(db).Add(assignment);
            await db.SaveChangesAsync(CancellationToken.None);

            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
            state.Submit(ids["submitter"], Now, route.Steps[0].Id);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Поштучний шлях: свій кеш і свій контекст.
        var single = new Dictionary<string, (AccessProfile Profile, EditDecision Decision)>();
        using (var memory = new MemoryCache(new MemoryCacheOptions()))
        {
            await using var db = builder.CreateContext();
            var service = Service(db, memory);
            foreach (var (name, id) in ids)
            {
                var profile = await service.BuildProfileAsync(id, CancellationToken.None);
                single[name] = (profile, await service.CanApproveAsync(
                    profile, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None));
            }
        }

        // Пакетний шлях: свіжий кеш — профілі будуються пакетом, а не беруться з поштучного.
        using var batchMemory = new MemoryCache(new MemoryCacheOptions());
        await using var batchDb = builder.CreateContext();
        var batch = Service(batchDb, batchMemory);
        var names = ids.Keys.ToList();
        var profiles = await batch.BuildProfilesAsync([.. names.Select(n => ids[n])], CancellationToken.None);
        var decisions = await batch.CanApproveManyAsync(
            [.. names.Select(n => profiles[ids[n]])], doc.DocumentId, doc.SheetDefId, doc.PeriodKey, null, null, CancellationToken.None);
        var hinted = await batch.CanApproveManyAsync(
            [.. names.Select(n => profiles[ids[n]])], doc.DocumentId, doc.SheetDefId, doc.PeriodKey, null,
            new KnownStepRole(stepRoleId), CancellationToken.None);

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var expected = single[name];
            Assert.Equal(expected.Decision.IsAllowed, decisions[i].IsAllowed);
            Assert.Equal(expected.Decision.Reason, decisions[i].Reason);
            Assert.Equal(expected.Decision.IsAllowed, hinted[i].IsAllowed);
            Assert.Equal(expected.Decision.Reason, hinted[i].Reason);

            // Профіль той самий за змістом (права, гранти, ролі, області, ознака учасника).
            var built = profiles[ids[name]];
            Assert.Equal(expected.Profile.CacheKey, built.CacheKey);
            Assert.Equal(expected.Profile.HasDocumentParticipations, built.HasDocumentParticipations);
            Assert.Equal(expected.Profile.RoleIds.Order(), built.RoleIds.Order());
            Assert.Equal(expected.Profile.Grants.OrderBy(g => g.Key), built.Grants.OrderBy(g => g.Key));
            Assert.Equal(expected.Profile.Permissions.Order(), built.Permissions.Order());
            Assert.Equal(expected.Profile.Scoped.Keys.Order(), built.Scoped.Keys.Order());
        }

        // Змістовність набору: дозволені рівно носій ролі кроку й учасник з роллю (і ніхто з решти).
        Assert.True(decisions[names.IndexOf("granted")].IsAllowed);
        Assert.True(decisions[names.IndexOf("participantWithRole")].IsAllowed);
        Assert.False(decisions[names.IndexOf("otherProject")].IsAllowed);
        Assert.False(decisions[names.IndexOf("wrongRole")].IsAllowed);
        Assert.False(decisions[names.IndexOf("noRole")].IsAllowed);
        Assert.False(decisions[names.IndexOf("participantNoRole")].IsAllowed);
    }
}
