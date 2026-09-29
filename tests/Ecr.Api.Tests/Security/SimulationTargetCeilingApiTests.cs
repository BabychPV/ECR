// tests/Ecr.Api.Tests/Security/SimulationTargetCeilingApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// D-210: ціль «View as» не може бути bootstrap-адміністратором і не може мати
/// жодного небезпечного права (<c>sec.Permission.IsDangerous = 1</c>) — з будь-якої
/// особистої ролі, з областю чи без. Наскрізно: справжній SQL, вхід, cookie, HTTP.
/// </summary>
/// <remarks>
/// ⛔ Відмова мусить бути ДО запису сеансу: інакше в <c>aud.SimulationSession</c>
/// лишився б відкритий сеанс перегляду чужих небезпечних прав, а cookie — ні.
/// Спроба пишеться незалежною подією <c>SimulationDenied</c>.
///
/// ⚠ Групові призначення цілі тут не перевіряються і не можуть: членство в групах
/// відоме лише з квитка ВЛАСНОЇ сесії (<c>P-02</c>), а профіль сеансу симуляції
/// будується без груп суб'єкта — групові права через «View as» не видно взагалі.
/// </remarks>
[Collection("SqlServer")]
public sealed class SimulationTargetCeilingApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-SimCeiling-2026!";
    private const string DeniedEvent = StartSimulationHandler.DeniedEvent;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Небезпечне право з особистої ролі — без області, з областю (глобальне і
    /// проєктне право) і з майбутнього строкового призначення.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("Security.ManageUsers", false, false)]
    [InlineData("Security.ManageUsers", true, false)]
    [InlineData("Period.Reopen", true, false)]
    [InlineData("Calculation.Publish", false, true)]
    public async Task Ціль_з_небезпечним_правом_дає_403_ECR_SIM_4031_без_сеансу(
        string permission, bool scoped, bool future)
    {
        var s = await ArrangeAsync(new Target(permission, scoped, future ? Validity.Future : Validity.Permanent))
            .ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var start = await StartAsync(client, s.SubjectId).ConfigureAwait(true);

        await AssertDeniedAsync(start, "err.ECR-SIM-4031.dangerousTarget", app).ConfigureAwait(true);
        Assert.Equal(0, await SessionsAsync(s.AdminId, s.SubjectId).ConfigureAwait(true));
        Assert.Equal(1, await DeniedEventsAsync(s.AdminId, s.SubjectId).ConfigureAwait(true));

        // Відмова не підміняє профіль актора: він лишається собою.
        var me = await MeAsync(client).ConfigureAwait(true);
        Assert.False(me.GetProperty("isSimulation").GetBoolean());
    }

    /// <summary>Роль первинного налаштування (<c>BootstrapAdministrator</c>).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ціль_із_роллю_BootstrapAdministrator_дає_403_bootstrapTarget()
    {
        var s = await ArrangeAsync(new Target(null, false, Validity.Permanent, BootstrapRole: true))
            .ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var start = await StartAsync(client, s.SubjectId).ConfigureAwait(true);

        await AssertDeniedAsync(start, "err.ECR-SIM-4031.bootstrapTarget", app).ConfigureAwait(true);
        Assert.Equal(0, await SessionsAsync(s.AdminId, s.SubjectId).ConfigureAwait(true));
        Assert.Equal(1, await DeniedEventsAsync(s.AdminId, s.SubjectId).ConfigureAwait(true));
    }

    /// <summary>
    /// Звичайна ціль — сеанс відкривається; прострочена підміна з небезпечним правом
    /// не блокує (вона вже нічого не дає і в профіль не входить).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ціль_без_небезпечних_прав_і_з_простроченою_підміною_відкривається()
    {
        var s = await ArrangeAsync(new Target("Security.ManageUsers", false, Validity.Expired))
            .ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var start = await StartAsync(client, s.SubjectId).ConfigureAwait(true);
        var body = await start.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(start.StatusCode == HttpStatusCode.Created, $"{start.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(1, await SessionsAsync(s.AdminId, s.SubjectId).ConfigureAwait(true));
        Assert.Equal(0, await DeniedEventsAsync(s.AdminId, s.SubjectId).ConfigureAwait(true));
    }

    private static async Task AssertDeniedAsync(HttpResponseMessage start, string messageKey, EcrApiFactory app)
    {
        var body = await start.Content.ReadAsStringAsync().ConfigureAwait(false);

        // ⛔ Саме 403: без арма в ExceptionHandlingMiddleware код доїхав би як 422.
        Assert.True(start.StatusCode == HttpStatusCode.Forbidden, $"{start.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-SIM-4031", problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, int subjectId)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/security/simulation", UriKind.Relative),
            new { subjectUserId = subjectId, reason = "D-210 check" });

    private static async Task<JsonElement> MeAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"/me: {response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> SessionsAsync(int actorId, int subjectId)
        => await ScalarAsync(
            "SELECT COUNT(*) FROM aud.SimulationSession WHERE ActorUserId = @actor AND SubjectUserId = @subject;",
            actorId, subjectId).ConfigureAwait(false);

    private async Task<int> DeniedEventsAsync(int actorId, int subjectId)
        => await ScalarAsync(
            $"SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'{DeniedEvent}' "
            + "AND ChangedByUserId = @actor AND TargetUserId = @subject;",
            actorId, subjectId).ConfigureAwait(false);

    private async Task<int> ScalarAsync(string sqlText, int actorId, int subjectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.Parameters.AddWithValue("@actor", actorId);
        command.Parameters.AddWithValue("@subject", subjectId);
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"scla_{_tag}", password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<(int AdminId, int SubjectId)> ArrangeAsync(Target target)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var admin = new User($"scla_{_tag}", $"Admin {_tag}", AuthProvider.Local);
        admin.SetPassword(new PasswordHasher().Hash(Password));
        var subject = new User($"scls_{_tag}", $"Subject {_tag}", AuthProvider.Local);
        subject.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.AddRange(admin, subject);

        var adminRole = NewRole($"SCLA_{_tag}");
        var plainRole = NewRole($"SCLS_{_tag}");
        db.Roles.AddRange(adminRole, plainRole);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(adminRole.Id, "Security.Simulate"));
        db.RolePermissions.Add(new RolePermission(plainRole.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(adminRole.Id, admin.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(plainRole.Id, subject.Id, null));

        if (target.Permission is { } permission)
        {
            var dangerousRole = NewRole($"SCLD_{_tag}");
            db.Roles.Add(dangerousRole);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(dangerousRole.Id, permission));
            db.RoleAssignments.Add(Assign(dangerousRole.Id, subject.Id, target));
        }

        if (target.BootstrapRole)
        {
            var bootstrapRoleId = await db.Roles
                .Where(r => r.Code == "BootstrapAdministrator")
                .Select(r => r.Id)
                .SingleAsync()
                .ConfigureAwait(false);
            db.RoleAssignments.Add(new RoleAssignment(bootstrapRoleId, subject.Id, null));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (admin.Id, subject.Id);
    }

    private static RoleAssignment Assign(int roleId, int userId, Target target)
    {
        var assignment = new RoleAssignment(roleId, userId, null);
        if (target.Scoped)
        {
            // Проєкт області існувати не мусить: область — JSON, а не зовнішній ключ.
            assignment.SetScope(RoleAssignmentScope.Create([987654]));
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        switch (target.Validity)
        {
            case Validity.Future:
                assignment.SetValidity(today.AddDays(10), today.AddDays(20));
                break;
            case Validity.Expired:
                assignment.SetValidity(today.AddDays(-20), today.AddDays(-10));
                break;
        }

        return assignment;
    }

    private static Role NewRole(string code)
        => new(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));

    private enum Validity
    {
        Permanent,
        Future,
        Expired,
    }

    private sealed record Target(string? Permission, bool Scoped, Validity Validity, bool BootstrapRole = false);
}
