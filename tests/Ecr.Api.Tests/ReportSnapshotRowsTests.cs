// tests/Ecr.Api.Tests/ReportSnapshotRowsTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-52a справжнім HTTP: рядки зрізу бачить лише той, хто має грант на проєкт
/// зрізу, а опис із колонкою, якої джерело не має, відмовляє реченням каталогу.
/// </summary>
[Collection("SqlServer")]
public sealed class ReportSnapshotRowsTests(SqlServerFixture sql)
{
    private const string Password = "Api-Report-Rows-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Рядки_зрізу_з_грантом_200_без_гранта_той_самий_404_що_й_неіснуючий()
    {
        using var app = new EcrApiFactory(sql);

        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        long snapshotId;

        await using (var db = Context())
        {
            var version = await db.ReportVersions.AsNoTracking().OrderBy(v => v.Id).FirstAsync().ConfigureAwait(true);
            var snapshot = new ReportSnapshot(
                version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Draft, DateTime.UtcNow, null);
            db.ReportSnapshots.Add(snapshot);
            await db.SaveChangesAsync().ConfigureAwait(true);

            var cell = new ReportRow(snapshot.Id, 1, "OutputCode");
            cell.SetValue("E_CO2", null, null);
            db.ReportRows.Add(cell);
            await db.SaveChangesAsync().ConfigureAwait(true);
            snapshotId = snapshot.Id;
        }

        var path = new Uri($"/api/v1/reports/snapshots/{snapshotId}/rows?limit=10", UriKind.Relative);

        using var owner = await SignedInAsync(app, document.ProjectId, "Report.ViewRegulatory").ConfigureAwait(true);
        var granted = await owner.GetAsync(path).ConfigureAwait(true);
        Assert.True(granted.IsSuccessStatusCode, $"{granted.StatusCode}: {app.ErrorsText}");

        using var page = JsonDocument.Parse(await granted.Content.ReadAsStringAsync().ConfigureAwait(true));
        var row = Assert.Single(page.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("E_CO2", row.GetProperty("cells").GetProperty("OutputCode").GetString());
        Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("nextCursor").ValueKind);

        using var stranger = await SignedInAsync(app, null, "Report.ViewRegulatory").ConfigureAwait(true);
        var foreign = await stranger.GetAsync(path).ConfigureAwait(true);
        var missing = await stranger
            .GetAsync(new Uri($"/api/v1/reports/snapshots/{long.MaxValue}/rows", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("ECR-RPT-0404", await foreign.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Опис_із_невідомою_колонкою_422_реченням_каталогу()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SignedInAsync(app, null, "Report.EditDefinition").ConfigureAwait(true);

        var response = await author.PostAsJsonAsync(
            new Uri("/api/v1/reports", UriKind.Relative),
            new
            {
                code = $"RPT{Guid.NewGuid():N}"[..11],
                nameL10n = new Dictionary<string, string> { ["en"] = "Unknown column" },
                isRegulatory = false,
                version = "1.0",
                columns = new[] { new { code = "Nope", kind = "text" } },
            }).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ECR-RPT-0422", body, StringComparison.Ordinal);
        Assert.Contains("has no column", body, StringComparison.Ordinal);
    }

    /// <summary>Користувач із правами й, за потреби, грантом Read на проєкт; входить локально.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, int? projectId, params string[] permissions)
    {
        var name = $"rpt_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Report rows test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            if (projectId is { } project)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, project, GrantLevel.Read));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
