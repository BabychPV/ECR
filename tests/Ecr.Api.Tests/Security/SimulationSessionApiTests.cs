// tests/Ecr.Api.Tests/Security/SimulationSessionApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// V-06 (UX-прохід, третій раунд): «View as» відкривав сеанс в
/// <c>aud.SimulationSession</c>, але <c>/me</c> лишався адміністратором з усіма
/// правами, і сеанс ніхто не закривав. За задумом (<c>ФВ-6.16a</c>, <c>D-96</c>):
/// під сеансом — права суб'єкта, лише читання, ознака для банера, завершення
/// закриває сеанс. Наскрізно: справжній SQL, вхід, cookie, HTTP.
/// </summary>
[Collection("SqlServer")]
public sealed class SimulationSessionApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Simulation-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Під_сеансом_права_суб_єкта_лише_читання_а_завершення_закриває_сеанс()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative))).StatusCode);

        var start = await client.PostAsJsonAsync(
            new Uri("/api/v1/security/simulation", UriKind.Relative),
            new { subjectUserId = s.SubjectId, reason = "V-06 check" }).ConfigureAwait(true);
        var startBody = await start.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(start.StatusCode == HttpStatusCode.Created, $"{start.StatusCode}: {startBody}\n{app.ErrorsText}");
        var sessionId = JsonDocument.Parse(startBody).RootElement.GetProperty("sessionId").GetInt64();

        // ⛔ Головне: `/me` — це ТЕПЕР суб'єкт, а не адміністратор.
        var me = await MeAsync(client).ConfigureAwait(true);
        Assert.True(me.GetProperty("isSimulation").GetBoolean());
        Assert.Equal(s.SubjectId, me.GetProperty("simulatedForUserId").GetInt32());
        Assert.Equal($"Subject {_tag}", me.GetProperty("simulatedForUserName").GetString());
        Assert.Equal(sessionId, me.GetProperty("simulationSessionId").GetInt64());
        Assert.Equal("Document.View", Assert.Single(me.GetProperty("permissions").EnumerateArray()).GetString());

        // Права суб'єкта діють на сервері, а не лише в `/me`.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative))).StatusCode);

        // Лише читання: запис відхиляється навіть там, де права актора дозволили б.
        var write = await client.PostAsJsonAsync(
            new Uri("/api/v1/roles", UriKind.Relative),
            new { code = $"SIMW_{_tag}", nameL10n = new Dictionary<string, string> { ["en"] = "x" }, permissionCodes = Array.Empty<string>() })
            .ConfigureAwait(true);
        var writeBody = await write.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(write.StatusCode == HttpStatusCode.Forbidden, $"{write.StatusCode}: {writeBody}");
        Assert.Equal("ECR-SIM-0403", JsonDocument.Parse(writeBody).RootElement.GetProperty("errorCode").GetString());

        Assert.Null(await EndedAtAsync(sessionId).ConfigureAwait(true));

        // Завершення — без номера сеансу: сервер знає сеанс цього входу.
        var end = await client.DeleteAsync(new Uri("/api/v1/security/simulation", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(end.StatusCode == HttpStatusCode.NoContent, $"{end.StatusCode}: {await end.Content.ReadAsStringAsync().ConfigureAwait(true)}");

        Assert.NotNull(await EndedAtAsync(sessionId).ConfigureAwait(true));

        var after = await MeAsync(client).ConfigureAwait(true);
        Assert.False(after.GetProperty("isSimulation").GetBoolean());
        Assert.Contains("Security.Simulate", after.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вихід_із_системи_закриває_відкритий_сеанс()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var start = await client.PostAsJsonAsync(
            new Uri("/api/v1/security/simulation", UriKind.Relative),
            new { subjectUserId = s.SubjectId, reason = "V-06 logout" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var sessionId = JsonDocument.Parse(await start.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("sessionId").GetInt64();

        var logout = await client.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), content: null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.NotNull(await EndedAtAsync(sessionId).ConfigureAwait(true));
    }

    private static async Task<JsonElement> MeAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"/me: {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<DateTime?> EndedAtAsync(long sessionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EndedAt FROM aud.SimulationSession WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", sessionId);
        var raw = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return raw is null or DBNull ? null : (DateTime)raw;
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"sima_{_tag}", password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<(int AdminId, int SubjectId)> ArrangeAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var admin = new User($"sima_{_tag}", $"Admin {_tag}", AuthProvider.Local);
        admin.SetPassword(new PasswordHasher().Hash(Password));
        var subject = new User($"sims_{_tag}", $"Subject {_tag}", AuthProvider.Local);
        subject.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.AddRange(admin, subject);

        var adminRole = NewRole($"SIMA_{_tag}");
        var subjectRole = NewRole($"SIMS_{_tag}");
        db.Roles.AddRange(adminRole, subjectRole);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(adminRole.Id, "Security.Simulate"));
        db.RolePermissions.Add(new RolePermission(adminRole.Id, "Security.ManageRoles"));
        db.RolePermissions.Add(new RolePermission(subjectRole.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(adminRole.Id, admin.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(subjectRole.Id, subject.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (admin.Id, subject.Id);
    }

    private static Role NewRole(string code)
        => new(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
}
