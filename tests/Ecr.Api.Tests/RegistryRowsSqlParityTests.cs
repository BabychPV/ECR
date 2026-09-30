// tests/Ecr.Api.Tests/RegistryRowsSqlParityTests.cs
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

namespace Ecr.Api.Tests;

/// <summary>
/// P1-4: видимість записів у SQL (<c>/rows</c>, плаский довідник) збігається з правилом пікера
/// (<c>RegistryResolver</c>, <c>/entries</c>) на межах вікна чинності, для неактивних, видалених і каскаду;
/// пошук значення в SQL не залежить від регістру й кирилиці.
/// </summary>
/// <remarks>
/// ⛔ Доказ червоного: у <c>RegistryRowsQuery.Visible</c> замінити <c>asOf &lt; e.ValidTo</c> на
/// <c>asOf &lt;= e.ValidTo</c> — рядок на межі <c>ValidTo</c> з'являється в <c>/rows</c>, а пікер його не
/// віддає, і перший же <c>Assert.Equal</c> на даті межі червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryRowsSqlParityTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Parity-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видимість_у_SQL_збігається_з_пікером_на_межах_вікна_неактивних_і_видалених()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var (id, code) = await CreateRegistryAsync(client, temporal: true);

        var ids = new List<long>();
        for (var i = 1; i <= 7; i++)
        {
            ids.Add(await CreateEntryAsync(client, code, id, $"K{i}", $"Вода{i}"));
        }

        await ExecAsync($"""
            UPDATE dic.RegistryEntry SET ValidFrom='2026-03-01' WHERE Id={ids[1]};
            UPDATE dic.RegistryEntry SET ValidTo='2026-03-01' WHERE Id={ids[2]};
            UPDATE dic.RegistryEntry SET ValidFrom='2026-02-01', ValidTo='2026-03-02' WHERE Id={ids[3]};
            UPDATE dic.RegistryEntry SET IsActive=0 WHERE Id={ids[4]};
            UPDATE dic.RegistryEntry SET IsDeleted=1 WHERE Id={ids[5]};
            UPDATE dic.RegistryEntry SET ParentEntryId={ids[0]} WHERE Id={ids[6]};
            """);

        foreach (var asOf in new[] { "2026-01-31", "2026-02-01", "2026-02-28", "2026-03-01", "2026-03-02", "2026-03-03" })
        {
            var rows = await IdsAsync(client, $"/api/v1/registries/{code}/rows?asOf={asOf}&limit=500");
            var picker = await IdsAsync(client, $"/api/v1/registries/{code}/entries?asOf={asOf}");
            Assert.Equal(picker.Order(), rows);
            Assert.NotEmpty(rows);

            var cascadeRows = await IdsAsync(client, $"/api/v1/registries/{code}/rows?asOf={asOf}&limit=500&parentEntryId={ids[0]}");
            var cascadePicker = await IdsAsync(client, $"/api/v1/registries/{code}/entries?asOf={asOf}&parentEntryId={ids[0]}");
            Assert.Equal(cascadePicker.Order(), cascadeRows);
        }

        // Пошук значення без регістру (кирилиця) — надмножина в SQL, точний збіг у обробнику; Id за зростанням.
        var found = await IdsAsync(client, $"/api/v1/registries/{code}/rows?asOf=2026-02-15&limit=500&q=%D0%B2%D0%9E%D0%B4");
        Assert.Equal(new[] { ids[0], ids[2], ids[3], ids[6] }.Order(), found);
        var none = await IdsAsync(client, $"/api/v1/registries/{code}/rows?asOf=2026-02-15&limit=500&q=nomatch");
        Assert.Empty(none);
    }

    private async Task ExecAsync(string text)
    {
        await using var c = new SqlConnection(sql.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = text;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long[]> IdsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
        return [.. items.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).Order()];
    }

    private static async Task<long> CreateEntryAsync(HttpClient client, string code, int registryId, string key, string name)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = registryId,
                code = key,
                display = new { values = new Dictionary<string, string> { ["en"] = key } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["NAME"] = name },
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<(int Id, string Code)> CreateRegistryAsync(HttpClient client, bool temporal)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/registries", UriKind.Relative),
            new { code = $"PA{tag}", nameL10n = new Dictionary<string, string> { ["en"] = $"PA {tag}" }, isTemporal = temporal });
        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var code = created.GetProperty("code").GetString()!;
        var saved = await client.PutAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative),
            new
            {
                fields = new[]
                {
                    new
                    {
                        id = (int?)null, code = "NAME", nameL10n = new { values = new Dictionary<string, string> { ["en"] = "NAME" } },
                        dataType = "String", ordinal = 1, isRequired = true, isKey = true,
                        lookupRegistryDefId = (int?)null, unitId = (int?)null,
                    },
                },
                rules = Array.Empty<object>(),
                reason = "parity",
            });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        return (created.GetProperty("id").GetInt32(), code);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regp_{Guid.NewGuid():N}"[..20];
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using (var db = new EcrDbContext(options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Parity" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var p in new[] { "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, p));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, app.ErrorsText);
        return client;
    }
}

