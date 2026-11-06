// tests/Ecr.Api.Tests/PermitImportMapAccessHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// Рецензія F13 (Аудит-2) імпорту таблиці дозволу: P2 - <c>POST …/import/lookups</c> читав реєстри-цілі без перевірки
/// <c>Registry.View</c> і без <c>Deny</c> (S18/D-312), тож читач перевіряв існування і назви схованих записів; P3 - два одночасні
/// ПЕРШІ збереження зіставлення давали <c>500</c> (<c>UQ_RegistryImportProfile</c>) замість <c>409</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази (виконані локально, кожну відновлено):
/// <list type="bullet">
/// <item><c>PermitImportLookupHandler.HandleAsync</c>: <c>RegistryAccess.CanAccess(…) ? LoadAsync : Target.Hidden</c> → завжди
/// <c>LoadAsync</c> - червоніють обидва тести P2 (схований код/назва резолвиться).</item>
/// <item><c>PermitImportMappingHandler.SaveAsync</c>: без <c>catch … IsUniqueViolation</c> - червоніє тест P3 (500).</item>
/// </list>
/// </remarks>
public sealed partial class PermitImportMapHttpTests
{
    private const string AccessPassword = "Api-Permit-Import-F13-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Deny_на_реєстр_цілі_ховає_його_записи_як_невідомі_і_з_виведення_Region()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(admin, world);
        var regionId = await RegistryIdAsync(world.RegionRegistry);

        using var reader = await SignedInWithGrantsAsync(app, ["Registry.View"], (regionId, GrantLevel.Read, true));
        var result = await LookupsAsync(reader, table, new
        {
            columns = new object[]
            {
                new { column = "NAE", values = new[] { $"N1_{_tag}" } },
                new { column = "REGION", values = new[] { $"RATY{_tag}", $"Mangystau{_tag}", $"nothing{_tag}" } },
            },
            derive = true,
        });

        // Видимий реєстр (дерево НАЕ) резолвиться як і раніше.
        Assert.Equal(world.N1, Values(result, "NAE")[$"N1_{_tag}"].EntryId);

        // Записи реєстру під Deny - за кодом і за назвою - та сама відповідь, що й неіснуючий текст.
        var region = RawValues(result, "REGION");
        var unknown = Shape(region[$"nothing{_tag}"]);
        Assert.Equal("null|null|unknown|0", unknown);
        Assert.Equal(unknown, Shape(region[$"RATY{_tag}"]));
        Assert.Equal(unknown, Shape(region[$"Mangystau{_tag}"]));

