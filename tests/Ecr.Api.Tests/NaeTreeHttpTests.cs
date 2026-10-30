// tests/Ecr.Api.Tests/NaeTreeHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// NAE plan N2 end to end (HTTP + SQL Server, fake AF reader): access matrix, dry run writes nothing, the first apply provisions the
/// registry (levels, fields, composite key) and builds the tree, a second run changes nothing (create-only, ECR owns the tree),
/// the composite key lets one NAE code repeat across regions but refuses the same code in the same region and area, an
/// incompatible registry is refused, and "where used" is empty for a tree that no permit points at (the real usages after an import are pinned in `AirLimitsImportHttpTests`, plan N3).
/// </summary>
/// <remarks>
/// Mutation proofs (local, not committed): remove <c>CreateOnly</c>/the existing-by-code check → the idempotency test creates
/// duplicates or fails; drop the composite key from the provisioner → the key-conflict test returns 201; drop a permission
/// call in a handler → the matching matrix row turns 200; drop the tree lock in <c>UpsertRegistryEntryHandler</c> → no test
/// fails by design (it closes a theoretical race; the guard is the code review of <c>LockTreeAsync</c> + the move race test).
/// </remarks>
[Collection("SqlServer")]
public sealed class NaeTreeHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Nae-Tree-2026!";
    private const string Root = @"\\AF\ECR\ECR_01_Air Equipment";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("", 403, 403)]
    [InlineData("Registry.View", 403, 200)]
    [InlineData("Registry.EditDefinition", 403, 403)]
    [InlineData("Registry.EditData", 403, 403)]
    [InlineData("Registry.EditDefinition,Registry.EditData", 200, 403)]
    public async Task Access_matrix__import_needs_both_edit_permissions_and_references_needs_view(
        string permissions, int import, int references)
    {
        var fake = new FakeAf(Standard());
        using var app = Factory(fake);
        using var client = await SignedInAsync(app, permissions.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var code = NewCode();
        var other = await SeedFlatAsync();

        // dryRun: the matrix is about permissions, so no provisioning (which would also need Registry.Publish).
        Assert.Equal((HttpStatusCode)import, (await ImportAsync(client, code, dryRun: true)).StatusCode);
        Assert.Equal(
            (HttpStatusCode)references,
            (await client.GetAsync(new Uri($"/api/v1/registries/{other.Code}/entries/{other.EntryId}/references", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DryRun__reports_the_plan_and_writes_nothing()
    {
        using var app = Factory(new FakeAf(Standard()));
        using var client = await SignedInAsync(app, Full);
        var code = NewCode();

        var report = await ReadAsync(await ImportAsync(client, code, dryRun: true));

        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.False(report.GetProperty("applied").GetBoolean());
        Assert.Equal(5, report.GetProperty("elementsRead").GetInt32());
        Assert.Equal(2, Created(report, "REGION"));
        Assert.Equal(1, Created(report, "AREA"));
        Assert.Equal(2, Created(report, "SITE"));
        Assert.Equal(3, Created(report, "SRC"));
        Assert.Equal(0, Existing(report, "SRC"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Tree(code))).StatusCode);
        Assert.False(await RegistryExistsAsync(code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Apply__provisions_the_registry_builds_the_tree_and_is_idempotent()
    {
        var fake = new FakeAf(Standard());
        using var app = Factory(fake);
        using var client = await SignedInAsync(app, Full);
        var code = NewCode();

        var first = await ReadAsync(await ImportAsync(client, code, dryRun: false));
        Assert.True(first.GetProperty("applied").GetBoolean());
        Assert.True(first.GetProperty("registryProvisioned").GetBoolean());
        Assert.Equal(3, Created(first, "SRC"));

        var tree = await client.GetFromJsonAsync<JsonElement>(Tree(code));
        Assert.True(tree.GetProperty("isHierarchical").GetBoolean());
        Assert.Equal(
            ["REGION", "AREA", "SITE", "EQUIP", "SRC"],
            tree.GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("code").GetString()!).ToArray());
        Assert.Equal(8, tree.GetProperty("nodes").GetArrayLength());

        var nodes = tree.GetProperty("nodes").EnumerateArray().ToList();
        var src = nodes.Where(n => n.GetProperty("levelCode").GetString() == "SRC").ToList();
        Assert.Equal(3, src.Count);
        Assert.All(src, n => Assert.Equal("SITE", LevelOf(nodes, n.GetProperty("parentEntryId").GetInt64())));
        Assert.Equal(2, nodes.Count(n => n.GetProperty("levelCode").GetString() == "REGION" && n.GetProperty("parentEntryId").ValueKind == JsonValueKind.Null));

        // The business fields and the audit record are there.
        await using (var db = new EcrDbContext(Options()))
        {
            var def = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Code == code);
            Assert.True(def.IsTemporal);
            Assert.Contains(def.Fields, f => f.Code == "NaeCode");
            Assert.Equal(1, await db.RegistryKeyDefs.CountAsync(k => k.RegistryDefId == def.Id && k.Code == "NAE_BUSINESS" && !k.IsPrimary));
            Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.StructureChange WHERE Operation = 'NaeAfImport' AND EntityId = {def.Id}").SingleAsync());
        }

        // Second run: nothing to create, nothing written; AF names changing later do not touch ECR's tree.
        fake.Snapshot = new NaeAfSnapshot(
            [.. fake.Snapshot.Elements.Select(e => e with { NameEn = "CHANGED IN AF" })], true);
        var second = await ReadAsync(await ImportAsync(client, code, dryRun: false));
        Assert.False(second.GetProperty("applied").GetBoolean());
        Assert.False(second.GetProperty("registryProvisioned").GetBoolean());
        Assert.Equal(0, Created(second, "SRC"));
        Assert.Equal(3, Existing(second, "SRC"));
        var after = await client.GetFromJsonAsync<JsonElement>(Tree(code));
        Assert.Equal(8, after.GetProperty("nodes").GetArrayLength());
        Assert.DoesNotContain("CHANGED IN AF", after.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task CompositeKey__same_nae_code_repeats_across_regions_but_not_inside_one_region_and_area()
    {
        // 1085 exists under Atyrau/Offshore and under Mangistau (no area) in the standard AF.
        using var app = Factory(new FakeAf(Standard()));
        using var client = await SignedInAsync(app, Full);
        var code = NewCode();
        await ReadAsync(await ImportAsync(client, code, dryRun: false));

        await using var db = new EcrDbContext(Options());
        var def = await db.RegistryDefs.SingleAsync(d => d.Code == code);
        var sites = await db.RegistryEntries.Where(e => e.RegistryDefId == def.Id && e.Code.StartsWith("S_ATYRAU_OFFSHORE")).ToListAsync();
        var site = Assert.Single(sites);

        var duplicate = await UpsertSrcAsync(client, def.Id, code, "MANUAL_DUP", site.Id, "1085", "ATYRAU", "OFFSHORE");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var otherArea = await UpsertSrcAsync(client, def.Id, code, "MANUAL_OK", site.Id, "1085", "ATYRAU", "ONSHORE");
        Assert.Equal(HttpStatusCode.Created, otherArea.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Apply__skips_a_business_key_that_already_belongs_to_another_node()
    {
        var fake = new FakeAf(Standard());
        using var app = Factory(fake);
        using var client = await SignedInAsync(app, Full);
        var code = NewCode();
        await ReadAsync(await ImportAsync(client, code, dryRun: false));

        // A user adds a Mangistau source under another technical code; AF later lists the same NAE with a different path.
        await using var db = new EcrDbContext(Options());
        var def = await db.RegistryDefs.SingleAsync(d => d.Code == code);
        var site = await db.RegistryEntries.SingleAsync(e => e.RegistryDefId == def.Id && e.Code.StartsWith("S_MANGISTAU"));
        Assert.Equal(HttpStatusCode.Created, (await UpsertSrcAsync(client, def.Id, code, "USER_NAE_0500", site.Id, "0500", "MANGISTAU", "-")).StatusCode);

        fake.Snapshot = new NaeAfSnapshot(
            [.. fake.Snapshot.Elements, El(@"Mangistau_Air\ZPK_1\500", "Mangistau", null, "NAE 500")], true);

        var report = await ReadAsync(await ImportAsync(client, code, dryRun: false));
        Assert.Equal(0, Created(report, "SRC"));
        Assert.Contains(
            report.GetProperty("issues").EnumerateArray(),
            i => i.GetProperty("code").GetString() == "existingElsewhere");
        Assert.True(report.GetProperty("skipped").GetInt32() == 1, report.GetProperty("issues").ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Apply__refuses_an_existing_registry_that_is_not_an_nae_tree()
    {
        using var app = Factory(new FakeAf(Standard()));
        using var client = await SignedInAsync(app, Full);
        var flat = await SeedFlatAsync();

        var response = await ImportAsync(client, flat.Code, dryRun: false);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("err.ECR-REG-0422.naeTreeIncompatible", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task References__pins_today_s_behaviour_an_empty_list_and_404_for_foreign_or_missing_entries()
    {
        using var app = Factory(new FakeAf(Standard()));
        using var client = await SignedInAsync(app, Full);
        var code = NewCode();
        await ReadAsync(await ImportAsync(client, code, dryRun: false));
        var tree = await client.GetFromJsonAsync<JsonElement>(Tree(code));
        var srcId = tree.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("levelCode").GetString() == "SRC").GetProperty("id").GetInt64();

        var body = await client.GetFromJsonAsync<JsonElement>(Refs(code, srcId));
        Assert.Equal(code, body.GetProperty("registryCode").GetString());
        Assert.Equal(srcId, body.GetProperty("entryId").GetInt64());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());

        var flat = await SeedFlatAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Refs(code, flat.EntryId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Refs(code, 999_999_999))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Refs("NO_SUCH_" + code, srcId))).StatusCode);
    }

    // ---- fixtures ------------------------------------------------------------------------------------------------------

    private static readonly string[] Full =
    [
        "Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish",
    ];

    private static NaeAfElement El(string relative, string? region, string? area, string? en = null)
        => new($@"{Root}\{relative}", relative.Split('\\')[^1], en, null, null, region, area);

    /// <summary>Atyrau/Offshore/Island_A/1085 and /1086, Mangistau/ZPK_1/1085 (same code, other region), plus group elements.</summary>
    private static List<NaeAfElement> Standard() =>
    [
        El(@"Atyrau_Air", null, null),
        El(@"Atyrau_Air\Offshore\Island_A", null, null),
        El(@"Atyrau_Air\Offshore\Island_A\1085", "Atyrau", "Offshore", "Gen 1085"),
        El(@"Atyrau_Air\Offshore\Island_A\1086", "Atyrau", "Offshore", "Gen 1086"),
        El(@"Mangistau_Air\ZPK_1\1085", "Mangistau", null, "ZPK 1085"),
    ];

    private static string NewCode() => $"NAET{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private sealed class FakeAf(IEnumerable<NaeAfElement> elements) : INaeAfReader
    {
        public NaeAfSnapshot Snapshot { get; set; } = new([.. elements], true);

        public Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct) => Task.FromResult(Snapshot);
    }

    private Func<string>? serverErrors;

    private EcrApiFactory Factory(FakeAf fake)
        => Track(new EcrApiFactory(sql) { ExtraServices = s => s.AddSingleton<INaeAfReader>(fake) });

    private EcrApiFactory Track(EcrApiFactory app)
    {
        serverErrors = () => app.ErrorsText;
        return app;
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string code, bool dryRun)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/af-import", UriKind.Relative),
            new { dataSourceId = 1, rootPath = Root, registryCode = code, dryRun });

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.UnprocessableEntity)
            {
                Assert.Fail($"{response.StatusCode}: {text} | {serverErrors?.Invoke()}");
            }

            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static int Created(JsonElement report, string level) => LevelCount(report, level, "create");

    private static int Existing(JsonElement report, string level) => LevelCount(report, level, "existing");

    private static int LevelCount(JsonElement report, string level, string property)
        => report.GetProperty("levels").EnumerateArray().Single(l => l.GetProperty("levelCode").GetString() == level).GetProperty(property).GetInt32();

    private static string? LevelOf(List<JsonElement> nodes, long id)
        => nodes.Single(n => n.GetProperty("id").GetInt64() == id).GetProperty("levelCode").GetString();

    private static Uri Tree(string code) => new($"/api/v1/registries/{code}/tree", UriKind.Relative);

    private static Uri Refs(string code, long id) => new($"/api/v1/registries/{code}/entries/{id}/references", UriKind.Relative);

    private static Task<HttpResponseMessage> UpsertSrcAsync(
        HttpClient client, int registryId, string registry, string code, long parent, string nae, string region, string area)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry}/entries", UriKind.Relative),
            new
            {
                registryDefId = registryId,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = parent,
                levelCode = "SRC",
                values = new Dictionary<string, object?> { ["NaeCode"] = nae, ["RegionCode"] = region, ["AreaCode"] = area },
            });

    private async Task<bool> RegistryExistsAsync(string code)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryDefs.AnyAsync(d => d.Code == code);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<(string Code, long EntryId)> SeedFlatAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());
        var registry = new RegistryDef(EcrCode.Create($"NF{tag}"), Name($"Flat {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        var entry = new RegistryEntry(registry.Id, EcrCode.Create($"E1_{tag}"), Name("E1"), 0, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return (registry.Code, entry.Id);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"nae_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("NAE tree test"));
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
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
