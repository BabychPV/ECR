// tests/Ecr.Infrastructure.Tests/Security/DocumentTemplateApprovalStepAccessTests.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// B74 P2 (пункт 1), диференційний Deny на реальному шляху <see cref="AccessDecisionService.CanApproveAsync"/>: крок
/// маршруту документа шаблону B береться з маршруту ВЕРСІЇ ШАБЛОНУ B за період (шкала B), а не основного шаблону A.
/// </summary>
/// <remarks>
/// Проєкт із двома шаблонами (A — основний, B — приєднаний сирим SQL), у кожної версії за період свій маршрут з одним
/// кроком: роль A і роль B, обидві з грантом <c>Approve</c> на проєкт. Аркуш документа B поданий на крок маршруту B.
/// Дозволено носію ролі B; носію ролі A — відмова. Якби документ B брав версію A (колишній
/// <c>EcrDbContext.VersionOf</c> = основний шаблон), маршрут був би A, <c>StepAt</c> чужого кроку дав би перший крок A —
/// і рішення перевернулося б (A дозволено, B відмова). Червоний доказ — прогін на виразі до правки (AccessDecisionService
/// з 404e58a72), фільтри доступу не змінюються. Дані лише свої: свій проєкт, шаблони, версії, ролі, маршрути, користувачі.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentTemplateApprovalStepAccessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task Аркуш_документа_шаблону_B_погоджує_роль_маршруту_версії_B_а_роль_маршруту_A_відмова()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        var b = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        await builder.OpenPeriodAsync(a);
        var spareProjectId = b.ProjectId;
        var period = a.PeriodKey.Value;

        var currentB = await ScalarAsync($"""
            SET NOCOUNT ON;
            INSERT cfg.TemplateVersion (TemplateId, Version, Status, PublishedAt, PublishedByUserId, CreatedAt, CreatedByUserId)
            VALUES ({b.TemplateId}, N'9.8.{b.TemplateVersionId}', 1, '2026-01-01', 1, '2026-01-01', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);
        await ExecuteAsync($"""
            UPDATE doc.Document SET ProjectId = {a.ProjectId}, TemplateId = {b.TemplateId} WHERE Id = {b.DocumentId};
            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            SELECT {a.ProjectId}, {a.TemplateId}, {a.TemplateVersionId}
            WHERE NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate WHERE ProjectId = {a.ProjectId} AND TemplateId = {a.TemplateId});
            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({a.ProjectId}, {b.TemplateId}, {currentB});
            INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)
            VALUES ({a.ProjectId}, {a.TemplateId}, {period}, {a.TemplateVersionId}),
                   ({a.ProjectId}, {b.TemplateId}, {period}, {b.TemplateVersionId});
            """);

        try
        {
            int approverA, approverB, outsider;
            await using (var db = builder.CreateContext())
            {
                var roleA = new Role(EcrCode.Create($"B74A_{Guid.NewGuid():N}"[..20]), Text("Step A"));
                var roleB = new Role(EcrCode.Create($"B74B_{Guid.NewGuid():N}"[..20]), Text("Step B"));
                db.Roles.AddRange(roleA, roleB);
                var userA = NewUser("b74_a");
                var userB = NewUser("b74_b");
                var userN = NewUser("b74_n");
                var submitter = NewUser("b74_s");
                db.Users.AddRange(userA, userB, userN, submitter);
                await db.SaveChangesAsync(CancellationToken.None);

                db.ResourceGrants.Add(new ResourceGrant(roleA.Id, ResourceKind.Project, a.ProjectId, GrantLevel.Approve));
                db.ResourceGrants.Add(new ResourceGrant(roleB.Id, ResourceKind.Project, a.ProjectId, GrantLevel.Approve));
                db.RoleAssignments.Add(new RoleAssignment(roleA.Id, userA.Id, null));
                db.RoleAssignments.Add(new RoleAssignment(roleB.Id, userB.Id, null));

                var routeA = new ApprovalRoute(
                    EcrCode.Create($"B74RA_{Guid.NewGuid():N}"[..20]), Text("Route A"), a.ProjectId, a.TemplateVersionId);
                routeA.AddStep(roleA.Id);
                var routeB = new ApprovalRoute(
                    EcrCode.Create($"B74RB_{Guid.NewGuid():N}"[..20]), Text("Route B"), a.ProjectId, b.TemplateVersionId);
                routeB.AddStep(roleB.Id);
                db.ApprovalRoutes.AddRange(routeA, routeB);
                await db.SaveChangesAsync(CancellationToken.None);

                var state = new ApprovalState(b.DocumentId, b.SheetDefId, period);
                state.Submit(submitter.Id, Now, routeB.Steps[0].Id);
                db.ApprovalStates.Add(state);
                await db.SaveChangesAsync(CancellationToken.None);

                (approverA, approverB, outsider) = (userA.Id, userB.Id, userN.Id);
            }

            using var memory = new MemoryCache(new MemoryCacheOptions());
            await using var read = builder.CreateContext();
            var service = Service(read, memory);

            async Task<bool> CanApprove(int userId)
            {
                var profile = await service.BuildProfileAsync(userId, CancellationToken.None);
                return (await service.CanApproveAsync(profile, b.DocumentId, b.SheetDefId, a.PeriodKey, CancellationToken.None)).IsAllowed;
            }

            var step = await service.CurrentApprovalStepAsync(b.DocumentId, b.SheetDefId, a.PeriodKey, CancellationToken.None);
            Assert.NotNull(step);
            Assert.True(await CanApprove(approverB), "роль кроку маршруту версії B мусить погоджувати аркуш документа B");
            Assert.False(await CanApprove(approverA), "роль маршруту основного шаблону A не погоджує документ шаблону B");
            Assert.False(await CanApprove(outsider), "без ролі — відмова");
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = {a.ProjectId};
                DELETE FROM doc.ProjectTemplate WHERE ProjectId = {a.ProjectId} AND TemplateId = {b.TemplateId};
                UPDATE doc.Document SET ProjectId = {spareProjectId}, TemplateId = NULL WHERE Id = {b.DocumentId};
                """);
        }
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static User NewUser(string prefix)
    {
        var user = new User($"{prefix}_{Guid.NewGuid():N}"[..20], prefix, AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    private static AccessDecisionService Service(EcrDbContext db, MemoryCache memory)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(1);
        currentUser.GroupSids.Returns([]);
        return new AccessDecisionService(
            db, new MetadataCache(memory, db), new AccessProfileCache(memory), new TestClock(Now), currentUser,
            new WorkflowStore(db));
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
