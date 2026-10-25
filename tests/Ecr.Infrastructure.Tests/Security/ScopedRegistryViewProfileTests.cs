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
        int[]? extraViewScopeProjects = null)
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
            assignment.SetScope(RoleAssignmentScope.Create(scopeProjects));
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

    private async Task<bool> CanChooseAsync(TestDocument doc, int userId)
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
        int? viewProject = LookupTargetMatcher.ViewProjectNeeded(profile) ? doc.ProjectId : null;
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
}
