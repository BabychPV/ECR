// tests/Ecr.Api.Tests/Security/RolePermissionsEditApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// (v) рішення людини 08.11 («чому я не можу редагувати роль?» → «так»): права ВЛАСНОЇ ролі правляться —
/// спершу попередній перегляд впливу, окреме підтвердження небезпечних прав, аудит, 409 на конкурентну правку;
/// вбудовані ролі — під замком.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ (кожен тест червоний без свого фіксу — на 0f892cdab маршрутів немає зовсім, 404/405):
/// <list type="bullet">
/// <item>прибрати перевірку <c>IsBuiltIn</c> у <c>RolePermissionRules.RequireEditableAsync</c> — червоніє «вбудована — 409»;</item>
/// <item>прибрати перевірку <c>confirmDangerous</c> — червоніє «небезпечне без підтвердження — 409»;</item>
/// <item>прибрати звірку версії — червоніють «застаріла версія — 409» і «дві правки — одна 409»;</item>
/// <item>прибрати <c>RolePermissionsChanged</c> — червоніє аудит;</item>
/// <item>прибрати <c>RotateStampsForRoleAsync</c> — червоніє «носій отримує нові права».</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RolePermissionsEditApiTests(SqlServerFixture sql)
{
    private const string Password = "Role-Perm-Edit-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Попередній_перегляд_показує_додані_прибрані_і_доступ_носіїв_до_після()
    {
        var (safeA, safeB, safeC, _) = await CatalogAsync().ConfigureAwait(true);

        // Роль: A, B. Носій має ще іншу роль з B — тож прибрання B йому нічого не знімає.
        var role = await CustomRoleAsync(safeA, safeB).ConfigureAwait(true);
        var other = await CustomRoleAsync(safeB).ConfigureAwait(true);
        var carrier = await UserWithRolesAsync(role.Id, other.Id).ConfigureAwait(true);
        var groupSid = $"S-1-5-21-{Random.Shared.Next(1, int.MaxValue)}-1108-1";
        await GroupAssignmentAsync(role.Id, groupSid).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAdminAsync(app).ConfigureAwait(true);

        var (status, body) = await PreviewAsync(admin, role.Id, [safeC]).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}\n{body}\n{app.ErrorsText}");

        var preview = JsonDocument.Parse(body).RootElement;
        Assert.Equal(RolePermissionsVersion.Of([safeA, safeB]), preview.GetProperty("version").GetString());
        Assert.Equal([safeC], Strings(preview.GetProperty("added")));
        Assert.Equal(new[] { safeA, safeB }.Order(StringComparer.Ordinal), Strings(preview.GetProperty("removed")));

        var user = preview.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("userId").GetInt32() == carrier);
        Assert.Equal([safeC], Strings(user.GetProperty("gained")));
        Assert.Equal([safeA], Strings(user.GetProperty("lost")));
        Assert.Equal(2, user.GetProperty("effectiveBefore").GetInt32());
        Assert.Equal(2, user.GetProperty("effectiveAfter").GetInt32());

        Assert.Contains(
            preview.GetProperty("groups").EnumerateArray(),
            g => g.GetProperty("sid").GetString() == groupSid);

        // Перегляд нічого не змінює.
        Assert.Equal(new[] { safeA, safeB }.Order(StringComparer.Ordinal), await PermissionsAsync(role.Id).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Вбудована_роль_під_замком_409_і_на_перегляд_і_на_збереження()
    {
        var (safeA, _, _, _) = await CatalogAsync().ConfigureAwait(true);
        var builtIn = await BuiltInRoleAsync().ConfigureAwait(true);
        var before = await PermissionsAsync(builtIn).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAdminAsync(app).ConfigureAwait(true);

        var (status, body) = await PreviewAsync(admin, builtIn, [safeA]).ConfigureAwait(true);
        AssertProblem(status, body, HttpStatusCode.Conflict, "ECR-SEC-0409", "err.ECR-SEC-0409.roleBuiltInPermissions");

        var put = await PutAsync(admin, builtIn, [safeA], RolePermissionsVersion.Of(before), confirm: true).ConfigureAwait(true);
        AssertProblem(put.Status, put.Body, HttpStatusCode.Conflict, "ECR-SEC-0409", "err.ECR-SEC-0409.roleBuiltInPermissions");

        Assert.Equal(before, await PermissionsAsync(builtIn).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    public async Task Небезпечне_право_без_окремого_підтвердження_409_з_ним_204_і_слід()
    {
        var (safeA, _, _, dangerous) = await CatalogAsync().ConfigureAwait(true);
        var role = await CustomRoleAsync(safeA).ConfigureAwait(true);
        var version = RolePermissionsVersion.Of([safeA]);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAdminAsync(app).ConfigureAwait(true);

        var (_, previewBody) = await PreviewAsync(admin, role.Id, [safeA, dangerous]).ConfigureAwait(true);
        Assert.Equal([dangerous], Strings(JsonDocument.Parse(previewBody).RootElement.GetProperty("addedDangerous")));

        var refused = await PutAsync(admin, role.Id, [safeA, dangerous], version, confirm: false).ConfigureAwait(true);
        AssertProblem(refused.Status, refused.Body, HttpStatusCode.Conflict, "ECR-SEC-0409",
            "err.ECR-SEC-0409.dangerousPermissionsNeedConfirmation");
        Assert.Equal([safeA], await PermissionsAsync(role.Id).ConfigureAwait(true));

        var accepted = await PutAsync(admin, role.Id, [safeA, dangerous], version, confirm: true).ConfigureAwait(true);
        Assert.True(accepted.Status == HttpStatusCode.NoContent, $"{(int)accepted.Status}\n{accepted.Body}\n{app.ErrorsText}");
        Assert.Equal(new[] { safeA, dangerous }.Order(StringComparer.Ordinal), await PermissionsAsync(role.Id).ConfigureAwait(true));
        Assert.Contains(dangerous, await EventDetailsAsync("DangerousPermissionsGranted", role.Id).ConfigureAwait(true), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Дві_правки_з_однієї_версії_друга_409_permissionsStale_а_не_500_без_IfMatch_422()
    {
        var (safeA, safeB, safeC, _) = await CatalogAsync().ConfigureAwait(true);
        var role = await CustomRoleAsync(safeA).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var first = await SignedInAdminAsync(app).ConfigureAwait(true);
        using var second = await SignedInAdminAsync(app).ConfigureAwait(true);

        // Обидва відкрили перегляд з тієї самої версії.
        var version = JsonDocument.Parse((await PreviewAsync(first, role.Id, [safeA, safeB]).ConfigureAwait(true)).Body)
            .RootElement.GetProperty("version").GetString()!;
        Assert.Equal(version, JsonDocument.Parse((await PreviewAsync(second, role.Id, [safeA, safeC]).ConfigureAwait(true)).Body)
            .RootElement.GetProperty("version").GetString());

        var ok = await PutAsync(first, role.Id, [safeA, safeB], version, confirm: false).ConfigureAwait(true);
        Assert.True(ok.Status == HttpStatusCode.NoContent, $"{(int)ok.Status}\n{ok.Body}\n{app.ErrorsText}");
        Assert.Equal(RolePermissionsVersion.Of([safeA, safeB]), ok.ETag);

        var stale = await PutAsync(second, role.Id, [safeA, safeC], version, confirm: false).ConfigureAwait(true);
        AssertProblem(stale.Status, stale.Body, HttpStatusCode.Conflict, "ECR-SEC-0409", "err.ECR-SEC-0409.permissionsStale");
        Assert.Equal(new[] { safeA, safeB }.Order(StringComparer.Ordinal), await PermissionsAsync(role.Id).ConfigureAwait(true));

        var noHeader = await PutAsync(second, role.Id, [safeA], ifMatch: null, confirm: false).ConfigureAwait(true);
        AssertProblem(noHeader.Status, noHeader.Body, HttpStatusCode.UnprocessableEntity, "ECR-REQ-0422",
            "err.ECR-REQ-0422.rolePermissionsIfMatch");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Збереження_пише_аудит_хто_роль_додано_прибрано_і_носій_отримує_нові_права()
    {
        var (safeA, _, _, _) = await CatalogAsync().ConfigureAwait(true);

        // `Security.ManageRoles` дає `GET /roles`: носій без нього — 403, з ним після збереження — 200.
        const string grantsListing = "Security.ManageRoles";
        var role = await CustomRoleAsync(safeA).ConfigureAwait(true);
        var (carrierName, carrierId) = await CarrierAsync(role.Id).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAdminAsync(app).ConfigureAwait(true);
        using var carrier = await SignInAsync(app, carrierName).ConfigureAwait(true);

        var denied = await carrier.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var stampBefore = await StampAsync(carrierId).ConfigureAwait(true);

        // ⚠ `Security.ManageRoles` у каталозі небезпечне — підтвердження обов'язкове.
        var put = await PutAsync(admin, role.Id, [grantsListing], RolePermissionsVersion.Of([safeA]), confirm: true).ConfigureAwait(true);
        Assert.True(put.Status == HttpStatusCode.NoContent, $"{(int)put.Status}\n{put.Body}\n{app.ErrorsText}");

        // Аудит: хто, роль, що додано й прибрано.
        var (actor, details) = await LastChangeAsync(role.Id).ConfigureAwait(true);
        Assert.Equal(await UserIdAsync(admin).ConfigureAwait(true), actor);
        var json = JsonDocument.Parse(details).RootElement;
        Assert.Equal(role.Code, json.GetProperty("role").GetString());
        Assert.Equal([grantsListing], Strings(json.GetProperty("added")));
        Assert.Equal([safeA], Strings(json.GetProperty("removed")));

        // Живий ефект за звичайним правилом сесії: штамп носія прокручено → стара cookie 401, новий вхід — нові права.
        Assert.NotEqual(stampBefore, await StampAsync(carrierId).ConfigureAwait(true));
        var stale = await carrier.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        using var again = await SignInAsync(app, carrierName).ConfigureAwait(true);
        var allowed = await again.GetAsync(new Uri("/api/v1/roles", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ───────────── helpers ─────────────

    private static void AssertProblem(HttpStatusCode status, string body, HttpStatusCode expected, string code, string messageKey)
    {
        Assert.True(status == expected, $"{(int)status}\n{body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
    }

    private static string[] Strings(JsonElement array)
        => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static async Task<(HttpStatusCode Status, string Body)> PreviewAsync(HttpClient client, int roleId, string[] codes)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/roles/{roleId}/permissions/preview", UriKind.Relative),
            new { permissionCodes = codes }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<(HttpStatusCode Status, string Body, string? ETag)> PutAsync(
        HttpClient client, int roleId, string[] codes, string? ifMatch, bool confirm)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/roles/{roleId}/permissions", UriKind.Relative))
        {
            Content = JsonContent.Create(new { permissionCodes = codes, confirmDangerous = confirm }),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        var response = await client.SendAsync(request).ConfigureAwait(false);

        return (response.StatusCode,
                await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                response.Headers.ETag?.Tag.Trim('"'));
    }

    /// <summary>Три безпечні й одне небезпечне право каталогу.</summary>
    private async Task<(string SafeA, string SafeB, string SafeC, string Dangerous)> CatalogAsync()
    {
        await using var db = Context();
        var safe = await db.Permissions.AsNoTracking()
            .Where(p => !p.IsDangerous && !p.Id.StartsWith("Security."))
            .OrderBy(p => p.Id).Select(p => p.Id).Take(3).ToListAsync().ConfigureAwait(false);
        var dangerous = await db.Permissions.AsNoTracking()
            .Where(p => p.IsDangerous).OrderBy(p => p.Id).Select(p => p.Id).FirstAsync().ConfigureAwait(false);

        return (safe[0], safe[1], safe[2], dangerous);
    }

    private async Task<Role> CustomRoleAsync(params string[] permissions)
    {
        await using var db = Context();
        var role = new Role(EcrCode.Create($"RP{Guid.NewGuid():N}"[..12]), Name("perm-edit"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var code in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, code));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return role;
    }

    private async Task<int> BuiltInRoleAsync()
    {
        await using var db = Context();

        return await db.Roles.AsNoTracking().Where(r => r.IsBuiltIn).OrderBy(r => r.Id).Select(r => r.Id).FirstAsync().ConfigureAwait(false);
    }

    private async Task<string[]> PermissionsAsync(int roleId)
    {
        await using var db = Context();

        return [.. (await db.RolePermissions.AsNoTracking().Where(rp => rp.RoleId == roleId)
            .Select(rp => rp.PermissionCode).ToListAsync().ConfigureAwait(false)).Order(StringComparer.Ordinal)];
    }

    private async Task<int> UserWithRolesAsync(params int[] roleIds)
    {
        await using var db = Context();
        var name = $"rpu_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var roleId in roleIds)
        {
            db.RoleAssignments.Add(new RoleAssignment(roleId, user.Id, principalSid: null));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }

    private async Task<(string Name, int Id)> CarrierAsync(int roleId)
    {
        var id = await UserWithRolesAsync(roleId).ConfigureAwait(false);
        await using var db = Context();

        return (await db.Users.Where(u => u.Id == id).Select(u => u.UserName).FirstAsync().ConfigureAwait(false), id);
    }

    private async Task GroupAssignmentAsync(int roleId, string sid)
    {
        await using var db = Context();
        db.RoleAssignments.Add(new RoleAssignment(roleId, userId: null, principalSid: sid));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<string> StampAsync(int userId)
    {
        await using var db = Context();

        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).FirstAsync().ConfigureAwait(false);
    }

    private static async Task<int> UserIdAsync(HttpClient client)
    {
        var me = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(false));

        return me.RootElement.GetProperty("userId").GetInt32();
    }

    private async Task<string> EventDetailsAsync(string eventType, int roleId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (1) DetailsJson FROM aud.SecurityEvent WHERE EventType = @t AND TargetRoleId = @r ORDER BY Id DESC;";
        command.Parameters.AddWithValue("@t", eventType);
        command.Parameters.AddWithValue("@r", roleId);

        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false) ?? string.Empty;
    }

    private async Task<(int Actor, string Details)> LastChangeAsync(int roleId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) ChangedByUserId, DetailsJson FROM aud.SecurityEvent
             WHERE EventType = N'RolePermissionsChanged' AND TargetRoleId = @r ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("@r", roleId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false), "події RolePermissionsChanged немає");

        return (reader.GetInt32(0), reader.GetString(1));
    }

    private async Task<HttpClient> SignedInAdminAsync(EcrApiFactory app)
    {
        var name = $"rpA_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("perm-edit admin"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageRoles"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return await SignInAsync(app, name).ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
