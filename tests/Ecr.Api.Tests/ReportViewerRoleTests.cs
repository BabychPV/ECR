// tests/Ecr.Api.Tests/ReportViewerRoleTests.cs
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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Рішення людини 2026-09-29 («ні, додай роль» на питання «регуляторний звіт і
/// далі бачать усі з доступом до проєкту?»): вміст регуляторного зрізу — рядки
/// й книга — за окремим проєктним правом <c>Report.ViewSnapshot</c> плюс грант
/// Read на проєкт; роль «Переглядач звітів» (<c>ReportViewer</c>) — у сіді.
/// </summary>
/// <remarks>
/// ⛔ Ролі тут — ВБУДОВАНІ з сіду (<c>ReportViewer</c>, <c>Approver</c>,
/// <c>SystemAdministrator</c>, <c>Viewer</c>, <c>DataEntry</c>), а грант Read —
/// окремою власною роллю без жодного права: перевіряється склад ролей, який
/// дає розгортання, і те, що самого гранта Read уже НЕ досить.
/// <para>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>PermissionCheck.RequireIn(…, ContentPermission, …)</c>
/// з <c>GetSnapshotRowsHandler</c> і <c>ExportSnapshotHandler</c> — червоніють
/// рядки «без права → 403» (дають 200).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewerRoleTests(SqlServerFixture sql)
{
    private const string Password = "Api-Report-Viewer-2026!";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    [InlineData("Viewer")]
    [InlineData("DataEntry")]
    [InlineData("Auditor")]
    public async Task Читання_проєкту_без_права_на_вміст_403_на_рядки_й_книгу(string builtInRole)
    {
        // ⚠ Кожна з цих ролей мала шлях до вмісту до рішення: `Viewer` і
        // `Auditor` — `Report.ViewRegulatory` (рядки), `Viewer` і `DataEntry` —
        // `Report.Export` (книга).
        var (a, _) = await TwoSnapshotsAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, [(builtInRole, null)], readOn: [a.ProjectId]).ConfigureAwait(true);

        var rows = await client.GetAsync(RowsOf(a.SnapshotId)).ConfigureAwait(true);
        var book = await client.GetAsync(BookOf(a.SnapshotId)).ConfigureAwait(true);

        await AssertForbiddenAsync(rows, builtInRole == "DataEntry" ? "Report.ViewRegulatory" : "Report.ViewSnapshot")
            .ConfigureAwait(true);
        await AssertForbiddenAsync(book, builtInRole == "Auditor" ? "Report.Export" : "Report.ViewSnapshot")
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Грант_Read_і_права_переліку_й_книги_без_права_на_вміст_403()
    {
        // ⛔ Найближчий до межі випадок: є все, КРІМ `Report.ViewSnapshot`.
        var (a, _) = await TwoSnapshotsAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInCustomAsync(
                app, a.ProjectId, "Report.ViewRegulatory", "Report.Export", "Report.BuildSnapshot")
            .ConfigureAwait(true);

        await AssertForbiddenAsync(await client.GetAsync(RowsOf(a.SnapshotId)).ConfigureAwait(true), "Report.ViewSnapshot")
            .ConfigureAwait(true);
        await AssertForbiddenAsync(await client.GetAsync(BookOf(a.SnapshotId)).ConfigureAwait(true), "Report.ViewSnapshot")
            .ConfigureAwait(true);

        // Перелік і перевірка суми лишаються: вмісту вони не віддають.
        var list = await client.GetAsync(new Uri($"/api/v1/reports/snapshots?projectId={a.ProjectId}", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    [InlineData("ReportViewer")]
    [InlineData("Approver")]
    [InlineData("SystemAdministrator")]
    public async Task Переглядач_звітів_погоджувач_і_адміністратор_бачать_вміст(string builtInRole)
    {
        var (a, _) = await TwoSnapshotsAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, [(builtInRole, null)], readOn: [a.ProjectId]).ConfigureAwait(true);

        var rows = await client.GetAsync(RowsOf(a.SnapshotId)).ConfigureAwait(true);
        var body = await rows.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(rows.StatusCode == HttpStatusCode.OK, $"{builtInRole}: {rows.StatusCode} {body} {app.ErrorsText}");

        using var page = JsonDocument.Parse(body);
        var row = Assert.Single(page.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("E_CO2", row.GetProperty("cells").GetProperty("OutputCode").GetString());

        var book = await client.GetAsync(BookOf(a.SnapshotId)).ConfigureAwait(true);
        Assert.True(book.StatusCode == HttpStatusCode.OK, $"{builtInRole}: {book.StatusCode} {app.ErrorsText}");
        Assert.NotEmpty(await book.Content.ReadAsByteArrayAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Переглядач_звітів_з_областю_A_не_бачить_зрізів_B()
    {
        // ⛔ Грант Read — на ОБИДВА проєкти, а роль «Переглядач звітів» — з
        // областю {A}: зупинити читання зрізу B може лише проєктна перевірка
        // права, гранти її не підмінять.
        var (a, b) = await TwoSnapshotsAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, [("ReportViewer", [a.ProjectId])], readOn: [a.ProjectId, b.ProjectId])
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(RowsOf(a.SnapshotId)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(BookOf(a.SnapshotId)).ConfigureAwait(true)).StatusCode);

        // Чужий — той самий 404, що й неіснуючий (у B немає й права переліку).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(RowsOf(b.SnapshotId)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BookOf(b.SnapshotId)).ConfigureAwait(true)).StatusCode);

        // І перелік без фільтра зрізів B не показує.
        var list = await client.GetAsync(new Uri("/api/v1/reports/snapshots", UriKind.Relative)).ConfigureAwait(true);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true));
        var ids = listed.RootElement.EnumerateArray().Select(s => s.GetProperty("id").GetInt64()).ToList();
        Assert.Contains(a.SnapshotId, ids);
        Assert.DoesNotContain(b.SnapshotId, ids);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Переглядач_звітів_без_гранта_Read_не_бачить_зрізу()
    {
        // Право без гранта — «немає проєкту», той самий 404.
        var (a, _) = await TwoSnapshotsAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, [("ReportViewer", null)], readOn: []).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(RowsOf(a.SnapshotId)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BookOf(a.SnapshotId)).ConfigureAwait(true)).StatusCode);
    }

    private static Uri RowsOf(long snapshotId)
        => new($"/api/v1/reports/snapshots/{snapshotId}/rows?limit=10", UriKind.Relative);

    private static Uri BookOf(long snapshotId)
        => new($"/api/v1/reports/snapshots/{snapshotId}/export.xlsx", UriKind.Relative);

    private static async Task AssertForbiddenAsync(HttpResponseMessage response, string permission)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}");
        Assert.Contains("ECR-AUTH-0403", body, StringComparison.Ordinal);
        Assert.Contains(permission, body, StringComparison.Ordinal);
    }

    private sealed record Snapshot(int ProjectId, long SnapshotId);

    /// <summary>Два проєкти, у кожному — зріз з одним рядком.</summary>
    private async Task<(Snapshot A, Snapshot B)> TwoSnapshotsAsync()
        => (await SnapshotAsync().ConfigureAwait(false), await SnapshotAsync().ConfigureAwait(false));

    private async Task<Snapshot> SnapshotAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        var version = await db.ReportVersions.AsNoTracking().OrderBy(v => v.Id).FirstAsync().ConfigureAwait(false);
        var snapshot = new ReportSnapshot(
            version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Draft, DateTime.UtcNow, null);
        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var cell = new ReportRow(snapshot.Id, 1, "OutputCode");
        cell.SetValue("E_CO2", null, null);
        db.ReportRows.Add(cell);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Snapshot(document.ProjectId, snapshot.Id);
    }

    /// <summary>
    /// Користувач із вбудованими ролями (за потреби — з областю) і окремою
    /// власною роллю БЕЗ прав, що дає лише грант Read на <paramref name="readOn"/>.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, (string Code, int[]? Scope)[] builtIn, int[] readOn)
    {
        var name = $"rv_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var reader = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Report viewer test: read grant"));
            db.Roles.Add(reader);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var project in readOn)
            {
                db.ResourceGrants.Add(new ResourceGrant(reader.Id, ResourceKind.Project, project, GrantLevel.Read));
            }

            db.RoleAssignments.Add(new RoleAssignment(reader.Id, user.Id, principalSid: null));

            foreach (var (code, scope) in builtIn)
            {
                var assignment = new RoleAssignment(await RoleIdAsync(code).ConfigureAwait(false), user.Id, principalSid: null);
                if (scope is not null)
                {
                    assignment.SetScope(RoleAssignmentScope.Create(scope));
                }

                db.RoleAssignments.Add(assignment);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return await LoginAsync(app, name).ConfigureAwait(false);
    }

    /// <summary>Власна роль із переліком прав і грантом Read на проєкт.</summary>
    private async Task<HttpClient> SignedInCustomAsync(EcrApiFactory app, int projectId, params string[] permissions)
    {
        var name = $"rvc_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Report viewer test: custom"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return await LoginAsync(app, name).ConfigureAwait(false);
    }

    private static async Task<HttpClient> LoginAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private async Task<int> RoleIdAsync(string roleCode)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM sec.Role WHERE Code = @code AND IsBuiltIn = 1";
        command.Parameters.AddWithValue("@code", roleCode);
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
