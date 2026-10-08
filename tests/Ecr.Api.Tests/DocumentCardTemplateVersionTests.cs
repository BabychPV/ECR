// tests/Ecr.Api.Tests/DocumentCardTemplateVersionTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// RC15, вкладка Contract: картка документа (<c>GET /documents/{id}</c>) віддає <c>templateVersion</c> — рядок
/// версії шаблону (<c>TemplateVersion.Version</c>) проєкту документа; клієнт показує його як службове поле «Version».
/// Адитивне необов'язкове поле; метадані шаблону не секрет, тож його бачить і звужений читач.
/// </summary>
[Collection("SqlServer")]
public sealed class DocumentCardTemplateVersionTests(SqlServerFixture sql)
{
    private const string Password = "Api-Card-TplVer-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Картка_документа_віддає_templateVersion_рівний_версії_шаблону_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(true);

        await using var db = builder.CreateContext();

        var expected = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == doc.TemplateVersionId)
            .Select(v => v.Version)
            .SingleAsync().ConfigureAwait(true);

        var userName = $"tv_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"TPLV_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Card template version viewer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}?periodKey={doc.PeriodKey.Value}", UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var card = JsonDocument.Parse(body).RootElement;
        Assert.True(card.TryGetProperty("templateVersion", out var value), $"templateVersion немає в картці: {body}");
        Assert.Equal(expected, value.GetString());
    }
}
