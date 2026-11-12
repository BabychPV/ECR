// tests/Ecr.Api.Tests/FlertSyncApiTests.cs
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

namespace Ecr.Api.Tests;

/// <summary>
/// «Синхронізувати зараз» і стан синку FLERT (<c>D-476</c>, крок F3) через справжній HTTP-шлях: права перевіряє обробник
/// (<c>Integration.Manage</c> на запуск; <c>Integration.View</c> або <c>Manage</c> на стан), а не клієнт.
/// </summary>
/// <remarks>
/// ⛔ Диференційний доказ доступу: той самий запит під роллю з правом і без нього; фільтри доступу не змінюються.
/// Мутаційний доказ (локально, не в коміті): прибрати <c>PermissionCheck.RequireAsync</c> у <c>SyncFlertNowHandler</c> →
/// <c>Без_Integration_Manage_запуск_403_а_стан_із_View_видно</c> червоний (202 замість 403).
/// </remarks>
[Collection("SqlServer")]
public sealed class FlertSyncApiTests(SqlServerFixture sql)
{
    private const string Password = "Flert-Sync-Api-2026!";

    private static readonly string[] MirrorCodes = ["STREAM", "STREAM_CASE", "GAS_COMPOSITION"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task З_Integration_Manage_запуск_202_з_jobId_а_стан_віддає_дзеркало_і_прогін()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ["Integration.Manage"]);

        using var sync = await client.PostAsync(new Uri("/api/v1/integration/flert-sync", UriKind.Relative), null);
        var body = await sync.Content.ReadAsStringAsync();
        Assert.True(sync.StatusCode == HttpStatusCode.Accepted, $"{sync.StatusCode}: {body} | {app.ErrorsText}");
        Assert.False(string.IsNullOrWhiteSpace(JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()));

        using var status = await client.GetAsync(new Uri("/api/v1/integration/flert-sync/status", UriKind.Relative));
        var text = await status.Content.ReadAsStringAsync();
        Assert.True(status.StatusCode == HttpStatusCode.OK, $"{status.StatusCode}: {text}");
        var root = JsonDocument.Parse(text).RootElement;
        Assert.False(root.GetProperty("enabled").GetBoolean());
        var codes = root.GetProperty("registryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(MirrorCodes, codes);

        // Відповідь стану мала (один рядок журналу без переліку зауважень) — сторінка читає її при кожному відкритті.
        Assert.True(text.Length < 1_024, $"стан синку FLERT — {text.Length} байт: {text}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Без_Integration_Manage_запуск_403_а_стан_із_View_видно()
    {
        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(app, ["Integration.View"]);

        using var sync = await viewer.PostAsync(new Uri("/api/v1/integration/flert-sync", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Forbidden, sync.StatusCode);

        using var status = await viewer.GetAsync(new Uri("/api/v1/integration/flert-sync/status", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Без_прав_інтеграції_і_стан_403()
    {
        using var app = new EcrApiFactory(sql);
        using var other = await SignedInAsync(app, ["Registry.EditData"]);

        using var status = await other.GetAsync(new Uri("/api/v1/integration/flert-sync/status", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);

        using var sync = await other.PostAsync(new Uri("/api/v1/integration/flert-sync", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Forbidden, sync.StatusCode);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"flrt_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "FLERT sync test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
