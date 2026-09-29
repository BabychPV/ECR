// tests/Ecr.Api.Tests/Security/UserEmailAuditApiTests.cs
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
/// S20 аудиту безпеки: зміна адреси сповіщень користувача — подія
/// <c>UserEmailChanged</c> у журналі безпеки з МАСКОВАНИМИ адресами.
/// </summary>
[Collection("SqlServer")]
public sealed class UserEmailAuditApiTests(SqlServerFixture sql)
{
    private const string Password = "Email-Audit-Probe-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S20")]
    public async Task S20_зміна_email_пише_подію_безпеки_з_маскованими_адресами()
    {
        var (adminId, targetId) = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, $"eadm_{_tag}").ConfigureAwait(true);

        using var first = await PutEmailAsync(admin, targetId, "john.doe@kpo.example").ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.NoContent, $"{first.StatusCode}: {app.ErrorsText}");

        using var second = await PutEmailAsync(admin, targetId, "mary@corp.example").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // Та сама адреса вдруге — не зміна, події немає.
        using var same = await PutEmailAsync(admin, targetId, "mary@corp.example").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);

        var events = await EventsAsync(targetId).ConfigureAwait(true);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(adminId, e.By));

        using var firstJson = JsonDocument.Parse(events[0].Details);
        Assert.Equal(JsonValueKind.Null, firstJson.RootElement.GetProperty("oldEmail").ValueKind);
        Assert.Equal("jo***@kpo.example", firstJson.RootElement.GetProperty("newEmail").GetString());

        using var secondJson = JsonDocument.Parse(events[1].Details);
        Assert.Equal("jo***@kpo.example", secondJson.RootElement.GetProperty("oldEmail").GetString());
        Assert.Equal("ma***@corp.example", secondJson.RootElement.GetProperty("newEmail").GetString());

        // ⛔ Повної адреси в журналі немає.
        Assert.DoesNotContain(events, e => e.Details.Contains("john.doe", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Details.Contains("mary@", StringComparison.Ordinal));
    }

    private sealed record EventRow(int By, string Details);

    private async Task<List<EventRow>> EventsAsync(int targetId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ChangedByUserId, ISNULL(DetailsJson, N'')
            FROM aud.SecurityEvent
            WHERE EventType = N'UserEmailChanged' AND TargetUserId = @t
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@t", targetId);

        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new EventRow(reader.GetInt32(0), reader.GetString(1)));
        }

        return rows;
    }

    private static Task<HttpResponseMessage> PutEmailAsync(HttpClient client, int userId, string email)
        => client.PutAsJsonAsync(new Uri($"/api/v1/users/{userId}/email", UriKind.Relative), new { email });

    private async Task<(int Admin, int Target)> ArrangeAsync()
    {
        await using var db = CreateContext();

        var role = new Role(
            EcrCode.Create($"UEM_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageUsers"));

        var hash = new PasswordHasher().Hash(Password);
        var admin = new User($"eadm_{_tag}", "eadm", AuthProvider.Local);
        admin.SetPassword(hash);
        var target = new User($"etgt_{_tag}", "etgt", AuthProvider.Local);
        target.SetPassword(hash);
        db.Users.AddRange(admin, target);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, admin.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (admin.Id, target.Id);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password })
            .ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{userName}: {response.StatusCode} {app.ErrorsText}");
        return client;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
