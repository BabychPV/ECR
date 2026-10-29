// tests/Ecr.Api.Tests/DocumentListTemplateVersionTests.cs
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
/// RPT-STABLE (Part A, контракт): перелік документів (<c>GET /documents</c>) віддає <c>templateVersion</c> — типову версію
/// шаблону документа (колонка «Версія»), а <c>templateVersionByPeriod</c> — РІДКУ карту періодів з іншою версією
/// (зараз відсутня: версія живе лише в проєкті; заповнить Part B «міграція з обраного місяця»).
/// </summary>
[Collection("SqlServer")]
public sealed class DocumentListTemplateVersionTests(SqlServerFixture sql)
{
    private const string Password = "Api-List-TplVer-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_документів_віддає_templateVersion_версії_шаблону_проєкту_і_без_карти_періодів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(true);

        await using var db = builder.CreateContext();
        var expected = await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == doc.TemplateVersionId)
            .Select(v => v.Version)
            .SingleAsync().ConfigureAwait(true);

        var userName = $"tl_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"TPLL_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "List template version viewer" }));
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
            new Uri($"/api/v1/documents?projectId={doc.ProjectId}&periodKey={doc.PeriodKey.Value}&limit=50", UriKind.Relative))
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var items = JsonDocument.Parse(body).RootElement.GetProperty("items");
        var row = items.EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == doc.DocumentId);
        Assert.True(row.TryGetProperty("templateVersion", out var value), $"templateVersion немає в рядку переліку: {body}");
        Assert.Equal(expected, value.GetString());

        // Карта періодів — рідка: поки всі періоди на типовій версії, її немає (null або поле не серіалізується).
        Assert.True(
            !row.TryGetProperty("templateVersionByPeriod", out var map) || map.ValueKind == JsonValueKind.Null,
            $"templateVersionByPeriod мусить бути порожнім, поки версії однакові: {body}");
    }
}
