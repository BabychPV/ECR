// tests/Ecr.Infrastructure.Tests/Security/SheetPeriodRightsEquivalenceTests.cs
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// D-6 (PERF): пакетне <see cref="AccessDecisionService.SheetPeriodRightsAsync"/> (умови всіх місяців двома запитами,
/// маршрут раз на версію шаблону) дає ТЕ САМЕ, що поштучні <c>CanApproveAsync</c> / <c>HasSubmitGrantAsync</c> /
/// <c>CanReopenAsync</c> — на змішаному наборі місяців (крок 1, крок 2, затверджений, чернетка, закритий місяць із
/// винятком документа і без) і профілів (роль кроку A, роль кроку B, автор подання, читач, без гранта).
/// </summary>
/// <remarks>
/// Мутація (запущена, звіт D-6): у пакеті брати роль першого кроку для всіх місяців (а не `StepAt(CurrentStepId)`) —
/// тест червоний для погоджувачів A і B.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetPeriodRightsEquivalenceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 6, 20, 10, 0, 0, DateTimeKind.Utc);

    private const int Jan = 202601;
    private const int Feb = 202602;
    private const int Mar = 202603;
    private const int Apr = 202604;
    private const int May = 202605;
    private const int Jun = 202606;

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessDecisionService Service(Ecr.Infrastructure.Persistence.EcrDbContext db, MemoryCache memory)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(1);
        currentUser.GroupSids.Returns([]);

        return new AccessDecisionService(
            db, new MetadataCache(memory, db), new AccessProfileCache(memory), new TestClock(Now), currentUser,
            new Ecr.Infrastructure.Persistence.WorkflowStore(db));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-559")]
    public async Task Пакет_місяців_збігається_з_поштучними_рішеннями()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: Jun, ct: CancellationToken.None);

        var ids = new Dictionary<string, int>();
        await using (var db = builder.CreateContext())
        {
            // Місяці січень–травень поруч із червнем документа; лютий і березень закриті, березень відкрито для документа.
            foreach (var (key, state) in new[]
                     {
                         (Jan, PeriodState.Open), (Feb, PeriodState.Closed), (Mar, PeriodState.Closed), (Apr, PeriodState.Open),
                         (May, PeriodState.Open),
                     })
            {
                var month = (byte)(key % 100);
                var period = new Period(
                    doc.ProjectId, new PeriodKey(key), month, new DateOnly(2026, month, 1),
                    new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));
                period.AdvanceTo(PeriodState.Open, Now);
                if (state == PeriodState.Closed)
                {
                    period.AdvanceTo(PeriodState.Grace, Now);
                    period.AdvanceTo(PeriodState.Closed, Now);
                }

                db.Periods.Add(period);
            }

            var roleA = new Role(EcrCode.Create($"SPA_{Guid.NewGuid():N}"[..20]), Text("Step A"));
            var roleB = new Role(EcrCode.Create($"SPB_{Guid.NewGuid():N}"[..20]), Text("Step B"));
            var submitRole = new Role(EcrCode.Create($"SPS_{Guid.NewGuid():N}"[..20]), Text("Submitter"));
            var readRole = new Role(EcrCode.Create($"SPR_{Guid.NewGuid():N}"[..20]), Text("Reader"));
            db.Roles.AddRange(roleA, roleB, submitRole, readRole);

            var users = new Dictionary<string, User>
            {
                ["approverA"] = NewUser("sp_a"),
                ["approverB"] = NewUser("sp_b"),
                ["submitter"] = NewUser("sp_s"),
                ["reader"] = NewUser("sp_r"),
                ["nobody"] = NewUser("sp_n"),
            };
            db.Users.AddRange(users.Values);
            await db.SaveChangesAsync(CancellationToken.None);
            foreach (var (name, user) in users)
            {
                ids[name] = user.Id;
            }

            db.ResourceGrants.Add(new ResourceGrant(roleA.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Approve));
            db.ResourceGrants.Add(new ResourceGrant(roleB.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Approve));
            db.ResourceGrants.Add(new ResourceGrant(submitRole.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
            db.ResourceGrants.Add(new ResourceGrant(readRole.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(roleA.Id, ids["approverA"], null));
            db.RoleAssignments.Add(new RoleAssignment(roleB.Id, ids["approverB"], null));
            db.RoleAssignments.Add(new RoleAssignment(submitRole.Id, ids["submitter"], null));
            db.RoleAssignments.Add(new RoleAssignment(readRole.Id, ids["reader"], null));

            var route = new ApprovalRoute(EcrCode.Create($"SPR_{Guid.NewGuid():N}"[..20]), Text("Route"), doc.ProjectId);
            route.AddStep(roleA.Id);
            route.AddStep(roleB.Id);
            db.ApprovalRoutes.Add(route);
            await db.SaveChangesAsync(CancellationToken.None);

            var stepA = route.Steps[0].Id;
            var stepB = route.Steps[1].Id;
            var submitter = ids["submitter"];

            // Січень — крок A; лютий (закритий) — крок B; березень (закритий, відкритий для документа) — затверджений;
            // квітень — затверджений; травень — крок A; червень — чернетка (рядка стану немає).
            ApprovalState Submitted(int key, int? step)
            {
                var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, key);
                state.Submit(submitter, Now, stepA);
                if (step == stepB)
                {
                    state.ApproveStep(ids["approverA"], Now, stepB);
                }

                return state;
            }

            var mar = Submitted(Mar, stepB);
            mar.Approve(ids["approverB"], Now);
            var apr = Submitted(Apr, stepB);
            apr.Approve(ids["approverB"], Now);
            db.ApprovalStates.AddRange(Submitted(Jan, stepA), Submitted(Feb, stepB), mar, apr, Submitted(May, stepA));
            db.DocumentPeriodReopens.Add(DocumentPeriodReopen.Open(doc.DocumentId, Mar, Now.AddDays(7), "late fix", ids["approverB"], Now));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        PeriodKey[] keys = [new(Jan), new(Feb), new(Mar), new(Apr), new(May), new(Jun)];

        foreach (var (name, id) in ids)
        {
            // Поштучний шлях: свій кеш і свій контекст.
            var single = (Approve: new HashSet<int>(), Submit: new HashSet<int>(), Reopen: new HashSet<int>());
            using (var memory = new MemoryCache(new MemoryCacheOptions()))
            {
                await using var db = builder.CreateContext();
                var service = Service(db, memory);
                var profile = await service.BuildProfileAsync(id, CancellationToken.None);
                foreach (var key in keys)
                {
                    if ((await service.CanApproveAsync(profile, doc.DocumentId, doc.SheetDefId, key, CancellationToken.None)).IsAllowed)
                    {
                        single.Approve.Add(key.Value);
                    }

                    if (await service.HasSubmitGrantAsync(profile, doc.DocumentId, doc.SheetDefId, key, null, CancellationToken.None))
                    {
                        single.Submit.Add(key.Value);
                    }

                    if ((await service.CanReopenAsync(profile, doc.DocumentId, doc.SheetDefId, key, CancellationToken.None)).IsAllowed)
                    {
                        single.Reopen.Add(key.Value);
                    }
                }
            }

            // Пакетний шлях: свіжий кеш.
            using var batchMemory = new MemoryCache(new MemoryCacheOptions());
            await using var batchDb = builder.CreateContext();
            var batch = Service(batchDb, batchMemory);
            var batchProfile = await batch.BuildProfileAsync(id, CancellationToken.None);
            var rights = await batch.SheetPeriodRightsAsync(
                batchProfile, doc.DocumentId, doc.SheetDefId, null, keys, keys, keys, CancellationToken.None);

            Assert.True(single.Approve.SetEquals(rights.Approve), $"{name}: approve {Show(single.Approve)} ≠ {Show(rights.Approve)}");
            Assert.True(single.Submit.SetEquals(rights.SubmitGrant), $"{name}: submit {Show(single.Submit)} ≠ {Show(rights.SubmitGrant)}");
            Assert.True(single.Reopen.SetEquals(rights.Reopen), $"{name}: reopen {Show(single.Reopen)} ≠ {Show(rights.Reopen)}");

            // Змістовність набору: права справді різні по місяцях.
            switch (name)
            {
                case "approverA":
                    Assert.Equal([Jan, May], rights.Approve.Order());
                    break;
                case "approverB":
                    Assert.Equal([Feb], rights.Approve.Order());
                    Assert.Contains(Apr, rights.Reopen);
                    break;
                case "submitter":
                    Assert.Empty(rights.Approve);
                    Assert.Equal(keys.Length, rights.SubmitGrant.Count);
                    break;
                case "nobody":
                    Assert.Empty(rights.Approve);
                    Assert.Empty(rights.SubmitGrant);
                    Assert.Empty(rights.Reopen);
                    break;
            }
        }
    }

    private static string Show(IEnumerable<int> keys) => $"[{string.Join(",", keys.Order())}]";

    private static User NewUser(string prefix)
    {
        var user = new User($"{prefix}_{Guid.NewGuid():N}"[..20], prefix, AuthProvider.Local);
        user.SetPassword("not-a-real-hash");

        return user;
    }
}
