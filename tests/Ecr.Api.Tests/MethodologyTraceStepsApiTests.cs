// tests/Ecr.Api.Tests/MethodologyTraceStepsApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
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
/// ✎ D-565 (PERF-03): <c>GET/PUT /api/v1/methodologies/{id}/trace-steps</c> на справжньому SQL — прапор вимкнено за
/// замовчуванням, вмикає право <c>Calculation.EditFormula</c>, зміна (і лише зміна) — у журналі структурних змін.
/// </summary>
/// <remarks>
/// ⛔ Мутації (локально): <c>Permission</c> обробника → <c>Calculation.View</c> — червоний 403-тест; не викликати
/// <c>SaveChangesAsync</c> — червоний перший (прапор не лишається в базі); писати журнал і на повторі того самого
/// значення — червоний «один запис»; <c>HasDefaultValue(true)</c> — червоний «за замовчуванням вимкнено».
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyTraceStepsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Meth-TraceSteps-2026!";

    private static readonly string[] EditorPermissions = ["Calculation.View", "Calculation.EditFormula"];
    private static readonly string[] ViewerPermissions = ["Calculation.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task За_замовчуванням_вимкнено_PUT_вмикає_і_вимикає_а_журнал_лише_на_зміну()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var id = await MethodologyAsync().ConfigureAwait(true);

        var initial = await ReadAsync(await client.GetAsync(Url(id)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(id, initial.GetProperty("methodologyId").GetInt32());
        Assert.False(initial.GetProperty("traceSteps").GetBoolean());
        Assert.False(await StoredAsync(id).ConfigureAwait(true));

        var on = await ReadAsync(
            await client.PutAsJsonAsync(Url(id), new { enabled = true }).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.True(on.GetProperty("traceSteps").GetBoolean());
        Assert.True(await StoredAsync(id).ConfigureAwait(true));

        // Повтор того самого значення — без другого запису журналу.
        await ReadAsync(
            await client.PutAsJsonAsync(Url(id), new { enabled = true }).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);

        var off = await ReadAsync(
            await client.PutAsJsonAsync(Url(id), new { enabled = false }).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.False(off.GetProperty("traceSteps").GetBoolean());
        Assert.False(await StoredAsync(id).ConfigureAwait(true));

        var journal = await JournalAsync(id).ConfigureAwait(true);
        Assert.Equal(2, journal.Count);
        Assert.Contains("\"traceSteps\":true", journal[0], StringComparison.Ordinal);
        Assert.Contains("\"traceSteps\":false", journal[1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Без_права_EditFormula_403_читати_можна_невідома_методологія_404()
    {
        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(app, ViewerPermissions).ConfigureAwait(true);
        using var editor = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var id = await MethodologyAsync().ConfigureAwait(true);

        await ReadAsync(await viewer.GetAsync(Url(id)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);

        var denied = await viewer.PutAsJsonAsync(Url(id), new { enabled = true }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.False(await StoredAsync(id).ConfigureAwait(true));
        Assert.Empty(await JournalAsync(id).ConfigureAwait(true));

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await editor.PutAsJsonAsync(Url(id + 1_000_000), new { enabled = true }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync(Url(id + 1_000_000)).ConfigureAwait(true)).StatusCode);
    }

    private static Uri Url(int methodologyId)
        => new($"/api/v1/methodologies/{methodologyId}/trace-steps", UriKind.Relative);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<bool> StoredAsync(int id)
    {
        await using var db = new EcrDbContext(Options());
        return await db.Methodologies.AsNoTracking().Where(m => m.Id == id)
            .Select(m => m.TraceSteps).SingleAsync().ConfigureAwait(false);
    }

    /// <summary>Записи журналу структурних змін прапора — NewJson у порядку запису.</summary>
    private async Task<List<string>> JournalAsync(int id)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NewJson FROM aud.StructureChange
             WHERE EntityType = N'calc.Methodology' AND Operation = N'SetTraceSteps' AND EntityId = @id AND TemplateVersionId = 0
             ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@id", id);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<int> MethodologyAsync()
    {
        await using var db = new EcrDbContext(Options());
        var methodology = new Methodology(
            EcrCode.Create($"TS{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-565 trace steps" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return methodology.Id;
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"mtrs_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Trace steps test" }));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
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
}
