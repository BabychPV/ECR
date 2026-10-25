// tests/Ecr.Infrastructure.Tests/Security/EditAssignmentDelegatorRightsTests.cs
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Аудит 25.10 (P2-2, P2-4): надання учасника діє, лише поки делегатор може його видавати, а повернення в роботу
/// вимагає ролі кроку маршруту так само, як затвердження.
/// </summary>
/// <remarks>
/// <para>⛔ Безпека. Мутаційні докази (доказ — червоний CI lane/permits/editassign-red до фіксу):</para>
/// <list type="bullet">
/// <item>P2-2: прибрати <c>WithDelegatorRightsAsync</c> у <c>ParticipationGrantsAsync</c> (або <c>DelegatorActive = false</c> у
/// ньому) → червоніють <c>Знижений_делегатор_надання_не_діє_а_відновлений_знову_діє</c> і
/// <c>Надання_не_видимість_після_зняття_ролей_делегатора</c>.</item>
/// <item>P2-4: прибрати передачу <c>requiredRoleId</c> у <c>CanReopenAsync</c> → червоніють
/// <c>Reopen_Approve_надання_без_ролі_кроку_відхиляється_у_Approved_і_Submitted</c>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentDelegatorRightsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        int DelegatorId,
        int ManagerRoleId,
        int ExecutorId,
        int StepRoleId);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static User NewUser(string prefix)
    {
        var user = new User($"{prefix}_{Guid.NewGuid():N}"[..20], "Delegator rights test", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    /// <summary>
    /// Документ з відкритим періодом; делегатор з роллю Manage на проєкт; виконавець без ролей; передача рівня
    /// <paramref name="level"/> на аркуш; роль кроку маршруту (маршрут проєкту з одним кроком — лише коли
    /// <paramref name="withRoute"/>).
    /// </summary>
    private async Task<World> ArrangeAsync(GrantLevel level, bool withRoute = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        await builder.OpenPeriodAsync(doc);

        await using var db = builder.CreateContext();

        var delegator = NewUser("dr_d");
        var executor = NewUser("dr_e");
        db.Users.AddRange(delegator, executor);

        var managerRole = new Role(EcrCode.Create($"DRM_{Guid.NewGuid():N}"[..20]), Text("Delegator manager"));
        var stepRole = new Role(EcrCode.Create($"DRS_{Guid.NewGuid():N}"[..20]), Text("Route step"));
        db.Roles.AddRange(managerRole, stepRole);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(managerRole.Id, delegator.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(managerRole.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Manage));

        var assignment = new EditAssignment(
            doc.DocumentId,
            EditAssignmentSlice.Create(doc.SheetCode, tableCode: null, monthNumber: null, periodKey: null),
            isExclusive: false,
            delegatedByUserId: delegator.Id,
            handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id,
            utcNow: Now,
            level: level);
        assignment.AddRecipient(executor.Id, delegator.Id, Now);
        new EditAssignmentStore(db).Add(assignment);

        if (withRoute)
        {
            var route = new ApprovalRoute(EcrCode.Create($"DRR_{Guid.NewGuid():N}"[..20]), Text("Delegator route"), doc.ProjectId);
            route.AddStep(stepRole.Id);
            db.ApprovalRoutes.Add(route);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return new World(builder, doc, delegator.Id, managerRole.Id, executor.Id, stepRole.Id);
    }

    private async Task SetSheetStateAsync(World w, DocumentStatus status, bool routeStep = false)
    {
        await using var db = w.Builder.CreateContext();
        var state = new ApprovalState(w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey.Value);
        int? firstStep = null;
        if (routeStep)
        {
            firstStep = await db.ApprovalSteps
                .Where(s => s.RoleId == w.StepRoleId)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(CancellationToken.None);
        }

        state.Submit(1, Now, firstStep);
        if (status == DocumentStatus.Approved)
        {
            state.Approve(1, Now);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static AccessProfile Participant(World w)
        => new AccessBuilder { UserId = w.ExecutorId, Participant = true }.Build();

    private AccessDecisionService Service(EcrDbContext db, MemoryCache memory, int actingUserId)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(actingUserId);
        currentUser.GroupSids.Returns([]);

        return new AccessDecisionService(
            db,
            new MetadataCache(memory, db),
            new AccessProfileCache(memory),
            new TestClock(Now),
            currentUser,
            new WorkflowStore(db));
    }

    private async Task<T> WithServiceAsync<T>(World w, Func<AccessDecisionService, Task<T>> action)
    {
        // Кеш профілів — свіжий на кожне рішення: тест міняє ролі делегатора прямо в базі (без штампа).
        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = w.Builder.CreateContext();
        return await action(Service(db, memory, w.ExecutorId));
    }

    private Task<EditDecision> SubmitAsync(World w)
        => WithServiceAsync(w, s => s.CanSubmitAsync(
            Participant(w), w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None));

    private Task<EditDecision> ApproveAsync(World w)
        => WithServiceAsync(w, s => s.CanApproveAsync(
            Participant(w), w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None));

    private Task<bool> CanReadTableAsync(World w)
        => WithServiceAsync(w, async s =>
        {
            var scope = await s.ReadScopeAsync(Participant(w), w.Doc.DocumentId, CancellationToken.None);
            return scope.InPeriod(w.Doc.PeriodKey).CanReadTable(w.Doc.TableDefId);
        });

    private Task<bool> ParticipatesAsync(World w)
        => WithServiceAsync(w, s => s.ParticipatesAsync(Participant(w), w.Doc.DocumentId, CancellationToken.None));

    private async Task RemoveDelegatorRolesAsync(World w)
    {
        await using var db = w.Builder.CreateContext();
        await db.RoleAssignments.Where(a => a.UserId == w.DelegatorId).ExecuteDeleteAsync(CancellationToken.None);
    }

    private async Task AssignRoleAsync(World w, int roleId)
    {
        await using var db = w.Builder.CreateContext();
        db.RoleAssignments.Add(new RoleAssignment(roleId, w.DelegatorId, null));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P2-2")]
    public async Task Знижений_делегатор_надання_не_діє_а_відновлений_знову_діє()
    {
        var w = await ArrangeAsync(GrantLevel.Submit);

        // Контроль: делегатор з Manage — надання діє (подання аркуша виконавцем без ролей).
        Assert.True((await SubmitAsync(w)).IsAllowed);

        // Зняті ролі делегатора — подання виконавцем відмовлено ОДРАЗУ, рядок передачі лишається.
        await RemoveDelegatorRolesAsync(w);
        var denied = await SubmitAsync(w);
        Assert.False(denied.IsAllowed);

        // Пониження до Write (не Manage, не автор) — теж не діє.
        await using (var db = w.Builder.CreateContext())
        {
            var writer = new Role(EcrCode.Create($"DRW_{Guid.NewGuid():N}"[..20]), Text("Delegator writer"));
            db.Roles.Add(writer);
            await db.SaveChangesAsync(CancellationToken.None);
            db.ResourceGrants.Add(new ResourceGrant(writer.Id, ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Write));
            await db.SaveChangesAsync(CancellationToken.None);
            await AssignRoleAsync(w, writer.Id);
        }

        Assert.False((await SubmitAsync(w)).IsAllowed);

        // Відновлення прав (Manage) — те саме надання знову діє без жодних правок рядка передачі.
        await RemoveDelegatorRolesAsync(w);
        await AssignRoleAsync(w, w.ManagerRoleId);
        Assert.True((await SubmitAsync(w)).IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P2-2")]
    public async Task Знижений_делегатор_Approve_надання_не_діє()
    {
        var w = await ArrangeAsync(GrantLevel.Approve);
        await SetSheetStateAsync(w, DocumentStatus.Submitted);

        Assert.True((await ApproveAsync(w)).IsAllowed);

        await RemoveDelegatorRolesAsync(w);
        Assert.False((await ApproveAsync(w)).IsAllowed);

        await AssignRoleAsync(w, w.ManagerRoleId);
        Assert.True((await ApproveAsync(w)).IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P2-2")]
    public async Task Надання_не_видимість_після_зняття_ролей_делегатора()
    {
        var w = await ArrangeAsync(GrantLevel.Read);

        Assert.True(await ParticipatesAsync(w));
        Assert.True(await CanReadTableAsync(w));

        await RemoveDelegatorRolesAsync(w);
        Assert.False(await ParticipatesAsync(w));
        Assert.False(await CanReadTableAsync(w));

        await AssignRoleAsync(w, w.ManagerRoleId);
        Assert.True(await ParticipatesAsync(w));
        Assert.True(await CanReadTableAsync(w));
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Submitted)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P2-4")]
    public async Task Reopen_Approve_надання_без_ролі_кроку_відхиляється_у_Approved_і_Submitted(DocumentStatus status)
    {
        var w = await ArrangeAsync(GrantLevel.Approve, withRoute: true);
        await SetSheetStateAsync(w, status, routeStep: status == DocumentStatus.Submitted);

        async Task<EditDecision> ReopenAsync(AccessProfile profile)
        {
            using var memory = new MemoryCache(new MemoryCacheOptions());
            await using var db = w.Builder.CreateContext();
            return await Service(db, memory, profile.UserId).CanReopenAsync(
                profile, w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None);
        }

        // Учасник з Approve-наданням, але без ролі кроку маршруту — відмова (так само, як у CanApprove).
        Assert.False((await ReopenAsync(Participant(w))).IsAllowed);

        // Контроль: власник кроку (роль кроку + грант Approve на проєкт) повертає, як і доти.
        var stepOwner = new AccessBuilder { UserId = w.ExecutorId }
            .Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Approve)
            .Role(w.StepRoleId)
            .Build();
        Assert.True((await ReopenAsync(stepOwner)).IsAllowed);

        // Контроль: учасник з Approve-наданням І ролью кроку теж повертає.
        var participantWithRole = new AccessBuilder { UserId = w.ExecutorId, Participant = true }
            .Role(w.StepRoleId)
            .Build();
        Assert.True((await ReopenAsync(participantWithRole)).IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P2-4")]
    public async Task Reopen_без_маршруту_лишається_як_було()
    {
        var w = await ArrangeAsync(GrantLevel.Approve, withRoute: false);
        await SetSheetStateAsync(w, DocumentStatus.Approved);

        // Маршруту немає — перевірки ролі кроку немає: Approve-учасник повертає, як і доти.
        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = w.Builder.CreateContext();
        var decision = await Service(db, memory, w.ExecutorId).CanReopenAsync(
            Participant(w), w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }
}
