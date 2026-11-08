// tests/Ecr.Api.Tests/Security/ExpiredRoleReassignApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// P1 (людина 08.11, кореляція e16184de): Security → користувач → додати роль, чия строкова
/// підміна СПЛИЛА, → Save давав «Internal error». <c>PUT /users/{id}/roles</c> вставляв друге
/// призначення (user, role) поруч із наявним сплилим рядком, і <c>UQ_RoleAssignment_User</c>
/// падав голим 500.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ (червоне до фіксу, перевірено на 602ef60bd):
/// <list type="bullet">
/// <item>вставка нового рядка замість оновлення наявного в <c>UserStore.ReplaceRolesAsync</c> —
/// червоніють обидва тести «сплила роль» (500 замість 200);</item>
/// <item>без поля <c>validity</c> у <c>UserRolesReplaced</c> (<c>ReplaceUserRolesHandler</c>) —
/// червоніють твердження аудиту «старі → нові»;</item>
/// <item>без арму <c>UQ_RoleAssignment_User</c> в <c>ExceptionHandlingMiddleware</c> — тест
/// конфлікту дає 500 замість 409.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class ExpiredRoleReassignApiTests(SqlServerFixture sql)
{
    private const string Password = "Role-Reassign-Exp-2026!";

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Сплила_роль_призначається_знову_200_один_рядок_безстроково_і_аудит_старі_нові()
    {
        var expiredTo = Today.AddDays(-13);
        var (target, roleId, code) = await TargetWithDatedRoleAsync(null, expiredTo).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app).ConfigureAwait(true);

        var response = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/users/{target}/roles", UriKind.Relative),
            new { roleCodes = new[] { code } }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var rows = await RowsAsync(target, roleId).ConfigureAwait(true);
        var row = Assert.Single(rows);
        Assert.Null(row.ValidFrom);
        Assert.Null(row.ValidTo);

        var change = await ValidityChangeAsync(target, code).ConfigureAwait(true);
        Assert.Equal(expiredTo.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            change.GetProperty("from").GetProperty("validTo").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("to").GetProperty("validTo").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Сплила_роль_з_новими_межами_оновлює_той_самий_рядок()
    {
        var expiredTo = Today.AddDays(-5);
        var newTo = Today.AddDays(30);
        var (target, roleId, code) = await TargetWithDatedRoleAsync(Today.AddDays(-40), expiredTo).ConfigureAwait(true);
        var idBefore = Assert.Single(await RowsAsync(target, roleId).ConfigureAwait(true)).Id;

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app).ConfigureAwait(true);

        var response = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/users/{target}/roles", UriKind.Relative),
            new
            {
                roleCodes = new[] { code },
                validity = new Dictionary<string, object> { [code] = new { validFrom = (string?)null, validTo = Iso(newTo) } },
            }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var row = Assert.Single(await RowsAsync(target, roleId).ConfigureAwait(true));
        Assert.Equal(idBefore, row.Id);
        Assert.Null(row.ValidFrom);
        Assert.Equal(newTo, row.ValidTo);

        var change = await ValidityChangeAsync(target, code).ConfigureAwait(true);
        Assert.Equal(Iso(Today.AddDays(-40)), change.GetProperty("from").GetProperty("validFrom").GetString());
        Assert.Equal(Iso(expiredTo), change.GetProperty("from").GetProperty("validTo").GetString());
        Assert.Equal(Iso(newTo), change.GetProperty("to").GetProperty("validTo").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Правка_меж_чинної_ролі_пишеться_в_аудит_старі_нові()
    {
        var oldTo = Today.AddDays(10);
        var newTo = Today.AddDays(20);
        var (target, roleId, code) = await TargetWithDatedRoleAsync(null, oldTo).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app).ConfigureAwait(true);

        var response = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/users/{target}/roles", UriKind.Relative),
            new
            {
                roleCodes = new[] { code },
                validity = new Dictionary<string, object> { [code] = new { validTo = Iso(newTo) } },
            }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        Assert.Equal(newTo, Assert.Single(await RowsAsync(target, roleId).ConfigureAwait(true)).ValidTo);

        var change = await ValidityChangeAsync(target, code).ConfigureAwait(true);
        Assert.Equal(Iso(oldTo), change.GetProperty("from").GetProperty("validTo").GetString());
        Assert.Equal(Iso(newTo), change.GetProperty("to").GetProperty("validTo").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порушення_UQ_RoleAssignment_User_віддається_як_409_ECR_SEC_0409_а_не_500()
    {
        var (target, roleId, _) = await TargetWithDatedRoleAsync(null, Today.AddDays(-1)).ConfigureAwait(true);

        // Справжнє порушення індексу на справжній базі — те саме, що давав живий 500.
        DbUpdateException? failure = null;
        await using (var db = Context())
        {
            db.RoleAssignments.Add(new RoleAssignment(roleId, target, principalSid: null));
            try
            {
                await db.SaveChangesAsync().ConfigureAwait(true);
            }
            catch (DbUpdateException ex)
            {
                failure = ex;
            }
        }

        Assert.NotNull(failure);
        Assert.Contains("UQ_RoleAssignment_User", failure.GetBaseException().Message, StringComparison.Ordinal);

        var problem = JsonDocument.Parse(await ProblemReservedMembersTests.ProblemTextAsync(failure)).RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-SEC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-SEC-0409.roleAssignmentConflict", problem.GetProperty("messageKey").GetString());
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Користувач з одним строковим призначенням ролі.</summary>
    private async Task<(int UserId, int RoleId, string Code)> TargetWithDatedRoleAsync(DateOnly? from, DateOnly to)
    {
        await using var db = Context();

        var name = $"rre_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("reassign-expired"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var dated = new RoleAssignment(role.Id, user.Id, principalSid: null);
        dated.SetValidity(from, to);
        db.RoleAssignments.Add(dated);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, role.Id, role.Code);
    }

    private async Task<List<RoleAssignment>> RowsAsync(int userId, int roleId)
    {
        await using var db = Context();

        return await db.RoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.RoleId == roleId)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Поле <c>validity[code]</c> останньої події <c>UserRolesReplaced</c> цього користувача.</summary>
    private async Task<JsonElement> ValidityChangeAsync(int userId, string code)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) DetailsJson FROM aud.SecurityEvent
             WHERE EventType = N'UserRolesReplaced' AND TargetUserId = @user
             ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("@user", userId);

        var json = (string?)await command.ExecuteScalarAsync().ConfigureAwait(false);
        Assert.False(json is null, "події UserRolesReplaced немає");

        var root = JsonDocument.Parse(json!).RootElement;
        Assert.True(root.TryGetProperty("validity", out var validity), $"у події немає validity: {json}");
        Assert.True(validity.TryGetProperty(code, out var change), $"у події немає validity.{code}: {json}");

        return change;
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"rreA_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("reassign-expired admin"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageUsers"));
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

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
