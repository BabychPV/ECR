// tests/Ecr.Api.Tests/ProjectTemplatesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B74 P3 (D-576, рішення людини 13.11): проєкт «рік · об'єкт» з документами РІЗНИХ шаблонів — наскрізно: справжній SQL,
/// вхід, HTTP. Шаблон A — основний (як після бекфілу PT01 / ремонту 99-epilogue), шаблон B приєднується через API.
/// </summary>
[Collection("SqlServer")]
public sealed partial class ProjectTemplatesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-B74-Templates-2026!";

    private static readonly string[] ManagerPermissions =
        ["Project.Manage", "Document.View", "Document.Create", "Document.Submit"];

    /// <summary>
    /// Черга «До погодження» (B155/D2): аркуш документа ДРУГОГО шаблону проєкту погоджувач бачить — знімок і маршрут
    /// беруться з версії пари, а не з версії основного шаблону.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати гілку <c>ApprovalQueuePairVersions</c> у <c>ListMyApprovalQueueHandler</c> — червоне (черга
    /// порожня: аркуша B у знімку версії A немає).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Finding", "B155")]
    public async Task Аркуш_документа_другого_шаблону_зявляється_в_черзі_погодження()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Approve).ConfigureAwait(true);
        await new TestDocumentBuilder(sql.ConnectionString).OpenPeriodAsync(s.A).ConfigureAwait(true);

        long documentId;
        await using (var db = Context())
        {
            db.ProjectTemplates.Add(new ProjectTemplate(s.A.ProjectId, s.B.TemplateId, s.B.TemplateVersionId));
            var document = new Document(s.A.ProjectId, $"B74Q-{Guid.NewGuid():N}"[..16], 1, DateTime.UtcNow, s.B.TemplateId);
            db.Documents.Add(document);
            await db.SaveChangesAsync().ConfigureAwait(true);
            documentId = document.Id;
            db.DocumentSheets.Add(new DocumentSheet(documentId, s.B.SheetDefId));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var submit = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/submit", UriKind.Relative),
            new { sheetDefId = s.B.SheetDefId, periodKey = s.A.PeriodKey.Value }).ConfigureAwait(true);
        Assert.True(submit.StatusCode == HttpStatusCode.NoContent, $"{submit.StatusCode}: {await submit.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var queue = await GetJsonAsync(app, client, "/api/v1/me/approval-queue").ConfigureAwait(true);
        var item = Assert.Single(queue.GetProperty("items").EnumerateArray(), x => x.GetProperty("documentId").GetInt64() == documentId);
        Assert.Equal(s.B.SheetDefId, item.GetProperty("sheetDefId").GetInt32());
    }

    /// <summary>
    /// Чинна версія документа ДРУГОГО шаблону — версія пари: і для пошуку версії документа (шапка, перенос, призначення,
    /// копії аркушів — <c>IDocumentStore.GetTemplateVersionIdAsync</c>), і для знімка читання доступу (S17: таблиці аркуша B
    /// видно за грантом на проєкт).
    /// </summary>
    /// <remarks>
    /// ⛔ Мутації: (1) <c>GetTemplateVersionIdAsync</c> з версією проєкту — червоне; (2) <c>AccessDecisionService.ReadScopeAsync</c>
    /// з версією проєкту — червоне (аркуша B у знімку версії A немає, <c>VisibleTablesOfSheet</c> = null).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Finding", "S17")]
    public async Task Версія_й_знімок_читання_документа_другого_шаблону_беруться_з_пари()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Write).ConfigureAwait(true);
        long documentId;
        int userId;
        await using (var db = Context())
        {
            db.ProjectTemplates.Add(new ProjectTemplate(s.A.ProjectId, s.B.TemplateId, s.B.TemplateVersionId));
            var document = new Document(s.A.ProjectId, $"B74V-{Guid.NewGuid():N}"[..16], 1, DateTime.UtcNow, s.B.TemplateId);
            db.Documents.Add(document);
            await db.SaveChangesAsync().ConfigureAwait(true);
            documentId = document.Id;
            userId = await db.Users.Where(u => u.UserName == s.UserName).Select(u => u.Id).SingleAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var scope = app.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<Ecr.Application.Ports.IDocumentStore>();
        Assert.Equal(s.B.TemplateVersionId, await documents.GetTemplateVersionIdAsync(documentId, CancellationToken.None).ConfigureAwait(true));
        Assert.Equal(s.A.TemplateVersionId, await documents.GetTemplateVersionIdAsync(s.A.DocumentId, CancellationToken.None).ConfigureAwait(true));

        var access = scope.ServiceProvider.GetRequiredService<Ecr.Application.Security.IAccessDecisionService>();
        var profile = await access.BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(true);
        var read = await access.ReadScopeAsync(profile, documentId, CancellationToken.None).ConfigureAwait(true);
        var visible = read.VisibleTablesOfSheet(s.B.SheetDefId);
        Assert.NotNull(visible);
        Assert.Contains(s.B.TableDefId, visible.Value.Tables.Keys);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static Task<HttpResponseMessage> AttachAsync(HttpClient client, int projectId, int versionId)
        => client.PostAsJsonAsync(new Uri($"/api/v1/projects/{projectId}/templates", UriKind.Relative), new { templateVersionId = versionId });

    private static Task<HttpResponseMessage> PostDocumentAsync(HttpClient client, int projectId, int templateId, int sheetDefId)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId, templateId, sheetDefIds = new[] { sheetDefId } });

    private static async Task<long> CreateDocumentAsync(EcrApiFactory app, HttpClient client, int projectId, int templateId, int sheetDefId)
    {
        var response = await PostDocumentAsync(client, projectId, templateId, sheetDefId).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {text}\n{app.ErrorsText}");
        return JsonDocument.Parse(text).RootElement.GetProperty("documentId").GetInt64();
    }

    private static async Task<List<JsonElement>> TemplatesAsync(EcrApiFactory app, HttpClient client, int projectId)
        => [.. (await GetJsonAsync(app, client, $"/api/v1/projects/{projectId}/templates").ConfigureAwait(false)).EnumerateArray()];

    private static async Task<JsonElement> GetJsonAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode} (очікувано {status}): {body}");
        var root = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, root.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, root.GetProperty("messageKey").GetString());
        return root;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Проєкт A (основний шаблон — як після бекфілу PT01: рядок <c>doc.ProjectTemplate</c> і <c>Document.TemplateId</c>),
    /// окремий шаблон B (його власний проєкт — лише джерело версії й аркуша), друга версія шаблону A.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(IEnumerable<string> permissions, GrantLevel level, bool deny = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync().ConfigureAwait(false);
        var b = await builder.BuildAsync().ConfigureAwait(false);

        await using var db = Context();

        db.ProjectTemplates.Add(new ProjectTemplate(a.ProjectId, a.TemplateId, a.TemplateVersionId));
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET TemplateId = {a.TemplateId} WHERE Id = {a.DocumentId}").ConfigureAwait(false);

        var a2 = new Ecr.Domain.Entities.Configuration.TemplateVersion(a.TemplateId, $"2.0.0.{a.TemplateVersionId}", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(a2);

        var userName = $"b74_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"B74_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "B74 manager" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, a.ProjectId, level, isDeny: deny));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var facts = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == a.TemplateVersionId || v.Id == b.TemplateVersionId)
            .Join(db.Templates, v => v.TemplateId, t => t.Id, (v, t) => new { v.Id, v.Version, t.Code })
            .ToListAsync().ConfigureAwait(false);
        var ofA = facts.Single(f => f.Id == a.TemplateVersionId);
        var ofB = facts.Single(f => f.Id == b.TemplateVersionId);

        return new Scenario(a, b, a2.Id, userName, ofA.Code, ofA.Version, ofB.Code, ofB.Version);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed record Scenario(
        TestDocument A, TestDocument B, int A2VersionId, string UserName,
        string ACode, string AVersion, string BCode, string BVersion);
}
