// tests/Ecr.Api.Tests/Security/RoleNameInListApiTests.cs
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
/// <c>GET /roles</c> віддає назву ролі (<c>nameL10n</c>) — ту, що задано при
/// створенні й перейменуванні; без неї екрани показували лише код.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>UserStore.ListRolesAsync</c> не передати
/// <c>r.NameL10n</c> у <c>RoleView</c> — поле в JSON <c>null</c>, тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class RoleNameInListApiTests(SqlServerFixture sql)
{
    private const string Password = "Role-Name-List-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Назва_ролі_віддається_після_створення_й_перейменування()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app).ConfigureAwait(true);

        // ⛔ Детермінований відтворювач флейка: у спільній базі CI ролей більше
        // за межу ListRolesAsync (Take(500) за Code). Заповнювачі сортуються
        // ПЕРЕД кодом тесту й прибираються наприкінці.
        await SeedFillerRolesAsync(520).ConfigureAwait(true);
        try
        {
            await RunAsync(app, admin).ConfigureAwait(true);
        }
        finally
        {
            await RemoveFillerRolesAsync().ConfigureAwait(true);
        }
    }

    private const string FillerPrefix = "Ffill_";

    private async Task SeedFillerRolesAsync(int count)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        for (var i = 0; i < count; i++)
        {
            db.Roles.Add(new Role(
                EcrCode.Create($"{FillerPrefix}{i:D4}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "filler" })));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task RemoveFillerRolesAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await db.Roles.Where(r => r.Code.StartsWith(FillerPrefix)).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private static async Task RunAsync(EcrApiFactory app, HttpClient admin)
    {
        var code = $"R{Guid.NewGuid():N}"[..12];
        var created = await admin.PostAsJsonAsync(
            new Uri("/api/v1/roles", UriKind.Relative),
            new { code, nameL10n = new Dictionary<string, string> { ["en"] = "Flare operators" }, permissionCodes = Array.Empty<string>() })
            .ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{(int)created.StatusCode}\n{app.ErrorsText}");
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("roleId").GetInt32();

        Assert.Equal("Flare operators", await NameAsync(admin, code).ConfigureAwait(true));

        var renamed = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/roles/{id}/code", UriKind.Relative),
            new { code, nameL10n = new Dictionary<string, string> { ["en"] = "Flare shift leads" } })
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);

        Assert.Equal("Flare shift leads", await NameAsync(admin, code).ConfigureAwait(true));
    }

    private static async Task<string?> NameAsync(HttpClient client, string code)
    {
        var list = JsonDocument.Parse(
            await client.GetStringAsync(new Uri("/api/v1/roles", UriKind.Relative)).ConfigureAwait(false)).RootElement;
        var role = list.EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);

        return role.GetProperty("nameL10n").GetProperty("values").GetProperty("en").GetString();
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"rnl_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "role-name admin" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageRoles"));
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
