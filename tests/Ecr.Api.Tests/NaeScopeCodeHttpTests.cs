// tests/Ecr.Api.Tests/NaeScopeCodeHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// AN-158 (human-reported): an NAE source under «Atyrau › AREA_TEST» showed Area «_____» because a manual REGION/AREA node stored no
/// code of its own and the code was derived from a technical code that flattened non-ASCII letters to «_». Root creation stores the
/// own code; a derived all-underscore code is empty; the start repair backfills stable ASCII codes and never invents a lost one.
/// </summary>
/// <remarks>
/// Mutation proof (local, not committed): drop the <c>own</c> field write in <c>NaeTreeManualHandler.Group</c> →
/// <c>Root__stores_its_own_codes_and_the_listed_area_is_inherited_by_sources</c> fails (stored AreaCode is null).
/// </remarks>
[Collection("SqlServer")]
public sealed class NaeScopeCodeHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Nae-Scope-2026!";

    private static readonly string[] Full = ["Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish"];

    private Func<string>? serverErrors;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Root__stores_its_own_codes_and_the_listed_area_is_inherited_by_sources()
    {
        var code = NewCode();
        using var app = Factory(code);
        using var client = await SignedInAsync(app);

        using var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative),
            new { regionCode = "Atyrau", areaCode = "Offshore", nameEn = (string?)null, nameRu = (string?)null, nameKz = (string?)null });
        var root = await ReadAsync(created);

        var nodes = await NodesAsync(client, code);
        var region = nodes.Single(n => n.Level == "REGION");
        var area = nodes.Single(n => n.Level == "AREA");
        Assert.Equal(root.GetProperty("areaEntryId").GetInt64(), area.Id);
        Assert.Equal("R_ATYRAU", region.Code);
        Assert.Equal("A_ATYRAU_OFFSHORE", area.Code);

        Assert.Equal("ATYRAU", Text(await ValuesAsync(client, code, region.Id), "RegionCode"));
        Assert.Equal("OFFSHORE", Text(await ValuesAsync(client, code, area.Id), "AreaCode"));

        var site = await CreatedIdAsync(await UpsertAsync(client, code, "SITE_1", area.Id, "SITE", "Site"));
        var source = await CreatedIdAsync(await UpsertAsync(client, code, "SRC_1", site, "SRC", "Source", new() { ["NaeCode"] = "2321" }));
        var values = await ValuesAsync(client, code, source);
        Assert.Equal(("ATYRAU", "OFFSHORE"), (Text(values, "RegionCode"), Text(values, "AreaCode")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Area__is_chosen_from_the_list_free_text_and_deactivated_entries_are_422_empty_is_region_only()
    {
        var code = NewCode();
        using var app = Factory(code);
        using var client = await SignedInAsync(app);

        // Free text (also Cyrillic) and a deactivated entry are refused with the NAE 0422 key; nothing is created.
        foreach (var bad in new[] { "Аймак", "Nowhere", "RETIRED" })
        {
            using var refused = await client.PostAsJsonAsync(new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = bad });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("naeAreaNotInList", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Empty(await NodesAsync(client, code));

        // Empty — the region only; a listed value (any case) — the region and its area.
        var regionOnly = await ReadAsync(await client.PostAsJsonAsync(new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = (string?)null }));
        Assert.Equal(JsonValueKind.Null, regionOnly.GetProperty("areaEntryId").ValueKind);
        var withArea = await ReadAsync(await client.PostAsJsonAsync(new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = "onshore" }));
        Assert.Equal("ONSHORE", Text(await ValuesAsync(client, code, withArea.GetProperty("areaEntryId").GetInt64()), "AreaCode"));

        // The AREA node card: the same rule on upsert.
        var area = withArea.GetProperty("areaEntryId").GetInt64();
        var region = withArea.GetProperty("regionEntryId").GetInt64();
        using var typed = await UpsertAsync(client, code, "A_ATYRAU_ONSHORE", region, "AREA", "Area", new() { ["AreaCode"] = "Аймак" }, id: area);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, typed.StatusCode);
        Assert.Contains("naeAreaNotInList", await typed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(
            HttpStatusCode.OK,
            (await UpsertAsync(client, code, "A_ATYRAU_ONSHORE", region, "AREA", "Area", new() { ["AreaCode"] = "offshore" }, id: area)).StatusCode);
        Assert.Equal("OFFSHORE", Text(await ValuesAsync(client, code, area), "AreaCode"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Repair__backfills_ascii_codes_once_and_leaves_a_flattened_code_empty()
    {
        var code = NewCode();
        using var app = Factory(code);
        using var client = await SignedInAsync(app);

        await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = "Offshore" }));
        await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Zhambyl", areaCode = "Onshore" }));

        // Data from before the fix: no own codes on REGION/AREA nodes, and the Cyrillic area code flattened to underscores.
        await using (var db = new EcrDbContext(Options()))
        {
            var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"""
                DELETE v FROM dic.RegistryValue v JOIN dic.RegistryEntry e ON e.Id = v.RegistryEntryId
                WHERE e.RegistryDefId = {id}
                """);
            await db.Database.ExecuteSqlAsync($"UPDATE dic.RegistryEntry SET Code = 'A_ZHAMBYL_____' WHERE RegistryDefId = {id} AND Code LIKE 'A[_]ZHAMBYL[_]%'");
        }

        for (var i = 0; i < 2; i++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AirTypeSystemProvisioner>().EnsureNaeTreeAsync(CancellationToken.None);
        }

        var nodes = await NodesAsync(client, code);
        async Task<(string?, string?)> Own(string entryCode)
        {
            var node = nodes.Single(n => n.Code == entryCode);
            var values = await ValuesAsync(client, code, node.Id);
            return (Text(values, "RegionCode"), Text(values, "AreaCode"));
        }

        Assert.Equal<(string?, string?)>(("ATYRAU", null), await Own("R_ATYRAU"));
        Assert.Equal<(string?, string?)>((null, "OFFSHORE"), await Own("A_ATYRAU_OFFSHORE"));
        Assert.Equal<(string?, string?)>(("ZHAMBYL", null), await Own("R_ZHAMBYL"));
        Assert.Equal<(string?, string?)>((null, null), await Own("A_ZHAMBYL_____"));

        // One audit row for the whole repair (2 regions + 1 area written, 1 area left empty); the second start wrote nothing.
        await using var audit = new EcrDbContext(Options());
        var defId = await audit.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
        var rows = await audit.Database
            .SqlQuery<string>($"SELECT NewJson AS Value FROM aud.StructureChange WHERE Operation = 'RepairScopeCodes' AND EntityId = {defId}")
            .ToListAsync();
        Assert.Equal("{\"written\":3,\"leftEmpty\":1}", Assert.Single(rows));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Derived__an_all_underscore_area_is_empty_below_a_legacy_node()
    {
        var code = NewCode();
        using var app = Factory(code);
        using var client = await SignedInAsync(app);

        await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = "Offshore" }));
        var nodes = await NodesAsync(client, code);
        var area = nodes.Single(n => n.Level == "AREA");

        // The reported shape: AREA node «A_ATYRAU_____» without a stored AreaCode.
        await using (var db = new EcrDbContext(Options()))
        {
            var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"DELETE v FROM dic.RegistryValue v JOIN dic.RegistryEntry e ON e.Id = v.RegistryEntryId WHERE e.RegistryDefId = {id} AND e.Id = {area.Id}");
            await db.Database.ExecuteSqlAsync($"UPDATE dic.RegistryEntry SET Code = 'A_ATYRAU_____' WHERE Id = {area.Id}");
        }

        var site = await CreatedIdAsync(await UpsertAsync(client, code, "SITE_1", area.Id, "SITE", "Site"));
        var source = await CreatedIdAsync(await UpsertAsync(client, code, "SRC_1", site, "SRC", "Source", new() { ["NaeCode"] = "2321" }));
        var values = await ValuesAsync(client, code, source);
        Assert.Equal("ATYRAU", Text(values, "RegionCode"));
        Assert.True(string.IsNullOrEmpty(Text(values, "AreaCode")), Text(values, "AreaCode"));

        // The fix for such a node: the administrator sets AreaCode on it, the sources below follow (03.11 cascade).
        Assert.Equal(
            HttpStatusCode.OK,
            (await UpsertAsync(client, code, "A_ATYRAU_____", nodes.Single(n => n.Level == "REGION").Id, "AREA", "Area", new() { ["AreaCode"] = "Onshore" }, id: area.Id)).StatusCode);
        Assert.Equal("ONSHORE", Text(await ValuesAsync(client, code, source), "AreaCode"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Site_is_optional__equip_and_src_hang_under_an_area_inherit_scope_stay_unique_and_move()
    {
        var code = NewCode();
        using var app = Factory(code);
        using var client = await SignedInAsync(app);

        await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = "Offshore" }));
        var area = (await NodesAsync(client, code)).Single(n => n.Level == "AREA");

        // EQUIP and SRC straight under the AREA (no SITE).
        var equip = await CreatedIdAsync(await UpsertAsync(client, code, "G_1", area.Id, "EQUIP", "Group"));
        var direct = await CreatedIdAsync(await UpsertAsync(client, code, "SRC_A", area.Id, "SRC", "Direct", new() { ["NaeCode"] = "3001" }));
        var underEquip = await CreatedIdAsync(await UpsertAsync(client, code, "SRC_B", equip, "SRC", "Under group", new() { ["NaeCode"] = "3002" }));
        foreach (var id in new[] { direct, underEquip })
        {
            var values = await ValuesAsync(client, code, id);
            Assert.Equal(("ATYRAU", "OFFSHORE"), (Text(values, "RegionCode"), Text(values, "AreaCode")));
        }

        // Uniqueness within (REGION, AREA) holds with and without a SITE in between: 3001 again under a site is refused.
        var site = await CreatedIdAsync(await UpsertAsync(client, code, "SITE_1", area.Id, "SITE", "Site"));
        using var taken = await UpsertAsync(client, code, "SRC_C", site, "SRC", "Dup", new() { ["NaeCode"] = "3001" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("naeCodeTaken", await taken.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Move: a source from under the SITE to straight under the AREA keeps its scope (same nearest REGION/AREA).
        var movable = await CreatedIdAsync(await UpsertAsync(client, code, "SRC_D", site, "SRC", "Movable", new() { ["NaeCode"] = "3003" }));
        using var moved = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/entries/{movable}/move", UriKind.Relative), new { parentEntryId = area.Id, levelCode = (string?)null });
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        Assert.Equal("OFFSHORE", Text(await ValuesAsync(client, code, movable), "AreaCode"));

        // SITE inside SITE still works; a SRC cannot sit above its region (REGION is required).
        Assert.True((await UpsertAsync(client, code, "SITE_2", site, "SITE", "Nested")).IsSuccessStatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Upgrade__site_becomes_optional_on_an_existing_tree_once()
    {
        var code = NewCode();
        using var app = Factory(code);
        using var client = await SignedInAsync(app);
        await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative), new { regionCode = "Atyrau", areaCode = "Offshore" }));

        await using (var db = new EcrDbContext(Options()))
        {
            var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE cfg.RegistryLevelDef SET IsOptional = 0 WHERE RegistryDefId = {id} AND Code = 'SITE'");
        }

        for (var i = 0; i < 2; i++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AirTypeSystemProvisioner>().EnsureNaeTreeAsync(CancellationToken.None);
        }

        await using var check = new EcrDbContext(Options());
        var defId = await check.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
        Assert.True(await check.RegistryLevelDefs.Where(l => l.RegistryDefId == defId && l.Code == "SITE").Select(l => l.IsOptional).SingleAsync());
        Assert.Equal(
            1,
            await check.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.StructureChange WHERE Operation = 'NaeSiteOptional' AND EntityId = {defId}").SingleAsync());
    }

    // ---- fixtures ------------------------------------------------------------------------------------------------------

    private sealed record Node(long Id, string Code, string Level);

    private static string NewCode() => $"NAES{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private async Task SeedAreaListAsync(string code)
    {
        await using var db = new EcrDbContext(Options());
        var list = new Ecr.Domain.Entities.Configuration.RegistryDef(EcrCode.Create("AREAS_" + code), Name("Areas"), isTemporal: false);
        db.RegistryDefs.Add(list);
        await db.SaveChangesAsync();
        var ordinal = 0;
        foreach (var entryCode in new[] { "OFFSHORE", "ONSHORE", "RETIRED" })
        {
            var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(
                list.Id, EcrCode.Create(entryCode), Name(entryCode[..1] + entryCode[1..].ToLowerInvariant()), ++ordinal, DateTime.UtcNow);
            if (entryCode == "RETIRED")
            {
                entry.Deactivate();
            }

            db.RegistryEntries.Add(entry);
        }

        await db.SaveChangesAsync();
    }

    private EcrApiFactory Factory(string code)
    {
        SeedAreaListAsync(code).GetAwaiter().GetResult();
        var app = new EcrApiFactory(sql)
        {
            ExtraServices = s =>
            {
                s.AddSingleton(new NaeTreeSettings(code, "NOREGIONS_" + code));
                s.AddSingleton(new AirLimitsSettings(code, "NO_SUBSTANCES_" + code, AreaRegistryCode: "AREAS_" + code));
            },
        };
        serverErrors = () => app.ErrorsText;
        return app;
    }

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text} | {serverErrors?.Invoke()}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private async Task<long> CreatedIdAsync(HttpResponseMessage response)
        => (await ReadAsync(response)).GetProperty("id").GetInt64();

    private async Task<List<Node>> NodesAsync(HttpClient client, string code)
        => [.. (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/tree", UriKind.Relative)))).GetProperty("nodes").EnumerateArray()
            .Select(n => new Node(n.GetProperty("id").GetInt64(), n.GetProperty("code").GetString()!, n.GetProperty("levelCode").GetString()!))];

    private async Task<JsonElement> ValuesAsync(HttpClient client, string code, long id)
        => (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/entries/{id}", UriKind.Relative)))).GetProperty("values");

    private static string? Text(JsonElement values, string field)
        => values.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<HttpResponseMessage> UpsertAsync(
        HttpClient client, string registry, string code, long? parent, string level, string en, Dictionary<string, object?>? values = null, long? id = null)
    {
        var registryId = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/registries/{registry}/definition", UriKind.Relative))).GetProperty("id").GetInt32();
        return await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry}/entries", UriKind.Relative),
            new { id, registryDefId = registryId, code, display = new { values = new Dictionary<string, string> { ["en"] = en } }, parentEntryId = parent, levelCode = level, values = values ?? [] });
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"naes_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("NAE scope test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in Full)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
