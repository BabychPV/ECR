// tests/Ecr.Api.Tests/ValidationHeaderFindingTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// RC15C D-1: порожнє обов'язкове поле шапки — знахідка <c>ECR-HDR-0422</c> з <c>tableDefId = 0</c> —
/// доходить до клієнта і з <c>POST …/validate</c>, і зі збереженого <c>GET …/validation</c> (панель Issues).
/// </summary>
/// <remarks>
/// ⚠ Доказ «сервер віддає»: знахідка не губиться ні при збереженні (<c>wf.ValidationResult</c>), ні фільтром
/// читача (<c>HiddenValidationIssues.CanSee</c> пропускає шапку), ні в DTO. Лише коди полів — жодного значення.
/// Читач — звужений (<c>Read</c>-грант), щоб довести, що шапка не має scope.
/// </remarks>
[Collection("SqlServer")]
public sealed class ValidationHeaderFindingTests(SqlServerFixture sql)
{
    private const string Password = "Api-Hdr-Finding-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-PS")]
    public async Task Порожнє_обовязкове_поле_шапки_віддається_і_з_validate_і_зі_збереженого_результату()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(true);

        await using var db = builder.CreateContext();
        var permit = new HeaderFieldDef(
            document.TemplateVersionId, EcrCode.Create("PERMIT"), Text("Permit"), 0, CellDataType.String);
        permit.SetRequired(true);
        db.HeaderFieldDefs.Add(permit);

        var userName = $"hdrf_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"HDRF_{Guid.NewGuid():N}"), Text("Header finding reader"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Read));
        await db.SaveChangesAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        var posted = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{document.DocumentId}/validate", UriKind.Relative),
            new { periodKey = document.PeriodKey.Value }).ConfigureAwait(true);
        var postedBody = await posted.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(posted.IsSuccessStatusCode, $"POST: {posted.StatusCode}\n{postedBody}\n{app.ErrorsText}");
        AssertHeaderFinding(postedBody);

        var stored = await client.GetAsync(new Uri(
            $"/api/v1/documents/{document.DocumentId}/validation?periodKey={document.PeriodKey.Value}",
            UriKind.Relative)).ConfigureAwait(true);
        var storedBody = await stored.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(stored.IsSuccessStatusCode, $"GET: {stored.StatusCode}\n{storedBody}\n{app.ErrorsText}");
        AssertHeaderFinding(storedBody);
    }

    private static void AssertHeaderFinding(string body)
    {
        var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        var finding = Assert.Single(messages, m => m.GetProperty("ruleCode").GetString() == "ECR-HDR-0422");
        Assert.Equal("Error", finding.GetProperty("severity").GetString());
        Assert.Equal(0, finding.GetProperty("tableDefId").GetInt32());
        Assert.Null(finding.GetProperty("rowKey").GetString());
        Assert.Null(finding.GetProperty("columnCode").GetString());
        Assert.Contains("PERMIT", finding.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
