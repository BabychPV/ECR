// tests/Ecr.Api.Tests/RegistryRowsHttpTests.cs
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
/// <c>GET /api/v1/registries/{code}/rows</c> — рядки довідника зі значеннями, наскрізь через HTTP і
/// SQL Server (RT-13, FEATURE-REGISTRY-TABLES §7.1, <c>D-158</c>, <c>D-166</c>).
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item>без <c>OrderBy(e =&gt; e.Id)</c> у курсорі → <see cref="Курсор_без_пропусків_і_повторів"/> червоний;</item>
/// <item><c>decimal</c> через <c>double</c> → <see cref="Decimal_рядком_без_втрати_знаків"/> червоний;</item>
/// <item>версія без <c>PeriodStart</c> значень → <see cref="Версія_рядка_змінюється_після_правки_значення"/> червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryRowsHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Rows-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Курсор_без_пропусків_і_повторів()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish");
        var registry = await FlatRegistryAsync(client);

        // ⚠ Коди спадають, Id зростають: порядок пікера (Ordinal, Code) і порядок курсора (Id)
        // протилежні — курсор, що гортає не за Id, повторює або губить рядки.
        var ids = new List<long>();
        foreach (var code in new[] { "Z", "Y", "X", "W", "V" })
        {
            ids.Add(await CreateAsync(client, registry, code, new() { ["NAME"] = code }));
        }

        var first = await RowsAsync(client, registry, "limit=2");
        Assert.Equal(5, first.GetProperty("totalCount").GetInt32());

        // Вставка між сторінками: новий запис має найменший код і найбільший Id.
        ids.Add(await CreateAsync(client, registry, "A", new() { ["NAME"] = "A" }));

        var seen = Ids(first).ToList();
        var cursor = first.GetProperty("nextCursor").GetString();
        while (cursor is not null)
        {
            var page = await RowsAsync(client, registry, $"limit=2&cursor={Uri.EscapeDataString(cursor)}");
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            Assert.True(seen.Count <= 12, "курсор не закінчується");
        }

        Assert.Equal(ids, seen);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Decimal_рядком_без_втрати_знаків()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish");
        var registry = await FlatRegistryAsync(client);

        // 26 значущих цифр: у double поміщається 15–17.
        const string precise = "1234567890.1234567890123456";
        var id = await CreateAsync(client, registry, "P", new() { ["NAME"] = "p", ["T"] = precise, ["ON"] = true });

        var row = Assert.Single(Rows(await RowsAsync(client, registry, "limit=10")));
        Assert.Equal(id, row.GetProperty("id").GetInt64());

        var value = row.GetProperty("values").GetProperty("T").GetProperty("value");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(precise, value.GetString());
        Assert.Equal("true", row.GetProperty("values").GetProperty("ON").GetProperty("value").GetString());

        // Поле без значення відсутнє, а не null-рядок.
        Assert.False(row.GetProperty("values").TryGetProperty("GROUP", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_рядка_змінюється_після_правки_значення()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish");
        var registry = await FlatRegistryAsync(client);

        var id = await CreateAsync(client, registry, "V", new() { ["NAME"] = "v", ["T"] = "1.5" });
        var before = Assert.Single(Rows(await RowsAsync(client, registry, "limit=10")));

        await Task.Delay(50);
        var between = await SqlNowAsync();
        await Task.Delay(50);

        var saved = await PostEntryAsync(client, registry, "V", new() { ["NAME"] = "v", ["T"] = "2.75" }, id);
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");

        var after = Assert.Single(Rows(await RowsAsync(client, registry, "limit=10")));
        Assert.Equal("2.75", after.GetProperty("values").GetProperty("T").GetProperty("value").GetString());
        Assert.NotEqual(before.GetProperty("version").GetString(), after.GetProperty("version").GetString());

        // asOfUtc — системна історія: момент до правки бачить старе значення й стару версію.
        var asOf = Uri.EscapeDataString(between.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
        var old = Assert.Single(Rows(await RowsAsync(client, registry, $"limit=10&asOfUtc={asOf}")));
        Assert.Equal("1.5", old.GetProperty("values").GetProperty("T").GetProperty("value").GetString());
        Assert.Equal(before.GetProperty("version").GetString(), old.GetProperty("version").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Частини_невидимого_батька_не_видно()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish");
        var (parent, child) = await CompositionAsync(client);

        var open = await CreateAsync(client, parent, "S1", new() { ["NAME"] = "1D-1" });
        var closed = await CreateAsync(client, parent, "S2", new() { ["NAME"] = "1D-2" });
        var partOpen = await CreateAsync(client, child, "P1", new() { ["PARENT"] = open, ["PART"] = "CH4" });
        var partClosed = await CreateAsync(client, child, "P2", new() { ["PARENT"] = closed, ["PART"] = "CH4" });

        var window = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{parent.Code}/entries/{closed}/validity", UriKind.Relative),
            new { from = (DateOnly?)null, to = new DateOnly(2026, 6, 1) });
        Assert.True(window.IsSuccessStatusCode, $"{window.StatusCode}: {await window.Content.ReadAsStringAsync()}");

        Assert.Equal([partOpen], Ids(await RowsAsync(client, child, "asOf=2026-07-01")));
        Assert.Equal([partOpen, partClosed], Ids(await RowsAsync(client, child, "asOf=2026-05-01")));

        // Батько композиції звужує до своїх частин; назва цілі Lookup — у display.
        var mine = Assert.Single(Rows(await RowsAsync(client, child, $"asOf=2026-05-01&parentEntryId={closed}")));
        Assert.Equal(partClosed, mine.GetProperty("id").GetInt64());
        var link = mine.GetProperty("values").GetProperty("PARENT");
        Assert.Equal(closed.ToString(System.Globalization.CultureInfo.InvariantCulture), link.GetProperty("value").GetString());
        Assert.Equal($"S2{parent.Tag}", link.GetProperty("display").GetString());

        // Без дати перелік частин темпорального батька збрехав би.
        var withoutDate = await client.GetAsync(new Uri($"/api/v1/registries/{child.Code}/rows", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutDate.StatusCode);
        Assert.Contains("err.ECR-REQ-0422.asOfRequired", await withoutDate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтри_межа_сторінки_і_права()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SignedInAsync(app, "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish");
        var registry = await FlatRegistryAsync(editor);

        var north = await CreateAsync(editor, registry, "N", new() { ["NAME"] = "Alpha", ["GROUP"] = "North", ["T"] = "12.40" });
        await CreateAsync(editor, registry, "S", new() { ["NAME"] = "Beta", ["GROUP"] = "South", ["T"] = "7" });

        Assert.Equal([north], Ids(await RowsAsync(editor, registry, "q=nort")));
        Assert.Equal([north], Ids(await RowsAsync(editor, registry, "field.T=12.4")));
        Assert.Empty(Ids(await RowsAsync(editor, registry, "field.GROUP=south&field.T=12.4")));

        var unknown = await editor.GetAsync(new Uri($"/api/v1/registries/{registry.Code}/rows?field.NOPE=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("err.ECR-REQ-0422.registryRowsFilter", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ⚠ Відхилено, а не обрізано до 500: інакше курсор наступної сторінки вів би не туди.
        var tooLarge = await editor.GetAsync(new Uri($"/api/v1/registries/{registry.Code}/rows?limit=501", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
        Assert.Contains("err.ECR-REQ-0422.pageSizeOutOfRange", await tooLarge.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using (var stranger = await SignedInAsync(app))
        {
            var denied = await stranger.GetAsync(new Uri($"/api/v1/registries/{registry.Code}/rows", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // Грант Read на цей довідник без глобального права — той самий шлях, що в пікері.
        using var granted = await SignedInAsync(app, grantRegistryId: registry.Id);
        Assert.Equal(2, (await RowsAsync(granted, registry, "limit=10")).GetProperty("totalCount").GetInt32());
    }

    private static JsonElement[] Rows(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private static long[] Ids(JsonElement page) => [.. Rows(page).Select(r => r.GetProperty("id").GetInt64())];

    private static async Task<JsonElement> RowsAsync(HttpClient client, Registry registry, string query)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/registries/{registry.Code}/rows?{query}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Плоский довідник: NAME (ключ), GROUP, T (decimal), ON (bool).</summary>
    private static async Task<Registry> FlatRegistryAsync(HttpClient client)
    {
        var registry = await CreateRegistryAsync(client, "RR", isTemporal: false);
        await SaveFieldsAsync(client, registry, [
            Field("NAME", "String", 1, isKey: true), Field("GROUP", "String", 2),
            Field("T", "Decimal", 3), Field("ON", "Bool", 4)]);
        return registry;
    }

    /// <summary>Темпоральний батько (NAME) і частина композиції (PARENT → батько, PART).</summary>
    private static async Task<(Registry Parent, Registry Child)> CompositionAsync(HttpClient client)
    {
        var parent = await CreateRegistryAsync(client, "RP", isTemporal: true);
        await SaveFieldsAsync(client, parent, [Field("NAME", "String", 1, isKey: true)]);

        var child = await CreateRegistryAsync(client, "RC", isTemporal: false);
        await SaveFieldsAsync(client, child, [
            new
            {
                id = (int?)null, code = "PARENT", nameL10n = Name("PARENT"), dataType = "Lookup", ordinal = 1,
                isRequired = true, isKey = false, lookupRegistryDefId = (int?)parent.Id, unitId = (int?)null,
                relationKind = "Composition", onParentDelete = "Cascade",
            },
            Field("PART", "String", 2, isKey: true)]);
        return (parent, child);
    }

    private static async Task SaveFieldsAsync(HttpClient client, Registry registry, object[] fields)
    {
        var saved = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{registry.Code}/definition", UriKind.Relative),
            new { fields, rules = Array.Empty<object>(), reason = "RT-13" });
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
    }

    private static object Field(string code, string dataType, int ordinal, bool isKey = false)
        => new
        {
            id = (int?)null, code, nameL10n = Name(code), dataType, ordinal, isRequired = isKey, isKey,
            lookupRegistryDefId = (int?)null, unitId = (int?)null,
        };

    private static object Name(string value) => new { values = new Dictionary<string, string> { ["en"] = value } };

    private static async Task<Registry> CreateRegistryAsync(HttpClient client, string prefix, bool isTemporal)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/registries", UriKind.Relative),
            new { code = $"{prefix}{tag}", nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} {tag}" }, isTemporal });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        var created = JsonDocument.Parse(body).RootElement;
        return new Registry(created.GetProperty("id").GetInt32(), created.GetProperty("code").GetString()!, tag);
    }

    private static async Task<long> CreateAsync(
        HttpClient client, Registry registry, string code, Dictionary<string, object?> values)
    {
        var response = await PostEntryAsync(client, registry, code, values, id: null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> PostEntryAsync(
        HttpClient client, Registry registry, string code, Dictionary<string, object?> values, long? id)
    {
        var full = $"{code}{registry.Tag}";
        return client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries", UriKind.Relative),
            new
            {
                id,
                registryDefId = registry.Id,
                code = full,
                display = new { values = new Dictionary<string, string> { ["en"] = full } },
                parentEntryId = (long?)null,
                values,
            });
    }

    /// <summary>Годинник SQL Server — той самий, що ставить <c>PeriodStart</c>.</summary>
    private async Task<DateTime> SqlNowAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SYSUTCDATETIME();";
        return DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync())!, DateTimeKind.Utc);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    /// <summary>Клієнт із правами або з ресурсним грантом <c>Read</c> на довідник.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => await SignedInAsync(app, null, permissions);

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, int? grantRegistryId, params string[] permissions)
    {
        var name = $"regr_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            if (permissions.Length > 0 || grantRegistryId is not null)
            {
                var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry rows test" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync();

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                if (grantRegistryId is { } registryId)
                {
                    db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registryId, GrantLevel.Read));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync();
            }
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Registry(int Id, string Code, string Tag);
}
