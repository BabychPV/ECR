// tests/Ecr.Infrastructure.Tests/Security/ScopedRegistryViewProfileTests.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Groups;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
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
/// P48 (hotfix p47): вибір запису Lookup потребує <c>Registry.View</c>; роль, призначена на проєкт документа (scoped),
/// теж його дає ДЛЯ ЦЬОГО вибору. Профіль будує РЕАЛЬНИЙ <see cref="AccessDecisionService.BuildProfileAsync"/> -
/// <c>Registry.View</c> входить у <c>PermissionScopes.Global</c> і в <c>Scoped[проєкт].Permissions</c> не потрапляє,
/// тож тест з ручним профілем цього не бачив (урок p47).
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>RegistryView</c> зі <c>ScopedProjectAccess</c>/<c>CanViewRegistry</c> - червоніє
/// <c>Scoped_роль_на_проєкт_документа_з_View_може_обрати_запис</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScopedRegistryViewProfileTests(SqlServerFixture sql) : IDisposable
{
    private const int Registry = 4242;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    private static LocalizedText Text(string v) => new(new Dictionary<string, string> { ["en"] = v });

    private Task<TestDocument> ArrangeAsync()
        => new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 1, rowCount: 1, ct: CancellationToken.None);

    private async Task<int> UserAsync(
        TestDocument doc, bool view, int[]? scopeProjects, bool projectWrite = true, bool deny = false,
        int[]? extraViewScopeProjects = null, string[]? narrowSheets = null,
        bool scopedReadGrant = false, bool globalViewRole = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();

        var user = new User($"srv_{Guid.NewGuid():N}"[..20], "Scoped view", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"SRV_{Guid.NewGuid():N}"[..20]), Text("Scoped view"));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        if (view)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, null);
        if (scopeProjects is not null)
        {
            assignment.SetScope(RoleAssignmentScope.Create(scopeProjects, narrowSheets));
        }

        db.RoleAssignments.Add(assignment);
        if (projectWrite)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        }

        if (deny)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, Registry, GrantLevel.None, isDeny: true));
        }

        if (scopedReadGrant)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, Registry, GrantLevel.Read));
        }

        if (globalViewRole)
        {
            var global = new Role(EcrCode.Create($"SRG_{Guid.NewGuid():N}"[..20]), Text("Global view"));
            db.Roles.Add(global);
            await db.SaveChangesAsync(CancellationToken.None);
            db.RolePermissions.Add(new RolePermission(global.Id, "Registry.View"));
            db.RoleAssignments.Add(new RoleAssignment(global.Id, user.Id, null));
        }

        // Окрема роль БЕЗ області для доступу до документа + роль View, призначена лише на ІНШИЙ проєкт.
        if (extraViewScopeProjects is not null)
        {
            var viewRole = new Role(EcrCode.Create($"SRW_{Guid.NewGuid():N}"[..20]), Text("View elsewhere"));
            db.Roles.Add(viewRole);
            await db.SaveChangesAsync(CancellationToken.None);
            db.RolePermissions.Add(new RolePermission(viewRole.Id, "Registry.View"));
            var elsewhere = new RoleAssignment(viewRole.Id, user.Id, null);
            elsewhere.SetScope(RoleAssignmentScope.Create(extraViewScopeProjects));
            db.RoleAssignments.Add(elsewhere);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return user.Id;
    }

    private async Task<bool> CanChooseAsync(TestDocument doc, int userId, int? projectOverride = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(userId);
        currentUser.GroupSids.Returns([]);
        var service = new AccessDecisionService(
            db, new MetadataCache(_memory, db), new AccessProfileCache(_memory), new TestClock(DateTime.UtcNow),
            currentUser, new WorkflowStore(db));

        var profile = await service.BuildProfileAsync(userId, CancellationToken.None);

        // Як викликачі: проєкт документа передається лише коли він потрібен.
        int? viewProject = LookupTargetMatcher.ViewProjectNeeded(profile) ? projectOverride ?? doc.ProjectId : null;
        return LookupTargetMatcher.Belongs(null, profile, Registry, Registry, viewProject);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Scoped_роль_на_проєкт_документа_з_View_може_обрати_запис()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(doc, view: true, scopeProjects: [doc.ProjectId]);

        Assert.True(await CanChooseAsync(doc, user));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Scoped_View_лише_на_інший_проєкт_запис_обрати_не_можна()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(
            doc, view: false, scopeProjects: null, extraViewScopeProjects: [doc.ProjectId + 1_000_000]);

        Assert.False(await CanChooseAsync(doc, user));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Без_View_запис_обрати_не_можна()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(doc, view: false, scopeProjects: null);
        var scopedNoView = await UserAsync(doc, view: false, scopeProjects: [doc.ProjectId]);

        Assert.False(await CanChooseAsync(doc, user));
        Assert.False(await CanChooseAsync(doc, scopedNoView));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P48")]
    public async Task Глобальний_View_дозволяє_а_View_з_Deny_на_реєстр_забороняє()
    {
        var doc = await ArrangeAsync();
        var global = await UserAsync(doc, view: true, scopeProjects: null);
        var denied = await UserAsync(doc, view: true, scopeProjects: null, deny: true);

        Assert.True(await CanChooseAsync(doc, global));
        Assert.False(await CanChooseAsync(doc, denied));
    }

    /// <summary>
    /// P3-6 (рецензія p48): роль зі ЗВУЖЕННЯМ (аркуші/періоди, D-214) з Registry.View прапорця RegistryView не дає -
    /// він лише від ролей без звуження, призначених на проєкт.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-NARROWED")]
    public async Task Звужена_роль_з_View_не_дає_RegistryView_і_вибір_запису_закритий()
    {
        var doc = await ArrangeAsync();
        var narrowed = await UserAsync(doc, view: true, scopeProjects: [doc.ProjectId], narrowSheets: ["SHEET_X"]);

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(narrowed);
        currentUser.GroupSids.Returns([]);
        var service = new AccessDecisionService(
            db, new MetadataCache(_memory, db), new AccessProfileCache(_memory), new TestClock(DateTime.UtcNow),
            currentUser, new WorkflowStore(db));

        var profile = await service.BuildProfileAsync(narrowed, CancellationToken.None);

        Assert.False(profile.Scoped.TryGetValue(doc.ProjectId, out var scoped) && scoped.RegistryView);
        Assert.False(await CanChooseAsync(doc, narrowed));
    }
    // ── P50 (Аудит-2 W14g, P2): Deny/Read-грант, видані САМІЙ ролі з областю, діють у проєкті призначення ────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P50")]
    public async Task Scoped_View_і_власний_Deny_у_тій_самій_ролі_запис_обрати_не_можна()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(doc, view: true, scopeProjects: [doc.ProjectId], deny: true);

        Assert.False(await CanChooseAsync(doc, user));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P50")]
    public async Task Scoped_Deny_діє_лише_в_проєкті_призначення_а_в_іншому_проєкті_ні()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(doc, view: false, scopeProjects: [doc.ProjectId], deny: true, globalViewRole: true);

        Assert.False(await CanChooseAsync(doc, user));
        Assert.True(await CanChooseAsync(doc, user, projectOverride: doc.ProjectId + 1_000_000));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P50")]
    public async Task Scoped_грант_Read_на_реєстр_без_View_дозволяє_вибір_лише_у_проєкті_призначення()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(doc, view: false, scopeProjects: [doc.ProjectId], scopedReadGrant: true);

        Assert.True(await CanChooseAsync(doc, user));
        Assert.False(await CanChooseAsync(doc, user, projectOverride: doc.ProjectId + 1_000_000));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P50")]
    public async Task Глобальний_Deny_як_раніше_перебиває_глобальний_View_і_без_Deny_вибір_дозволений()
    {
        var doc = await ArrangeAsync();
        var denied = await UserAsync(doc, view: true, scopeProjects: null, deny: true);
        var plain = await UserAsync(doc, view: true, scopeProjects: [doc.ProjectId]);

        Assert.False(await CanChooseAsync(doc, denied));
        Assert.True(await CanChooseAsync(doc, plain));
    }
    // ── P50b (Аудит P3-1): Deny ролі зі ЗВУЖЕНОЮ областю (D-214) теж діє; View/Read-грант такої ролі - ні (fail-closed) ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P50b")]
    public async Task Deny_звуженої_ролі_перебиває_View_іншої_ролі_лише_в_проєкті_призначення()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(
            doc, view: false, scopeProjects: [doc.ProjectId], deny: true, globalViewRole: true, narrowSheets: ["SHEET_X"]);

        Assert.False(await CanChooseAsync(doc, user));
        Assert.True(await CanChooseAsync(doc, user, projectOverride: doc.ProjectId + 1_000_000));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P50b")]
    public async Task View_і_Read_грант_звуженої_ролі_доступу_не_дають()
    {
        var doc = await ArrangeAsync();
        var user = await UserAsync(
            doc, view: true, scopeProjects: [doc.ProjectId], scopedReadGrant: true, narrowSheets: ["SHEET_X"]);

        Assert.False(await CanChooseAsync(doc, user));
    }}
