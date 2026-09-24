// tests/Ecr.Api.Tests/DocumentTemplateForProjectTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// V-12 (UX-прохід, третій раунд): оператор із <c>Document.Create</c> не міг
/// створити документ через інтерфейс — діалог питав <c>GET /templates</c>
/// (<c>Template.View</c>). Тепер джерело — <c>GET /projects/{id}/document-template</c>
/// з тим самим правом, що й на <c>POST /documents</c>, а права на шаблони
/// лишаються недоступними. Наскрізно: справжній SQL, вхід, HTTP.
/// </summary>
[Collection("SqlServer")]
public sealed class DocumentTemplateForProjectTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Template-2026!";

    private static readonly string[] OperatorPermissions =
        ["Document.View", "Document.Create", "Document.Export", "Document.Import"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Оператор_отримує_версію_проєкту_й_аркуші_і_створює_документ_без_Template_View()
    {
        var s = await ArrangeAsync(OperatorPermissions, deny: false).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // ⚠ Права на шаблони НЕ розширено: перелік шаблонів як був 403, так і є.
        var templates = await client.GetAsync(new Uri("/api/v1/templates?limit=100", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, templates.StatusCode);

        var response = await client
            .GetAsync(new Uri($"/api/v1/projects/{s.Document.ProjectId}/document-template", UriKind.Relative))
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var dto = JsonDocument.Parse(body).RootElement;
        Assert.Equal(s.Document.TemplateVersionId, dto.GetProperty("templateVersionId").GetInt32());
        var sheetIds = dto.GetProperty("sheets").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(s.Document.SheetDefId, sheetIds);

        // Те, що віддав ендпоінт, — достатньо, щоб створити документ.
        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new
            {
                projectId = s.Document.ProjectId,
                templateVersionId = dto.GetProperty("templateVersionId").GetInt32(),
                sheetDefIds = sheetIds,
            }).ConfigureAwait(true);
        Assert.True(
            create.StatusCode == HttpStatusCode.Created,
            $"{create.StatusCode}: {await create.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_Document_Create_403()
    {
        var s = await ArrangeAsync(["Document.View"], deny: false).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/projects/{s.Document.ProjectId}/document-template", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заборона_на_проєкт_404_як_на_читання()
    {
        var s = await ArrangeAsync(OperatorPermissions, deny: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/projects/{s.Document.ProjectId}/document-template", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private async Task<Scenario> ArrangeAsync(IEnumerable<string> permissions, bool deny)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync().ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var userName = $"dtp_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DTP_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Operator" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(
            role.Id, ResourceKind.Project, document.ProjectId, deny ? GrantLevel.Read : GrantLevel.Write, isDeny: deny));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document, userName);
    }

    private sealed record Scenario(TestDocument Document, string UserName);
}
