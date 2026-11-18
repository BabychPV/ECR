// tests/Ecr.Infrastructure.Tests/Security/TemplateApprovalRouteAccessTests.cs
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// ✎ B155 (слово людини 16.11): крок маршруту ШАБЛОНУ затверджує й повертає в роботу роль кроку АБО вбудований
/// загальний <c>Approver</c> — на реальному шляху <see cref="AccessDecisionService"/> (затвердження, повернення
/// поданого й повернення ЗАТВЕРДЖЕНОГО — останній крок, D2). Маршрут проєкту — без змін: лише роль кроку.
/// </summary>
/// <remarks>
/// Свої дані: документ (період 2099xx), ролі C1/C2 (кроки), G (грант Approve на проєкт, без кроку), користувачі;
/// вбудований <c>Approver</c> — із сіду, грантів йому тест не дає (грант іде через G). Прибирання — у finally.
/// ⛔ МУТАЦІЇ: <c>EditRules.HoldsStepRole</c> без гілки альтернативи — червоніють «загальний Approver»; без гілки
/// роль кроку — червоніють «носій ролі кроку»; <c>AllowsGeneralApprover</c> = true для будь-якого маршруту —
/// червоніє «маршрут проєкту»; <c>AlternativeRoleOfAsync</c> у <c>LastApprovalStepRoleAsync</c> → null — червоніє
/// «повернення затвердженого загальним».
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateApprovalRouteAccessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Крок_маршруту_шаблону_затверджує_роль_кроку_або_загальний_Approver_у_порядку_кроків()
    {
        await using var stand = await Stand.CreateAsync(sql);
        await stand.RouteAsync($"TPLA_{stand.Tag}", projectId: null, templateId: stand.Doc.TemplateId, stand.C1, stand.C2);
        await stand.SubmitAsync();

        // Крок 1 (C1): носій C1 і загальний — так; носій C2 (наступний крок) і ні того, ні іншого — ні.
        Assert.True(await stand.CanApproveAsync(stand.UserC1), "носій ролі кроку 1 затверджує");
        Assert.True(await stand.CanApproveAsync(stand.UserGeneral), "загальний Approver затверджує крок маршруту шаблону");
        Assert.False(await stand.CanApproveAsync(stand.UserC2), "крок 2 ще не настав — порядок кроків");
        Assert.False(await stand.CanApproveAsync(stand.UserNeither), "без ролі кроку й без Approver — відмова");

        // Крок 2 (C2).
        await stand.ApproveStepAsync();
        Assert.True(await stand.CanApproveAsync(stand.UserC2), "носій ролі кроку 2 затверджує на кроці 2");
        Assert.False(await stand.CanApproveAsync(stand.UserC1), "крок 1 уже пройдено");
        Assert.True(await stand.CanApproveAsync(stand.UserGeneral), "загальний — на кожному кроці маршруту шаблону");
        Assert.False(await stand.CanApproveAsync(stand.UserNeither), "без ролі — відмова");

        // Повернення ПОДАНОГО (поточний крок 2) — те саме правило.
        Assert.True(await stand.CanReopenAsync(stand.UserC2), "носій ролі поточного кроку повертає поданий");
        Assert.True(await stand.CanReopenAsync(stand.UserGeneral), "загальний повертає поданий");
        Assert.False(await stand.CanReopenAsync(stand.UserNeither), "без ролі — не повертає поданий");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Повернення_ЗАТВЕРДЖЕНОГО_роль_останнього_кроку_або_загальний_Approver()
    {
        await using var stand = await Stand.CreateAsync(sql);
        await stand.RouteAsync($"TPLB_{stand.Tag}", projectId: null, templateId: stand.Doc.TemplateId, stand.C1, stand.C2);
        await stand.SubmitAsync();
        await stand.ApproveStepAsync();
        await stand.ApproveStepAsync();
        Assert.Equal(DocumentStatus.Approved, await stand.StatusAsync());

        // Правило 437: власний аркуш ПОВНІСТЮ затверджений — затвердити (і правити як погоджувач, D-442) не може ніхто.
        Assert.False(await stand.CanApproveAsync(stand.UserGeneral), "затверджений аркуш не затверджується вдруге");
        Assert.False(await stand.CanApproveAsync(stand.UserC2), "затверджений аркуш не затверджується вдруге");

        Assert.True(await stand.CanReopenAsync(stand.UserC2), "носій ролі ОСТАННЬОГО кроку повертає затверджений");
        Assert.True(await stand.CanReopenAsync(stand.UserGeneral), "загальний повертає затверджений (маршрут шаблону)");
        Assert.False(await stand.CanReopenAsync(stand.UserC1), "роль не останнього кроку — ні");
        Assert.False(await stand.CanReopenAsync(stand.UserNeither), "без ролі — ні");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Маршрут_проєкту_без_змін_загальний_Approver_не_замінює_роль_кроку()
    {
        await using var stand = await Stand.CreateAsync(sql);
        await stand.RouteAsync($"PRJB_{stand.Tag}", projectId: stand.Doc.ProjectId, templateId: null, stand.C1);
        await stand.SubmitAsync();

        Assert.True(await stand.CanApproveAsync(stand.UserC1), "носій ролі кроку маршруту проєкту затверджує");
        Assert.False(await stand.CanApproveAsync(stand.UserGeneral), "маршрут ПРОЄКТУ: загальний Approver — не роль кроку");
        Assert.False(await stand.CanReopenAsync(stand.UserGeneral), "і не повертає");

        await stand.ApproveStepAsync();
        Assert.False(await stand.CanReopenAsync(stand.UserGeneral), "затверджений аркуш маршруту проєкту загальний не повертає");
        Assert.True(await stand.CanReopenAsync(stand.UserC1), "носій ролі останнього кроку повертає");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Виняток_проєкту_для_шаблону_перекриває_маршрут_шаблону_і_маршрут_шаблону_перекриває_маршрут_проєкту()
    {
        await using var stand = await Stand.CreateAsync(sql);
        await stand.RouteAsync($"PRJC_{stand.Tag}", projectId: stand.Doc.ProjectId, templateId: null, stand.C2);
        await stand.RouteAsync($"TPLC_{stand.Tag}", projectId: null, templateId: stand.Doc.TemplateId, stand.C1);
        await stand.SubmitAsync();

        // (–,T) над (P,–): крок — C1, і загальний теж.
        Assert.True(await stand.CanApproveAsync(stand.UserC1), "маршрут шаблону вище за маршрут проєкту");
        Assert.False(await stand.CanApproveAsync(stand.UserC2), "маршрут проєкту перекрито маршрутом шаблону");
        Assert.True(await stand.CanApproveAsync(stand.UserGeneral), "загальний — на маршруті шаблону");

        // (P,T) над (–,T).
        await stand.RouteAsync($"OVRC_{stand.Tag}", projectId: stand.Doc.ProjectId, templateId: stand.Doc.TemplateId, stand.C2);
        Assert.True(await stand.CanApproveAsync(stand.UserC2), "виняток проєкту вище за маршрут шаблону");
        Assert.False(await stand.CanApproveAsync(stand.UserC1), "маршрут шаблону перекрито винятком проєкту");
        Assert.True(await stand.CanApproveAsync(stand.UserGeneral), "виняток проєкту — теж маршрут шаблону: загальний так");
    }

    /// <summary>Свій документ, ролі, користувачі й маршрути; прибирає все своє.</summary>
    private sealed class Stand : IAsyncDisposable
    {
        private readonly SqlServerFixture _sql;
        private readonly TestDocumentBuilder _builder;
        private readonly List<string> _routes = [];
        private readonly MemoryCache _memory = new(new MemoryCacheOptions());

        private Stand(SqlServerFixture sql, TestDocumentBuilder builder, TestDocument doc)
        {
            _sql = sql;
            _builder = builder;
            Doc = doc;
            Tag = doc.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public TestDocument Doc { get; }

        public string Tag { get; }

        public int C1 { get; private set; }

        public int C2 { get; private set; }

        public int G { get; private set; }

        public int UserC1 { get; private set; }

        public int UserC2 { get; private set; }

        public int UserGeneral { get; private set; }

        public int UserNeither { get; private set; }

        public int Submitter { get; private set; }

        public static async Task<Stand> CreateAsync(SqlServerFixture sql)
        {
            var builder = new TestDocumentBuilder(sql.ConnectionString);
            var doc = await builder.BuildAsync(periodKey: 209901, columnCount: 2, rowCount: 1, ct: CancellationToken.None);
            await builder.OpenPeriodAsync(doc);
            var stand = new Stand(sql, builder, doc);

            await using var db = builder.CreateContext();
            var general = await db.Roles.Where(r => r.Code == "Approver" && r.IsBuiltIn).Select(r => r.Id).SingleAsync();

            var c1 = new Role(EcrCode.Create($"B155C1_{Guid.NewGuid():N}"[..20]), Text("Step 1"));
            var c2 = new Role(EcrCode.Create($"B155C2_{Guid.NewGuid():N}"[..20]), Text("Step 2"));
            var g = new Role(EcrCode.Create($"B155G_{Guid.NewGuid():N}"[..20]), Text("Grant"));
            db.Roles.AddRange(c1, c2, g);
            var users = new[] { NewUser("b155_c1"), NewUser("b155_c2"), NewUser("b155_ge"), NewUser("b155_no"), NewUser("b155_su") };
            db.Users.AddRange(users);
            await db.SaveChangesAsync();

            // Рівень Approve дає лише G; кроки — C1/C2; загальний — вбудований Approver (без грантів від тесту).
            db.ResourceGrants.Add(new ResourceGrant(g.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Approve));
            foreach (var user in users.Take(4))
            {
                db.RoleAssignments.Add(new RoleAssignment(g.Id, user.Id, null));
            }

            db.RoleAssignments.Add(new RoleAssignment(c1.Id, users[0].Id, null));
            db.RoleAssignments.Add(new RoleAssignment(c2.Id, users[1].Id, null));
            db.RoleAssignments.Add(new RoleAssignment(general, users[2].Id, null));
            await db.SaveChangesAsync();

            (stand.C1, stand.C2, stand.G) = (c1.Id, c2.Id, g.Id);
            (stand.UserC1, stand.UserC2, stand.UserGeneral, stand.UserNeither, stand.Submitter) =
                (users[0].Id, users[1].Id, users[2].Id, users[3].Id, users[4].Id);
            return stand;
        }

        public async Task RouteAsync(string code, int? projectId, int? templateId, params int[] roleIds)
        {
            await using var db = _builder.CreateContext();
            var route = new ApprovalRoute(EcrCode.Create(code), Text(code), projectId, templateVersionId: null, templateId: templateId);
            foreach (var roleId in roleIds)
            {
                route.AddStep(roleId);
            }

            db.ApprovalRoutes.Add(route);
            await db.SaveChangesAsync();
            _routes.Add(code);
        }

        public async Task SubmitAsync()
        {
            await using var db = _builder.CreateContext();
            var route = await new WorkflowStore(db).FindRouteAsync(Doc.ProjectId, Doc.TemplateVersionId, CancellationToken.None);
            var state = new ApprovalState(Doc.DocumentId, Doc.SheetDefId, Doc.PeriodKey.Value);
            state.Submit(Submitter, Now, route?.FirstStep?.Id);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        public async Task ApproveStepAsync()
        {
            await using var db = _builder.CreateContext();
            var route = await new WorkflowStore(db).FindRouteAsync(Doc.ProjectId, Doc.TemplateVersionId, CancellationToken.None);
            var state = await db.ApprovalStates.SingleAsync(s => s.DocumentId == Doc.DocumentId && s.SheetDefId == Doc.SheetDefId
                                                                  && s.PeriodKey == Doc.PeriodKey.Value && s.SheetCopyId == null);
            var current = route!.StepAt(state.CurrentStepId);
            state.ApproveStep(Submitter, Now, route.StepAfter(current!.Id)?.Id);
            await db.SaveChangesAsync();
        }

        public async Task<DocumentStatus> StatusAsync()
        {
            await using var db = _builder.CreateContext();
            return await db.ApprovalStates
                .Where(s => s.DocumentId == Doc.DocumentId && s.SheetDefId == Doc.SheetDefId && s.PeriodKey == Doc.PeriodKey.Value)
                .Select(s => s.Status)
                .SingleAsync();
        }

        public async Task<bool> CanApproveAsync(int userId)
        {
            await using var db = _builder.CreateContext();
            var service = Service(db);
            var profile = await service.BuildProfileAsync(userId, CancellationToken.None);
            return (await service.CanApproveAsync(profile, Doc.DocumentId, Doc.SheetDefId, Doc.PeriodKey, CancellationToken.None)).IsAllowed;
        }

        public async Task<bool> CanReopenAsync(int userId)
        {
            await using var db = _builder.CreateContext();
            var service = Service(db);
            var profile = await service.BuildProfileAsync(userId, CancellationToken.None);
            return (await service.CanReopenAsync(profile, Doc.DocumentId, Doc.SheetDefId, Doc.PeriodKey, CancellationToken.None)).IsAllowed;
        }

        public async ValueTask DisposeAsync()
        {
            var routes = string.Join(",", _routes.Select(r => $"N'{r}'"));
            var users = $"{UserC1},{UserC2},{UserGeneral},{UserNeither},{Submitter}";
            await using var connection = new SqlConnection(_sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                DELETE FROM wf.ApprovalState WHERE DocumentId = {Doc.DocumentId};
                {(routes.Length == 0 ? string.Empty : $"DELETE s FROM wf.ApprovalStep s JOIN wf.ApprovalRoute r ON r.Id = s.ApprovalRouteId WHERE r.Code IN ({routes}); DELETE FROM wf.ApprovalRoute WHERE Code IN ({routes});")}
                DELETE FROM sec.RoleAssignment WHERE UserId IN ({users});
                DELETE FROM sec.ResourceGrant WHERE RoleId IN ({C1},{C2},{G});
                DELETE FROM sec.Role WHERE Id IN ({C1},{C2},{G});
                """;
            await command.ExecuteNonQueryAsync();
            _memory.Dispose();
        }

        private AccessDecisionService Service(EcrDbContext db)
        {
            var currentUser = Substitute.For<ICurrentUser>();
            currentUser.UserId.Returns(1);
            currentUser.GroupSids.Returns([]);
            return new AccessDecisionService(
                db, new MetadataCache(_memory, db), new AccessProfileCache(_memory), new TestClock(Now), currentUser,
                new WorkflowStore(db));
        }

        private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

        private static User NewUser(string prefix)
        {
            var user = new User($"{prefix}_{Guid.NewGuid():N}"[..20], prefix, AuthProvider.Local);
            user.SetPassword("not-a-real-hash");
            return user;
        }
    }
}
