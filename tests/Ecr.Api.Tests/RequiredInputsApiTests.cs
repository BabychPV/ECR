// tests/Ecr.Api.Tests/RequiredInputsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-577 (L6) / D-578, огляд «тестові прогалини»: <c>GET /documents/{id}/tables/{instance}/required-inputs</c> і
/// <c>GET /methodologies/{id}/versions/{vid}/derived-inputs</c> наскрізно через HTTP — право, сховані колонки, чужий
/// екземпляр, чужа версія (власність маршруту), право методології.
/// </summary>
/// <remarks>
/// Мутації (локально): (1) у <c>GetRequiredInputsHandler</c> прибрати фільтр <c>readable.CanReadColumn</c> — червоний «схована
/// колонка»; (2) прибрати <c>DocumentVisibility.ResolveInDocumentAsync</c> — червоний «чужий екземпляр» (200 замість 404);
/// (3) у <c>MethodologiesController.DerivedInputs</c> прибрати <c>scope.RequireAsync</c> — червоні «чужа версія» і «без права».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-577")]
public sealed class RequiredInputsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Required-Inputs-2026!";

    [Fact]
    public async Task L6_перелік_входів_право_сховані_колонки_чужий_екземпляр()
    {
        var s = await ArrangeAsync();
        var reader = await UserAsync(s, ["Document.View"], denyColumn: null);
        var blind = await UserAsync(s, ["Document.View"], denyColumn: s.RequiredColumn);
        var stranger = await UserAsync(s, [], denyColumn: null, grantProject: false);

        using var app = new EcrApiFactory(sql);
        using (var client = await SignedInAsync(app, reader))
        {
            var body = await GetJsonAsync(client, RequiredUri(s.Document.DocumentId, s.Document.TableInstanceId));
            Assert.Equal(1, body.GetProperty("total").GetInt32());
            var cell = Assert.Single(body.GetProperty("cells").EnumerateArray());
            Assert.Equal(s.RequiredCode, cell.GetProperty("columnCode").GetString());
            Assert.Equal("block", cell.GetProperty("level").GetString());

            // Чужий екземпляр (іншого документа) під своїм документом — 404, а не чужі дані.
            var foreign = await client.GetAsync(RequiredUri(s.Document.DocumentId, s.Other.TableInstanceId));
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var client = await SignedInAsync(app, blind))
        {
            var body = await GetJsonAsync(client, RequiredUri(s.Document.DocumentId, s.Document.TableInstanceId));
            Assert.Equal(0, body.GetProperty("total").GetInt32());
            Assert.Empty(body.GetProperty("cells").EnumerateArray());
        }

        using (var client = await SignedInAsync(app, stranger))
        {
            var denied = await client.GetAsync(RequiredUri(s.Document.DocumentId, s.Document.TableInstanceId));
            Assert.False(denied.IsSuccessStatusCode, denied.StatusCode.ToString());
        }
    }

    [Fact]
    [Trait("Requirement", "D-578")]
    public async Task D578_виведені_входи_право_методології_і_власність_маршруту()
    {
        var s = await ArrangeAsync();
        var methodologist = await UserAsync(s, ["Calculation.View", "Calculation.ManageRequiredInputs"], denyColumn: null);
        var reader = await UserAsync(s, ["Document.View"], denyColumn: null);

        using var app = new EcrApiFactory(sql);
        using (var client = await SignedInAsync(app, methodologist))
        {
            var ok = await client.GetAsync(DerivedUri(s.MethodologyId, s.VersionId));
            Assert.True(ok.IsSuccessStatusCode, $"{ok.StatusCode}: {await ok.Content.ReadAsStringAsync()}");

            // Версія іншої методології під цією — 404 (власність маршруту), а не її перелік.
            var foreign = await client.GetAsync(DerivedUri(s.MethodologyId, s.OtherVersionId));
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var client = await SignedInAsync(app, reader))
        {
            var denied = await client.GetAsync(DerivedUri(s.MethodologyId, s.VersionId));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    private static Uri RequiredUri(long documentId, long instanceId)
        => new($"/api/v1/documents/{Num(documentId)}/tables/{Num(instanceId)}/required-inputs", UriKind.Relative);

    private static Uri DerivedUri(int methodologyId, int versionId)
        => new($"/api/v1/methodologies/{Num(methodologyId)}/versions/{Num(versionId)}/derived-inputs", UriKind.Relative);

    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 202604, columnCount: 3, rowCount: 1);
        var other = await builder.BuildAsync(periodKey: 202604, columnCount: 3, rowCount: 1);
        var input = document.ColumnDefIds[1];
        var required = document.ColumnDefIds[2];

        await using var db = builder.CreateContext();
        var code = (await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == required)).Code;

        var methodology = new Methodology(
            EcrCode.Create($"RI_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        var otherMethodology = new Methodology(
            EcrCode.Create($"RJ_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "o" }));
        db.Methodologies.AddRange(methodology, otherMethodology);
        await db.SaveChangesAsync();
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, at);
        version.Publish(2, "ri", new DateOnly(2026, 1, 1), testsPassed: true, at);
        var otherVersion = new MethodologyVersion(otherMethodology.Id, "1.0", CalculationLevel.Configuration, 1, at);
        db.MethodologyVersions.AddRange(version, otherVersion);
        await db.SaveChangesAsync();
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("ALL"), "{}", 100));
        db.MethodologyRequiredInputs.Add(new MethodologyRequiredInput(version.Id, required, RequiredInputSeverity.Block, hint: null));
        db.CalculationBindings.Add(new CalculationBinding(document.TableDefId, document.ColumnDefIds[0], methodology.Id, "tons", "{}"));
        await db.SaveChangesAsync();

        // Рядок розпочато (вхід), обов'язкове поле порожнє.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({document.PeriodKey.Value}, {document.RowIds[0]}, {input}, {document.TableDefId}, 5, 0, 0)
            """);

        return new Scenario(builder, document, other, required, code, methodology.Id, version.Id, otherVersion.Id);
    }

    private static async Task<string> UserAsync(Scenario s, string[] permissions, int? denyColumn, bool grantProject = true)
    {
        await using var db = s.Builder.CreateContext();
        var userName = $"reqin_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"REQIN_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Required inputs" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (grantProject)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, GrantLevel.Write));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Other.ProjectId, GrantLevel.Write));
        }

        if (denyColumn is { } column)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, column, GrantLevel.Read, isDeny: true));
        }

        await db.SaveChangesAsync();
        return userName;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, Uri uri)
    {
        var response = await client.GetAsync(uri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Document, TestDocument Other, int RequiredColumn, string RequiredCode,
        int MethodologyId, int VersionId, int OtherVersionId);
}
