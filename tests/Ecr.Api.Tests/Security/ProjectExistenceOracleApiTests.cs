// tests/Ecr.Api.Tests/Security/ProjectExistenceOracleApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S17 аудиту безпеки: невидимий існуючий проєкт (і його період) відповідає
/// ТАК САМО, як неіснуючий, — інакше різниця 404/403 є оракулом існування.
/// </summary>
/// <remarks>
/// ⛔ Доказ на HTTP: статус ставить <c>ExceptionHandlingMiddleware</c>, а тіло —
/// той самий конвеєр; обробниковий тест бачив би лише тип винятку.
///
/// ⚠ Порівнюється статус, <c>errorCode</c>, <c>messageKey</c> і <c>title</c>
/// для кожної групи дій. Ідентифікатор у тексті відповіді відрізняється за
/// побудовою (різні id), тож текст не порівнюється.
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectExistenceOracleApiTests(SqlServerFixture sql)
{
    private const string Password = "Oracle-Probe-2026!";

    /// <summary>Id, якого в базі тесту немає.</summary>
    private const int Missing = int.MaxValue - 17;

    private static readonly string[] Permissions =
        ["Project.Manage", "Period.Configure", "Period.Reopen", "Document.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S17")]
    public async Task S17_невидимий_проєкт_і_період_відповідають_як_неіснуючі()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(ct: CancellationToken.None).ConfigureAwait(true);
        var periodId = await PeriodIdAsync(chain.ProjectId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // Усі права є, гранта на проєкт — жодного: проєкт для людини невидимий.
        using var stranger = await SignedInAsync(app, grantOn: null).ConfigureAwait(true);

        foreach (var (name, send) in Actions())
        {
            using var invisible = await send(stranger, chain.ProjectId, periodId).ConfigureAwait(true);
            using var missing = await send(stranger, Missing, Missing).ConfigureAwait(true);

            var a = await ShapeAsync(invisible).ConfigureAwait(true);
            var b = await ShapeAsync(missing).ConfigureAwait(true);

            Assert.True(a == b, $"{name}: невидимий {a} ≠ неіснуючий {b}. {app.ErrorsText}");
            Assert.True(a.Status == HttpStatusCode.NotFound, $"{name}: {a}. {app.ErrorsText}");
        }

        // Контроль: ВИДИМИЙ проєкт (грант Read) без рівня Manage — і далі 403 із
        // причиною. Інакше тест доводив би лише, що маршрути відповідають 404 завжди.
        using var reader = await SignedInAsync(app, grantOn: chain.ProjectId).ConfigureAwait(true);

        using var activate = await reader.PostAsync(
            new Uri($"/api/v1/projects/{chain.ProjectId}/activate", UriKind.Relative), content: null)
            .ConfigureAwait(true);
        var denied = await ShapeAsync(activate).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", denied.MessageKey);

        using var calendar = await reader.GetAsync(
            new Uri($"/api/v1/projects/{chain.ProjectId}/periods", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(calendar.StatusCode == HttpStatusCode.OK, $"{calendar.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Групи дій над проєктом і періодом.</summary>
    private static IEnumerable<(string Name, Func<HttpClient, int, int, Task<HttpResponseMessage>> Send)> Actions()
    {
        yield return ("GET periods", (c, p, _) => c.GetAsync(Url($"/api/v1/projects/{p}/periods")));
        yield return ("POST activate", (c, p, _) => c.PostAsync(Url($"/api/v1/projects/{p}/activate"), null));
        yield return ("POST archive", (c, p, _) => c.PostAsync(Url($"/api/v1/projects/{p}/archive"), null));
        yield return ("POST clone", (c, p, _) => c.PostAsJsonAsync(
            Url($"/api/v1/projects/{p}/clone"), new { code = $"CLN{Guid.NewGuid():N}"[..12] }));
        yield return ("PUT current-period", (c, p, _) => c.PutAsJsonAsync(
            Url($"/api/v1/projects/{p}/current-period"), new { pinnedPeriodId = (int?)null, reason = (string?)null }));
        yield return ("PUT timezone", (c, p, _) => c.PutAsJsonAsync(
            Url($"/api/v1/projects/{p}/timezone"), new { timeZoneId = "Asia/Aqtau" }));
        yield return ("GET approval-route", (c, p, _) => c.GetAsync(Url($"/api/v1/projects/{p}/approval-route")));
        yield return ("PUT approval-route", (c, p, _) => c.PutAsJsonAsync(
            Url($"/api/v1/projects/{p}/approval-route"), new { roleIds = Array.Empty<int>() }));
        yield return ("POST period reopen", (c, _, period) => c.PostAsJsonAsync(
            Url($"/api/v1/periods/{period}/reopen"), new { reason = "S17 probe", until = (DateTime?)null }));
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>Те, що бачить клієнт і за чим можна відрізнити відповіді.</summary>
    private sealed record Shape(HttpStatusCode Status, string? ErrorCode, string? MessageKey, string? Title);

    private static async Task<Shape> ShapeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Shape(response.StatusCode, null, null, null);
        }

        var root = JsonDocument.Parse(text).RootElement;
        return new Shape(response.StatusCode, Str(root, "errorCode"), Str(root, "messageKey"), Str(root, "title"));
    }

    private static string? Str(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) ? v.ToString() : null;

    private async Task<int> PeriodIdAsync(int projectId)
    {
        await using var db = NewDb();
        return await db.Periods.Where(p => p.ProjectId == projectId).Select(p => p.Id).FirstAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Користувач з усіма правами дій; грант Read — лише коли задано проєкт.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, int? grantOn)
    {
        var name = $"s17_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "S17 oracle probe" }));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in Permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));

            if (grantOn is { } projectId)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
