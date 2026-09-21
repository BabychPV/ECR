// tests/Ecr.Api.Tests/Security/LastSignInApiTests.cs
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
/// <c>sec.User.LastSignInAt</c>: пише лише успішний вхід, читає перелік
/// користувачів адмінки (BE-12).
/// </summary>
[Collection("SqlServer")]
public sealed class LastSignInApiTests(SqlServerFixture sql)
{
    private const string Password = "Kashagan-2026-Winter!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-12")]
    public async Task Успішний_вхід_пише_момент_у_базу_і_віддає_його_в_переліку()
    {
        var name = await ArrangeAdminAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // Невдала спроба до входу: колонка лишається порожньою.
        var failed = await LoginAsync(client, name, "не-той-пароль").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        Assert.Null(await StoredAsync(name).ConfigureAwait(true));

        var before = DateTime.UtcNow.AddSeconds(-5);
        var ok = await LoginAsync(client, name, Password).ConfigureAwait(true);
        Assert.True(ok.IsSuccessStatusCode, $"{ok.StatusCode}: {app.ErrorsText}");

        var stored = await StoredAsync(name).ConfigureAwait(true);
        Assert.NotNull(stored);
        Assert.InRange(stored.Value, before, DateTime.UtcNow.AddSeconds(5));

        // Невдала спроба ПІСЛЯ входу момент не зсуває.
        using (var other = app.CreateClient())
        {
            await LoginAsync(other, name, "не-той-пароль").ConfigureAwait(true);
        }

        Assert.Equal(stored, await StoredAsync(name).ConfigureAwait(true));

        var items = await ListAllAsync(client).ConfigureAwait(true);

        // Рядок UTC із «Z»: без нього клієнт прочитав би момент як місцевий час.
        var mine = items[name].GetString()!;
        Assert.EndsWith("Z", mine, StringComparison.Ordinal);
        Assert.Equal(stored.Value, DateTime.Parse(
            mine, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal));

        // Поле присутнє й тоді, коли входу не було: null, а не відсутній ключ.
        Assert.Equal(JsonValueKind.Null, items[$"lsn_{_tag}"].ValueKind);
    }

    /// <summary>Усі сторінки переліку: userName → lastSignInAt.</summary>
    private static async Task<Dictionary<string, JsonElement>> ListAllAsync(HttpClient client)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var url = "/api/v1/users?limit=500" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var u in json.RootElement.GetProperty("items").EnumerateArray())
            {
                result[u.GetProperty("userName").GetString()!] = u.GetProperty("lastSignInAt").Clone();
            }

            cursor = json.RootElement.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (cursor is not null);

        return result;
    }

    /// <summary>Роль із правом переліку користувачів + локальний користувач + ще один, що не входив.</summary>
    private async Task<string> ArrangeAdminAsync()
    {
        await using var db = CreateContext();

        var role = new Role(
            EcrCode.Create($"LSI_{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageUsers"));

        var name = $"lsi_{_tag}";
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var never = new User($"lsn_{_tag}", "never", AuthProvider.Local);
        never.SetPassword(user.PasswordHash!);
        db.Users.Add(never);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private async Task<DateTime?> StoredAsync(string name)
    {
        await using var db = CreateContext();
        return await db.Users.AsNoTracking()
            .Where(u => u.UserName == name)
            .Select(u => u.LastSignInAt)
            .SingleAsync().ConfigureAwait(false);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password)
        => client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password });

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
