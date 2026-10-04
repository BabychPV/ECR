// tests/Ecr.Api.Tests/Security/RegistryDenyHttpTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// S18 (ENT-AUDIT) на справжньому шляху: користувач із ГЛОБАЛЬНИМИ <c>Registry.View</c> і
/// <c>Registry.EditData</c> та явною забороною (<c>ResourceGrant.IsDeny</c>) на довідник не бачить і не
/// змінює його на жодному маршруті — <c>404</c>, як неіснуючий; довідник без заборони й користувач без
/// заборони — як раніше.
/// </summary>
/// <remarks>
/// ⛔ Рівень HTTP навмисно: профіль будує <c>AccessDecisionService</c> з рядків <c>sec.ResourceGrant</c>
/// (ключ <c>Registry:{id}</c>), і лише тут видно, що заборона доходить до обробників справжнім ключем, а
/// не тим, який склав тест. Доказ червоного: набір запущено на чистій вершині <c>dev/integration</c> без
/// виправлення S18 — усі «404» тут були 200/201.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryDenyHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Deny-2026!";
    private const string AsOf = "2026-09-30";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання__заборонений_довідник_404_на_кожному_маршруті__відкритий_і_користувач_без_заборони_200()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        using var denied = await SignedInAsync(app, stand.DeniedRegistryId);
        using var plain = await SignedInAsync(app, denyRegistryId: null);

        // Заборонений: усі маршрути читання → 404 (а не 200 через глобальне Registry.View).
        foreach (var path in ReadPaths(stand.DeniedCode, stand.DeniedEntryId))
        {
            var response = await denied.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"GET {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        // Відповідь та сама, що на довідник, якого немає (різниця розкривала б існування).
        var missing = await denied.GetAsync(new Uri($"/api/v1/registries/NO_{stand.Tag}/entries?asOf={AsOf}", UriKind.Relative));
        var hidden = await denied.GetAsync(new Uri($"/api/v1/registries/{stand.DeniedCode}/entries?asOf={AsOf}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal(
            (await JsonAsync(missing)).GetProperty("messageKey").GetString(),
            (await JsonAsync(hidden)).GetProperty("messageKey").GetString());

        // Відкритий довідник тим самим користувачем — 200 (заборона діє лише на свій довідник).
        foreach (var path in ReadPaths(stand.OpenCode, stand.OpenEntryId))
        {
            var response = await denied.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"GET {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        }

        // Користувач без заборони бачить обидва — поведінка до S18 не змінилась.
        foreach (var path in ReadPaths(stand.DeniedCode, stand.DeniedEntryId))
        {
            var response = await plain.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"GET {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_і_пошук__заборонений_довідник_не_повертається__відкритий_повертається()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        using var denied = await SignedInAsync(app, stand.DeniedRegistryId);
        using var plain = await SignedInAsync(app, denyRegistryId: null);

        var listDenied = await CodesAsync(denied, "/api/v1/registries");
        Assert.DoesNotContain(stand.DeniedCode, listDenied);
        Assert.Contains(stand.OpenCode, listDenied);

        var listPlain = await CodesAsync(plain, "/api/v1/registries");
        Assert.Contains(stand.DeniedCode, listPlain);
        Assert.Contains(stand.OpenCode, listPlain);

        // Пошук за спільним підрядком тегу: обидва довідники збігаються, заборонений — ні для «denied».
        var searchDenied = await CodesAsync(denied, $"/api/v1/search?q=DENY{stand.Tag}&limit=20");
        Assert.DoesNotContain(stand.DeniedCode, searchDenied);
        Assert.Contains(stand.OpenCode, searchDenied);

        var searchPlain = await CodesAsync(plain, $"/api/v1/search?q=DENY{stand.Tag}&limit=20");
        Assert.Contains(stand.DeniedCode, searchPlain);
        Assert.Contains(stand.OpenCode, searchPlain);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис__заборонений_довідник_404_і_нічого_не_записано__відкритий_записується()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        using var denied = await SignedInAsync(app, stand.DeniedRegistryId);

        var deniedBefore = await CountAsync(stand.DeniedRegistryId);

        // Upsert (ручне створення).
        var upsert = await denied.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{stand.DeniedCode}/entries", UriKind.Relative), Upsert(stand.DeniedRegistryId, "NEW1"));
        Assert.Equal(HttpStatusCode.NotFound, upsert.StatusCode);

        // Пакет (і dryRun теж).
        foreach (var dryRun in new[] { true, false })
        {
            var batch = await denied.PostAsJsonAsync(
                new Uri($"/api/v1/registries/{stand.DeniedCode}/entries/batch?dryRun={dryRun.ToString().ToLowerInvariant()}", UriKind.Relative),
                new { items = new[] { new { clientRowId = "c1", op = "upsert", id = (long?)null, code = "NEW2", baseVersion = (string?)null, values = new Dictionary<string, object?>() } } });
            Assert.Equal(HttpStatusCode.NotFound, batch.StatusCode);
        }

        // Імпорт CSV (і dryRun теж).
        foreach (var dryRun in new[] { true, false })
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("code\r\nNEW3\r\n"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            content.Add(file, "file", "entries.csv");
            var import = await denied.PostAsync(
                new Uri($"/api/v1/registries/{stand.DeniedCode}/entries/import?dryRun={dryRun.ToString().ToLowerInvariant()}", UriKind.Relative),
                content);
            Assert.Equal(HttpStatusCode.NotFound, import.StatusCode);
        }

        // Вікно чинності й видалення наявного запису.
        var validity = await denied.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{stand.DeniedCode}/entries/{stand.DeniedEntryId}/validity", UriKind.Relative),
            new { from = "2026-01-01", to = (string?)null });
        Assert.Equal(HttpStatusCode.NotFound, validity.StatusCode);

        var delete = await denied.DeleteAsync(
            new Uri($"/api/v1/registries/{stand.DeniedCode}/entries/{stand.DeniedEntryId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // Зовнішні ключі: перелік.
        var keys = await denied.GetAsync(
            new Uri($"/api/v1/registries/{stand.DeniedCode}/external-keys", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, keys.StatusCode);

        // Нічого не записано: рядків стільки, скільки було, запис живий, вікно не змінено.
        Assert.Equal(deniedBefore, await CountAsync(stand.DeniedRegistryId));
        await using (var db = new EcrDbContext(Options()))
        {
            var entry = await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == stand.DeniedEntryId);
            Assert.False(entry.IsDeleted);
            Assert.Null(entry.ValidFrom);
        }

        // Той самий користувач у відкритому довіднику пише як раніше.
        var open = await denied.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{stand.OpenCode}/entries", UriKind.Relative), Upsert(stand.OpenRegistryId, "NEW4"));
        Assert.True(
            open.StatusCode == HttpStatusCode.Created,
            $"{open.StatusCode} {await open.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    /// <summary>
    /// L1-13: батько запису — живий запис ЦЬОГО довідника. До виправлення: запис чужого довідника ставав батьком (201),
    /// неіснуючий id давав 500 на зовнішньому ключі.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task L1_13_parentEntryId_з_чужого_або_неіснуючого_запису_404_а_свого_довідника_201()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        using var user = await SignedInAsync(app, denyRegistryId: null);
        var uri = new Uri($"/api/v1/registries/{stand.OpenCode}/entries", UriKind.Relative);

        object Body(string code, long? parent) => new
        {
            id = (long?)null,
            registryDefId = stand.OpenRegistryId,
            code,
            display = new { values = new Dictionary<string, string> { ["en"] = code } },
            parentEntryId = parent,
            values = new Dictionary<string, object?>(),
        };

        var foreign = await user.PostAsJsonAsync(uri, Body("PF1", stand.DeniedEntryId));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var missing = await user.PostAsJsonAsync(uri, Body("PF2", 987654321));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using (var db = new EcrDbContext(Options()))
        {
            Assert.False(await db.RegistryEntries.AnyAsync(e => e.RegistryDefId == stand.OpenRegistryId && (e.Code == "PF1" || e.Code == "PF2")));
        }

        var own = await user.PostAsJsonAsync(uri, Body("PF3", stand.OpenEntryId));
        Assert.True(own.StatusCode == HttpStatusCode.Created, $"{own.StatusCode}: {await own.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    /// <summary>
    /// L5-08: перевірка ключа, перемикання джерела й зв'язки в описі не обходять заборону на довідник.
    /// До виправлення: keys/check віддавав 200/422 замість 404, source-kind перемикав заборонений довідник,
    /// а <c>relations[].targetRegistryCode</c> віддавав код забороненої цілі.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task L5_08__перевірка_ключа_перемикання_джерела_й_зв_язки_не_розкривають_заборонений_довідник()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await SeedAsync();
        string referrerCode;
        await using (var db = new EcrDbContext(Options()))
        {
            var referrer = new RegistryDef(EcrCode.Create($"DENY{stand.Tag}_R"), Name($"Ref {stand.Tag}"), isTemporal: false);
            db.RegistryDefs.Add(referrer);
            await db.SaveChangesAsync();
            var link = new RegistryFieldDef(referrer.Id, EcrCode.Create("TO_DENIED"), Name("To denied"), CellDataType.Lookup, 1);
            link.PointTo(stand.DeniedRegistryId);
            db.RegistryFieldDefs.Add(link);
            await db.SaveChangesAsync();
            referrerCode = referrer.Code;
        }

        using var denied = await SignedInAsync(app, stand.DeniedRegistryId, "Registry.EditDefinition", "Integration.Manage");

        // 1. keys/check: заборонений = неіснуючий.
        var request = new { fieldCodes = new[] { "X" }, ignoreCase = false };
        var check = await denied.PostAsJsonAsync(new Uri($"/api/v1/registries/{stand.DeniedCode}/keys/check", UriKind.Relative), request);
        var checkMissing = await denied.PostAsJsonAsync(new Uri($"/api/v1/registries/NO_{stand.Tag}/keys/check", UriKind.Relative), request);
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        Assert.Equal(
            (await JsonAsync(checkMissing)).GetProperty("messageKey").GetString(),
            (await JsonAsync(check)).GetProperty("messageKey").GetString());

        // 2. source-kind: набір із забороненим відмовляє 404 і НЕ міняє жодного довідника.
        var switched = await denied.PutAsJsonAsync(
            new Uri("/api/v1/registries/source-kind", UriKind.Relative),
            new { registryCodes = new[] { stand.OpenCode, stand.DeniedCode }, sourceKind = "External", reason = "L5-08" });
        Assert.Equal(HttpStatusCode.NotFound, switched.StatusCode);
        await using (var db = new EcrDbContext(Options()))
        {
            var kinds = await db.RegistryDefs.AsNoTracking()
                .Where(d => d.Id == stand.DeniedRegistryId || d.Id == stand.OpenRegistryId)
                .Select(d => d.SourceKind)
                .ToListAsync();
            Assert.All(kinds, k => Assert.Equal(RegistrySourceKind.Local, k));
        }

        // 3. опис довідника-посилальника: ціль-заборонена без коду.
        var definition = await denied.GetAsync(new Uri($"/api/v1/registries/{referrerCode}/definition", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        var relation = (await JsonAsync(definition)).GetProperty("relations").EnumerateArray()
            .Single(r => r.GetProperty("fieldCode").GetString() == "TO_DENIED");
        Assert.Equal(JsonValueKind.Null, relation.GetProperty("targetRegistryCode").ValueKind);

        // Контроль: той, кому не заборонено, бачить код цілі (поведінка до L5-08 не зламана).
        using var plain = await SignedInAsync(app, denyRegistryId: null);
        var plainDef = await plain.GetAsync(new Uri($"/api/v1/registries/{referrerCode}/definition", UriKind.Relative));
        var plainRelation = (await JsonAsync(plainDef)).GetProperty("relations").EnumerateArray()
            .Single(r => r.GetProperty("fieldCode").GetString() == "TO_DENIED");
        Assert.Equal(stand.DeniedCode, plainRelation.GetProperty("targetRegistryCode").GetString());
    }

    private static string[] ReadPaths(string code, long entryId) =>
    [
        $"/api/v1/registries/{code}/entries?asOf={AsOf}",
        $"/api/v1/registries/{code}/entries/{entryId}",
        $"/api/v1/registries/{code}/rows?asOf={AsOf}",
        $"/api/v1/registries/{code}/definition",
        $"/api/v1/registries/{code}/definition/draft",
        $"/api/v1/registries/{code}/history",
        $"/api/v1/registries/{code}/external-keys",
    ];

    private static object Upsert(int registryDefId, string code) => new
    {
        id = (long?)null,
        registryDefId,
        code,
        display = new { values = new Dictionary<string, string> { ["en"] = code } },
        parentEntryId = (long?)null,
        values = new Dictionary<string, object?>(),
    };

    private static async Task<IReadOnlyList<string>> CodesAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await JsonAsync(response);
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        return [.. root.EnumerateArray().Select(e => e.GetProperty("code").GetString()!)];
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<int> CountAsync(int registryDefId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.AsNoTracking().CountAsync(e => e.RegistryDefId == registryDefId);
    }

    /// <summary>Два довідники з одним записом кожен; коди несуть спільний підрядок для пошуку.</summary>
    private async Task<Stand> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var deniedRegistry = new RegistryDef(EcrCode.Create($"DENY{tag}_D"), Name($"Denied {tag}"), isTemporal: false);
        var openRegistry = new RegistryDef(EcrCode.Create($"DENY{tag}_O"), Name($"Open {tag}"), isTemporal: false);
        db.RegistryDefs.AddRange(deniedRegistry, openRegistry);
        await db.SaveChangesAsync();

        var deniedEntry = new RegistryEntry(deniedRegistry.Id, EcrCode.Create("D1"), Name("D1"));
        var openEntry = new RegistryEntry(openRegistry.Id, EcrCode.Create("O1"), Name("O1"));
        db.RegistryEntries.AddRange(deniedEntry, openEntry);
        await db.SaveChangesAsync();

        return new Stand(
            tag, deniedRegistry.Id, deniedRegistry.Code, deniedEntry.Id, openRegistry.Id, openRegistry.Code, openEntry.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// Користувач із глобальними <c>Registry.View</c>/<c>Registry.EditData</c>; з
    /// <paramref name="denyRegistryId"/> — ще й роль із забороною на цей довідник.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, int? denyRegistryId, params string[] extraPermissions)
    {
        var name = $"regdn_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry deny test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.EditData"));
            foreach (var extra in extraPermissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, extra));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));

            if (denyRegistryId is { } denied)
            {
                var denier = new Role(EcrCode.Create($"D{Guid.NewGuid():N}"[..12]), Name("Registry deny"));
                db.Roles.Add(denier);
                await db.SaveChangesAsync();

                db.ResourceGrants.Add(new ResourceGrant(denier.Id, ResourceKind.Registry, denied, GrantLevel.Read, isDeny: true));
                db.RoleAssignments.Add(new RoleAssignment(denier.Id, user.Id, principalSid: null));
            }

            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Stand(
        string Tag, int DeniedRegistryId, string DeniedCode, long DeniedEntryId, int OpenRegistryId, string OpenCode, long OpenEntryId);
}