        // Виведення Region із дерева НАЕ не бере запис схованого реєстру (без Deny тут був би RegionAty).
        var derived = result.GetProperty("derived").EnumerateArray().Single(d => d.GetProperty("naeEntryId").GetInt64() == world.N1);
        var cell = derived.GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("column").GetString() == "REGION");
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("entryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("label").ValueKind);
        Assert.Equal("noMatch", cell.GetProperty("reason").GetString());

        // Контроль: адміністратор без Deny бачить той самий запис - фільтр діє саме від заборони.
        var control = await LookupsAsync(admin, table, new
        {
            columns = new[] { new { column = "REGION", values = new[] { $"RATY{_tag}" } } },
            derive = false,
        });
        Assert.Equal(world.RegionAty, Values(control, "REGION")[$"RATY{_tag}"].EntryId);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_View_на_реєстр_цілі_його_записи_невідомі_навіть_не_джерело()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(admin, world);
        var tableId = await RegistryIdAsync(table);

        // Лише грант Read на таблицю дозволу: діалог відкривається, але реєстри-цілі (НАЕ, регіони) читач не бачить.
        using var reader = await SignedInWithGrantsAsync(app, [], (tableId, GrantLevel.Read, false));
        var result = await LookupsAsync(reader, table, new
        {
            columns = new[]
            {
                new { column = "NAE", values = new[] { $"0036{_tag}", $"N1_{_tag}", $"Boiler{_tag}", $"Site1{_tag}", "nothing" } },
            },
            derive = true,
        });

        var nae = RawValues(result, "NAE");
        var unknown = Shape(nae["nothing"]);
        Assert.Equal("null|null|unknown|0", unknown);
        Assert.All(new[] { $"0036{_tag}", $"N1_{_tag}", $"Boiler{_tag}", $"Site1{_tag}" }, text => Assert.Equal(unknown, Shape(nae[text])));
        Assert.Empty(result.GetProperty("derived").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_перші_збереження_зіставлення_одночасно_дають_409_а_не_500()
    {
        var world = await SeedAsync();
        var tableId = 0;
        var racing = 0;
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = s =>
            {
                s.AddSingleton(new NaeTreeSettings(world.NaeRegistry, NaeTreeSettings.Default.RegionSourceRegistryCode));
                s.AddSingleton(new AirLimitsSettings(world.NaeRegistry, AirLimitsSettings.Default.SubstanceRegistryCode, world.RegionRegistry, world.AreaRegistry));
                // Детермінована гонка: між перевіркою «зіставлення немає» і вставкою інший запит уже зберіг своє.
                s.AddScoped<IRegistryImportProfileStore>(sp => new RacingProfileStore(
                    new RegistryImportProfileStore(sp.GetRequiredService<EcrDbContext>()),
                    sql,
                    () => Volatile.Read(ref tableId) != 0 && Interlocked.Exchange(ref racing, 1) == 0 ? tableId : null));
            },
        };
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(admin, world);
        Volatile.Write(ref tableId, await RegistryIdAsync(table));
        var url = new Uri($"/api/v1/permit-tables/{table}/import-mapping", UriKind.Relative);

        var response = await admin.PutAsJsonAsync(url, new { columns = new Dictionary<string, string> { ["NAE number"] = "NAE" } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}");
        Assert.Equal("ECR-REG-0409", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("err.ECR-REG-0409.importMappingChanged", body, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref racing));

        // Збережене - зіставлення суперника; повтор із його токеном проходить.
        var saved = await Json(await admin.GetAsync(url));
        Assert.Equal("NAME", saved.GetProperty("columns").GetProperty("rival").GetString());
        var token = saved.GetProperty("rowVersion").GetString()!;
        var retried = await Put(admin, url, token, new Dictionary<string, string> { ["NAE number"] = "NAE" });
        Assert.True(retried.StatusCode == HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
    }

    /// <remarks>
    /// AU-13 P2-A (S18/D-312): <c>PUT …/import-mapping</c> перевіряв лише <c>Permit.EditType</c> - користувач із Deny Read на таблицю
    /// дозволу зберігав спільне зіставлення (200), а невідомий стовпець давав 422 замість 404 (оракул таблиці й кодів колонок).
    /// Мутація: прибрати <c>RegistryAccess.RequireAsync</c> із <c>SaveAsync</c> - червоніє (200 і 422 замість 404).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збереження_зіставлення_під_Deny_таблиці_404_як_неіснуюча_і_нічого_не_зберігає()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(admin, world);
        var tableId = await RegistryIdAsync(table);
        var missing = $"PRM_NOPE{_tag}_Sources";

        using var denied = await SignedInWithGrantsAsync(
            app, ["Permit.EditType", "Registry.View", "Registry.EditData"], (tableId, GrantLevel.Read, true));
        var url = new Uri($"/api/v1/permit-tables/{table}/import-mapping", UriKind.Relative);
        var missingUrl = new Uri($"/api/v1/permit-tables/{missing}/import-mapping", UriKind.Relative);

        var unknown = await denied.PutAsJsonAsync(missingUrl, new { columns = new Dictionary<string, string> { ["Name"] = "NAME" } });
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        Assert.True(unknown.StatusCode == HttpStatusCode.NotFound, $"{unknown.StatusCode}: {unknownBody}");

        // Відомий стовпець і невідомий - та сама відповідь, що й таблиця, якої немає.
        foreach (var column in new[] { "NAME", "NOPECOL" })
        {
            var hidden = await denied.PutAsJsonAsync(url, new { columns = new Dictionary<string, string> { ["Name"] = column } });
            var body = await hidden.Content.ReadAsStringAsync();
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"{column}: {hidden.StatusCode}: {body}");
            Assert.Equal(Uniform(unknownBody, missing), Uniform(body, table));
        }

        // Нічого не збережено: адміністратор бачить порожнє зіставлення.
        var seen = await Json(await admin.GetAsync(url));
        Assert.Empty(seen.GetProperty("columns").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("rowVersion").ValueKind);

        // Контроль: редактор без Deny зберігає, як і раніше.
        using var editor = await SignedInWithGrantsAsync(app, ["Permit.EditType", "Registry.View"]);
        var saved = await editor.PutAsJsonAsync(url, new { columns = new Dictionary<string, string> { ["Name"] = "NAME" } });
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
    }

    private static string Uniform(string body, string code)
        => System.Text.RegularExpressions.Regex.Replace(
            body.Replace(code, "<T>", StringComparison.Ordinal),
            "\"(traceId|correlationId|requestId)\"\\s*:\\s*\"[^\"]*\",?",
            string.Empty);

    private static Dictionary<string, JsonElement> RawValues(JsonElement result, string column)
        => result.GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("column").GetString() == column)
            .GetProperty("values").EnumerateArray()
            .ToDictionary(v => v.GetProperty("text").GetString()!, v => v);

    /// <summary>Усе, крім ехо тексту: <c>entryId|label|reason|matches</c>.</summary>
    private static string Shape(JsonElement value)
        => string.Join(
            "|",
            value.GetProperty("entryId").ValueKind == JsonValueKind.Null ? "null" : value.GetProperty("entryId").GetRawText(),
            value.GetProperty("label").ValueKind == JsonValueKind.Null ? "null" : value.GetProperty("label").GetString(),
            value.GetProperty("reason").GetString(),
            value.GetProperty("matches").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async Task<int> RegistryIdAsync(string code)
    {
        await using var db = sql.CreateContext();
        return await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
    }

    private async Task<HttpClient> SignedInWithGrantsAsync(
        EcrApiFactory app, string[] permissions, params (int RegistryId, GrantLevel Level, bool Deny)[] grants)
    {
        var name = $"pimf_{Guid.NewGuid():N}"[..20];
        await using (var db = sql.CreateContext())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(AccessPassword));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Text("Permit import F13"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            foreach (var (registryId, level, deny) in grants)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registryId, level, isDeny: deny));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = AccessPassword });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Справжнє сховище, що перед першим «зіставлення немає» вставляє профіль суперника окремим контекстом.</summary>
    private sealed class RacingProfileStore(IRegistryImportProfileStore inner, SqlServerFixture sql, Func<int?> raceOn)
        : IRegistryImportProfileStore
    {
        public Task<IReadOnlyList<RegistryImportProfile>> ListAsync(int registryDefId, CancellationToken ct) => inner.ListAsync(registryDefId, ct);

        public async Task<RegistryImportProfile?> FindAsync(int registryDefId, string code, CancellationToken ct)
        {
            var found = await inner.FindAsync(registryDefId, code, ct);
            if (found is null && raceOn() is { } table && table == registryDefId)
            {
                await using var db = sql.CreateContext();
                var userId = await db.Users.Select(u => u.Id).FirstAsync(ct);
                db.RegistryImportProfiles.Add(new RegistryImportProfile(
                    registryDefId, EcrCode.Create(code), Text("Paste mapping"),
                    """{"kind":"permitPasteMapping","columns":{"rival":"NAME"}}""", userId, DateTime.UtcNow));
                await db.SaveChangesAsync(ct);
            }

            return found;
        }

        public void Add(RegistryImportProfile profile) => inner.Add(profile);

        public void Expect(RegistryImportProfile profile, byte[] rowVersion) => inner.Expect(profile, rowVersion);
    }
}
