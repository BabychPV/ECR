// tests/Ecr.Api.Tests/NaeNodeTypesHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
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
/// NAE node types (human 03.11, quiz) end to end over HTTP + SQL Server: the AF load by template (a site inside a site, a source
/// under a unit), field-to-level binding in the definition, Q1 (English name required, Russian/Kazakh optional), Q3 (NAE number
/// required on SRC and unique within REGION+AREA, within REGION where there is no AREA — <c>409 ECR-REG-4094</c>), derived
/// RegionCode/AreaCode and read-only AfPath, moves that would change a source's region/area, and the one-time upgrade of a tree
/// provisioned before the node types.
/// </summary>
/// <remarks>
/// Mutation proof (local, not committed): make <c>NaeTreeNodePolicy.ScopeOf</c> ignore the AREA ancestor → the same number in
/// Atyrau/Onshore is refused, <c>NaeCode__is_unique_within_region_and_area_or_region_without_area</c> fails on the 201 row.
/// </remarks>
[Collection("SqlServer")]
public sealed class NaeNodeTypesHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Nae-Types-2026!";
    private const string Root = @"\\AF\ECR\ECR_01_Air";

    private static readonly string[] Full = ["Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish"];

    private static readonly string[] LevelOrder = ["REGION", "AREA", "SITE", "EQUIP", "SRC"];

    private Func<string>? serverErrors;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task AfImport__by_template_nests_a_site_in_a_site_and_binds_fields_to_their_node_type()
    {
        var (client, code, app) = await TreeAsync();
        using var _ = app;
        using var __ = client;

        var nodes = await NodesAsync(client, code);
        var block = nodes["S_ATYRAU_OFFSHORE_ISLAND_A_BLOCK_2"];
        Assert.Equal(("SITE", nodes["S_ATYRAU_OFFSHORE_ISLAND_A"].Id), (block.Level, block.Parent));
        Assert.Equal(("EQUIP", block.Id), (nodes["G_ATYRAU_OFFSHORE_ISLAND_A_BLOCK_2_GEN_1"].Level, nodes["G_ATYRAU_OFFSHORE_ISLAND_A_BLOCK_2_GEN_1"].Parent));
        Assert.Equal(nodes["G_ATYRAU_OFFSHORE_ISLAND_A_BLOCK_2_GEN_1"].Id, nodes["N_ATYRAU_OFFSHORE_1085"].Parent);

        var values = await ValuesAsync(client, code, nodes["N_ATYRAU_OFFSHORE_1085"].Id);
        Assert.Equal(("1085", "ATYRAU", "OFFSHORE"), (values.GetProperty("NaeCode").GetString(), values.GetProperty("RegionCode").GetString(), values.GetProperty("AreaCode").GetString()));

        // The definition says which node type each field belongs to and which fields the server owns.
        var fields = (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative))))
            .GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("code").GetString()!);
        Assert.Equal(("SRC", true, false), Field(fields["NaeCode"]));
        Assert.Equal(("SRC", false, true), Field(fields["RegionCode"]));
        Assert.Equal(("SRC", false, true), Field(fields["AreaCode"]));
        Assert.Equal((null, false, true), Field(fields["AfPath"]));
        Assert.Equal(("SRC", false, false), Field(fields["SourceType"]));

        var levels = (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/hierarchy", UriKind.Relative))))
            .GetProperty("levels").EnumerateArray().ToDictionary(l => l.GetProperty("code").GetString()!, l => l.GetProperty("allowsSelfNesting").GetBoolean());
        Assert.Equal([false, false, true, false, false], LevelOrder.Select(l => levels[l]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task NaeCode__is_unique_within_region_and_area_or_region_without_area()
    {
        var (client, code, app) = await TreeAsync();
        using var _ = app;
        using var __ = client;
        var nodes = await NodesAsync(client, code);

        // Same region and area, another site: taken (the scope is REGION+AREA, not the site).
        var islandB = await CreatedIdAsync(await UpsertAsync(client, code, "ISLAND_B", nodes["A_ATYRAU_OFFSHORE"].Id, "SITE", "Island B"));
        using (var taken = await UpsertAsync(client, code, "DUP_1085", islandB, "SRC", "Dup", new() { ["NaeCode"] = "1085" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            var problem = await ProblemAsync(taken);
            Assert.Equal(("ECR-REG-4094", "err.ECR-REG-4094.naeCodeTaken"), (problem.GetProperty("errorCode").GetString(), problem.GetProperty("messageKey").GetString()));
            Assert.Contains("N_ATYRAU_OFFSHORE_1085", problem.ToString(), StringComparison.Ordinal);
        }

        // Same region, another area: free.
        Assert.Equal(HttpStatusCode.Created, (await UpsertAsync(client, code, "ON_1085", nodes["S_ATYRAU_ONSHORE_PLANT"].Id, "SRC", "On", new() { ["NaeCode"] = "1085" })).StatusCode);

        // A region without On/Offshore: unique in the whole region, across its sites; the number is normalised first.
        var zpk2 = await CreatedIdAsync(await UpsertAsync(client, code, "ZPK_2", nodes["R_MANGISTAU"].Id, "SITE", "ZPK 2"));
        using (var inRegion = await UpsertAsync(client, code, "M_1085", zpk2, "SRC", "M", new() { ["NaeCode"] = "1085" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, inRegion.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await UpsertAsync(client, code, "M_36", zpk2, "SRC", "M", new() { ["NaeCode"] = "36_pod" })).StatusCode);
        using (var normalised = await UpsertAsync(client, code, "M_0036", nodes["S_MANGISTAU_NA_ZPK_1"].Id, "SRC", "M", new() { ["NaeCode"] = "0036_POD" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, normalised.StatusCode);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Validation__nae_code_required_on_src_only_english_name_required_and_fields_of_other_types_refused()
    {
        var (client, code, app) = await TreeAsync();
        using var _ = app;
        using var __ = client;
        var plant = (await NodesAsync(client, code))["S_ATYRAU_ONSHORE_PLANT"].Id;

        using (var noCode = await UpsertAsync(client, code, "NO_CODE", plant, "SRC", "No code"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noCode.StatusCode);
            Assert.Equal("err.ECR-REG-0422.requiredFieldsMissing", (await ProblemAsync(noCode)).GetProperty("messageKey").GetString());
        }

        // A group node needs no NAE number; Russian and Kazakh names are optional.
        Assert.Equal(HttpStatusCode.Created, (await UpsertAsync(client, code, "UNIT_X", plant, "EQUIP", "Unit X")).StatusCode);

        using (var noEn = await UpsertAsync(client, code, "UNIT_RU", plant, "EQUIP", null, ru: "Установка"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noEn.StatusCode);
            Assert.Equal("err.ECR-REG-0422.naeNameEnRequired", (await ProblemAsync(noEn)).GetProperty("messageKey").GetString());
        }

        using (var foreign = await UpsertAsync(client, code, "UNIT_T", plant, "EQUIP", "Unit T", new() { ["SourceType"] = "T" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
            Assert.Equal("err.ECR-REG-0422.fieldLevelMismatch", (await ProblemAsync(foreign)).GetProperty("messageKey").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Derived__client_region_area_and_af_path_are_ignored_and_moves_keep_the_scope()
    {
        var (client, code, app) = await TreeAsync();
        using var _ = app;
        using var __ = client;
        var nodes = await NodesAsync(client, code);

        var id = await CreatedIdAsync(await UpsertAsync(
            client, code, "SRC_2001", nodes["S_ATYRAU_ONSHORE_PLANT"].Id, "SRC", "Source 2001",
            new() { ["NaeCode"] = "2001", ["RegionCode"] = "MANGISTAU", ["AreaCode"] = "OFFSHORE", ["AfPath"] = @"\\fake", ["SourceType"] = "T" }));
        var values = await ValuesAsync(client, code, id);
        Assert.Equal(("2001", "ATYRAU", "ONSHORE", "T"), (values.GetProperty("NaeCode").GetString(), values.GetProperty("RegionCode").GetString(), values.GetProperty("AreaCode").GetString(), values.GetProperty("SourceType").GetString()));
        Assert.True(!values.TryGetProperty("AfPath", out var af) || af.ValueKind == JsonValueKind.Null, values.ToString());

        // Into the other area: refused, the source keeps its scope.
        using (var across = await MoveAsync(client, code, id, nodes["S_ATYRAU_OFFSHORE_ISLAND_A"].Id))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, across.StatusCode);
            Assert.Equal("err.ECR-REG-0422.naeMoveChangesScope", (await ProblemAsync(across)).GetProperty("messageKey").GetString());
        }

        // A site with sources into another region: refused too; within the area: fine.
        using (var siteAcross = await MoveAsync(client, code, nodes["S_ATYRAU_ONSHORE_PLANT"].Id, nodes["R_MANGISTAU"].Id))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, siteAcross.StatusCode);
        }

        var plant2 = await CreatedIdAsync(await UpsertAsync(client, code, "PLANT_2", nodes["A_ATYRAU_ONSHORE"].Id, "SITE", "Plant 2"));
        using (var within = await MoveAsync(client, code, id, plant2))
        {
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Definition__fields_are_bound_and_unbound_by_level_code()
    {
        var (client, code, app) = await TreeAsync();
        using var _ = app;
        using var __ = client;

        var definition = await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative)));
        object Save(string? sourceTypeLevel) => new
        {
            reason = "node types",
            fields = definition.GetProperty("fields").EnumerateArray().Select((f, i) => new
            {
                id = f.GetProperty("id").GetInt32(),
                code = f.GetProperty("code").GetString(),
                nameL10n = JsonSerializer.Deserialize<JsonElement>(f.GetProperty("nameL10n").GetRawText()),
                dataType = f.GetProperty("dataType").GetString(),
                ordinal = i + 1,
                isRequired = f.GetProperty("isRequired").GetBoolean(),
                isKey = f.GetProperty("isScopeField").GetBoolean(),
                lookupRegistryDefId = (int?)null,
                unitId = (int?)null,
                levelCode = f.GetProperty("code").GetString() == "SourceType" ? sourceTypeLevel : null,
            }).ToArray(),
            rules = Array.Empty<object>(),
        };

        using (var unknown = await PutDefinitionAsync(client, code, definition, Save("NOPE")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            Assert.Equal("err.ECR-REG-0422.levelUnknown", (await ProblemAsync(unknown)).GetProperty("messageKey").GetString());
        }

        await ReadAsync(await PutDefinitionAsync(client, code, definition, Save("")));
        var after = (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative))))
            .GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("code").GetString()!);
        Assert.Equal(JsonValueKind.Null, after["SourceType"].GetProperty("levelCode").ValueKind);
        Assert.Equal("SRC", after["NaeCode"].GetProperty("levelCode").GetString()); // null keeps the binding

        // The definition audit records the binding: before (SourceType on SRC) and after (all levels).
        await using var db = new EcrDbContext(Options());
        var defId = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
        var srcId = await db.RegistryLevelDefs.Where(l => l.RegistryDefId == defId && l.Code == "SRC").Select(l => l.Id).SingleAsync();
        var audit = await db.Database
            .SqlQuery<string>($"SELECT TOP 1 OldJson + '|' + NewJson AS Value FROM aud.StructureChange WHERE Operation = 'SaveDefinition' AND EntityId = {defId} ORDER BY ChangedAt DESC")
            .SingleAsync();
        var (oldJson, newJson) = (JsonDocument.Parse(audit.Split('|')[0]).RootElement, JsonDocument.Parse(audit.Split('|')[1]).RootElement);
        int? LevelOf(JsonElement snapshot, string field)
        {
            var f = snapshot.GetProperty("fields").EnumerateArray().Single(x => x.GetProperty("Code").GetString() == field);
            return f.GetProperty("LevelId").ValueKind == JsonValueKind.Null ? null : f.GetProperty("LevelId").GetInt32();
        }

        Assert.Equal((srcId, (int?)null, srcId), (LevelOf(oldJson, "SourceType"), LevelOf(newJson, "SourceType"), LevelOf(newJson, "NaeCode")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task LevelChange__into_src_needs_a_free_number_and_out_of_src_is_refused()
    {
        var (client, code, app) = await TreeAsync();
        using var _ = app;
        using var __ = client;
        var plant = (await NodesAsync(client, code))["S_ATYRAU_ONSHORE_PLANT"].Id;
        var source = await CreatedIdAsync(await UpsertAsync(client, code, "ON_A", plant, "SRC", "On A", new() { ["NaeCode"] = "2001" }));

        // Upsert INTO SRC: the number is required, must be free in the region/area, region/area are derived.
        var unit = await CreatedIdAsync(await UpsertAsync(client, code, "UNIT_L", plant, "EQUIP", "Unit L"));
        using (var noNumber = await UpsertAsync(client, code, "UNIT_L", plant, "SRC", "Unit L", id: unit))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noNumber.StatusCode);
            Assert.Equal("err.ECR-REG-0422.requiredFieldsMissing", (await ProblemAsync(noNumber)).GetProperty("messageKey").GetString());
        }

        using (var taken = await UpsertAsync(client, code, "UNIT_L", plant, "SRC", "Unit L", new() { ["NaeCode"] = "2001" }, id: unit))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Equal("err.ECR-REG-4094.naeCodeTaken", (await ProblemAsync(taken)).GetProperty("messageKey").GetString());
        }

        await ReadAsync(await UpsertAsync(client, code, "UNIT_L", plant, "SRC", "Unit L", new() { ["NaeCode"] = "2002" }, id: unit));
        var values = await ValuesAsync(client, code, unit);
        Assert.Equal(("2002", "ATYRAU", "ONSHORE"), (values.GetProperty("NaeCode").GetString(), values.GetProperty("RegionCode").GetString(), values.GetProperty("AreaCode").GetString()));

        // Move INTO SRC: the move carries no values, so a node without a stored number is refused...
        var bare = await CreatedIdAsync(await UpsertAsync(client, code, "UNIT_M", plant, "EQUIP", "Unit M"));
        using (var bareMove = await MoveAsync(client, code, bare, plant, "SRC"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bareMove.StatusCode);
            Assert.Equal("err.ECR-REG-0422.requiredFieldsMissing", (await ProblemAsync(bareMove)).GetProperty("messageKey").GetString());
        }

        // ...a node holding a number (data from before the node types) must find it free, then gets the derived region/area.
        await StoreNumberAsync(code, bare, "2001");
        using (var takenMove = await MoveAsync(client, code, bare, plant, "SRC"))
        {
            Assert.Equal(HttpStatusCode.Conflict, takenMove.StatusCode);

            // The scope check answers first (4094 with the holder), not only the composite key behind it (4092).
            Assert.Equal("err.ECR-REG-4094.naeCodeTaken", (await ProblemAsync(takenMove)).GetProperty("messageKey").GetString());
        }

        await StoreNumberAsync(code, bare, "2003");
        await ReadAsync(await MoveAsync(client, code, bare, plant, "SRC"));
        var moved = await ValuesAsync(client, code, bare);
        Assert.Equal(("2003", "ATYRAU", "ONSHORE"), (moved.GetProperty("NaeCode").GetString(), moved.GetProperty("RegionCode").GetString(), moved.GetProperty("AreaCode").GetString()));
        Assert.Equal("SRC", (await NodesAsync(client, code))["UNIT_M"].Level);

        // OUT of SRC: refused by move and by upsert; the source stays a source.
        using (var outMove = await MoveAsync(client, code, source, plant, "EQUIP"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, outMove.StatusCode);
            Assert.Equal("err.ECR-REG-0422.naeSourceLevelChange", (await ProblemAsync(outMove)).GetProperty("messageKey").GetString());
        }

        using (var outUpsert = await UpsertAsync(client, code, "ON_A", plant, "EQUIP", "On A", id: source))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, outUpsert.StatusCode);
            Assert.Equal("err.ECR-REG-0422.naeSourceLevelChange", (await ProblemAsync(outUpsert)).GetProperty("messageKey").GetString());
        }

        Assert.Equal("SRC", (await NodesAsync(client, code))["ON_A"].Level);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Upgrade__a_tree_from_before_the_node_types_is_bound_once_on_start()
    {
        var (client, code, app) = await TreeAsync();
        app.Dispose();
        client.Dispose();

        await using (var db = new EcrDbContext(Options()))
        {
            var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE cfg.RegistryFieldDef SET LevelId = NULL, IsRequired = 0 WHERE RegistryDefId = {id}");
            await db.Database.ExecuteSqlAsync($"UPDATE cfg.RegistryLevelDef SET AllowsSelfNesting = 0 WHERE RegistryDefId = {id}");
        }

        using var upgraded = new EcrApiFactory(sql) { ExtraServices = s => s.AddSingleton(new AirLimitsSettings(code, "NO_SUBSTANCES_" + code)) };
        serverErrors = () => upgraded.ErrorsText;
        using var started = upgraded.CreateClient();

        async Task<(int Version, int Bound, bool Required, bool SiteNests)> StateAsync()
        {
            await using var db = new EcrDbContext(Options());
            var def = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Code == code);
            var site = await db.RegistryLevelDefs.SingleAsync(l => l.RegistryDefId == def.Id && l.Code == "SITE");
            return (def.DefinitionVersion, def.Fields.Count(f => f.LevelId != null), def.Fields.Single(f => f.Code == "NaeCode").IsRequired, site.AllowsSelfNesting);
        }

        // The start (or this explicit call, whichever runs first) upgrades; every later call changes nothing.
        await using (var scope = upgraded.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AirTypeSystemProvisioner>().EnsureNaeTreeAsync(CancellationToken.None);
        }

        var once = await StateAsync();
        Assert.Equal((5, true, true), (once.Bound, once.Required, once.SiteNests));

        // Audit: exactly one system row — before without levels, after with them (the same snapshot shape as SaveDefinition).
        var audit = await UpgradeAuditAsync();
        Assert.Single(audit);
        Assert.Equal(0, audit[0].UserId);
        Assert.DoesNotContain("\"LevelId\":", audit[0].Old.Replace("\"LevelId\":null", string.Empty, StringComparison.Ordinal));
        Assert.Contains("\"LevelId\":", audit[0].New.Replace("\"LevelId\":null", string.Empty, StringComparison.Ordinal));
        Assert.Contains("\"definitionVersion\":" + (once.Version - 1), audit[0].Old);
        Assert.Contains("\"definitionVersion\":" + once.Version, audit[0].New);

        await using (var scope = upgraded.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AirTypeSystemProvisioner>().EnsureNaeTreeAsync(CancellationToken.None);
        }

        Assert.Equal(once, await StateAsync());
        Assert.Single(await UpgradeAuditAsync());

        async Task<List<(int UserId, string Old, string New)>> UpgradeAuditAsync()
        {
            await using var db = new EcrDbContext(Options());
            var defId = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            var rows = await db.Database
                .SqlQuery<string>($"SELECT CONCAT(ChangedByUserId, CHAR(1), OldJson, CHAR(1), NewJson) AS Value FROM aud.StructureChange WHERE Operation = 'UpgradeNodeTypes' AND EntityId = {defId}")
                .ToListAsync();
            return [.. rows.Select(r => r.Split('\u0001')).Select(p => (int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p[1], p[2]))];
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------------------------------

    private static NaeAfElement El(string relative, string? template, string? en = null)
        => new($@"{Root}\{relative}", relative.Split('\\')[^1], en, null, null, null, null, template);

    /// <summary>Atyrau: Offshore/Island_A/Block_2/Gen_1/1085 (site in site, unit), Onshore/Plant; Mangistau (no areas): ZPK_1/1085.</summary>
    private static List<NaeAfElement> Standard() =>
    [
        El(@"Atyrau", "Air_Region"),
        El(@"Atyrau\Offshore", "Air_OnShoreOffShore"),
        El(@"Atyrau\Offshore\Island_A", "Air_Area", "Island A"),
        El(@"Atyrau\Offshore\Island_A\Block_2", "Air_Area", "Block 2"),
        El(@"Atyrau\Offshore\Island_A\Block_2\Gen_1", "Air_Unit", "Generator 1"),
        El(@"Atyrau\Offshore\Island_A\Block_2\Gen_1\1085", "Equipment", "Stack 1085"),
        El(@"Atyrau\Onshore", "Air_OnShoreOffShore"),
        El(@"Atyrau\Onshore\Plant", "Air_Area", "Plant"),
        El(@"Mangistau", "Air_Region"),
        El(@"Mangistau\ZPK_1", "Air_Area", "ZPK 1"),
        El(@"Mangistau\ZPK_1\1085", "Equipment", "ZPK 1085"),
    ];

    private sealed class FakeAf(IEnumerable<NaeAfElement> elements) : INaeAfReader
    {
        public Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct)
            => Task.FromResult(new NaeAfSnapshot([.. elements], true));
    }

    private async Task<(HttpClient Client, string Code, EcrApiFactory App)> TreeAsync()
    {
        var code = $"NAEN{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var app = new EcrApiFactory(sql) { ExtraServices = s => s.AddSingleton<INaeAfReader>(new FakeAf(Standard())) };
        serverErrors = () => app.ErrorsText;
        var client = await SignedInAsync(app);
        var report = await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/af-import", UriKind.Relative),
            new { dataSourceId = 1, rootPath = Root, registryCode = code, dryRun = false }));
        Assert.True(report.GetProperty("applied").GetBoolean(), report.ToString());
        Assert.Equal(0, report.GetProperty("skipped").GetInt32());
        return (client, code, app);
    }

    private async Task<Dictionary<string, (long Id, long? Parent, string Level)>> NodesAsync(HttpClient client, string code)
        => (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/tree", UriKind.Relative))))
            .GetProperty("nodes").EnumerateArray()
            .ToDictionary(
                n => n.GetProperty("code").GetString()!,
                n => (n.GetProperty("id").GetInt64(),
                      n.GetProperty("parentEntryId").ValueKind == JsonValueKind.Null ? (long?)null : n.GetProperty("parentEntryId").GetInt64(),
                      n.GetProperty("levelCode").GetString()!));

    private async Task<JsonElement> ValuesAsync(HttpClient client, string code, long id)
        => (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/entries/{id}", UriKind.Relative)))).GetProperty("values");

    private static (string? Level, bool Required, bool ReadOnly) Field(JsonElement f)
        => (f.GetProperty("levelCode").GetString(), f.GetProperty("isRequired").GetBoolean(), f.GetProperty("isReadOnly").GetBoolean());

    private static async Task<HttpResponseMessage> UpsertAsync(
        HttpClient client, string registry, string code, long parent, string level, string? en, Dictionary<string, object?>? values = null, string? ru = null, long? id = null)
    {
        var registryId = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/registries/{registry}/definition", UriKind.Relative))).GetProperty("id").GetInt32();
        var names = new Dictionary<string, string>();
        if (en is not null)
        {
            names["en"] = en;
        }

        if (ru is not null)
        {
            names["ru"] = ru;
        }

        return await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry}/entries", UriKind.Relative),
            new { id, registryDefId = registryId, code, display = new { values = names }, parentEntryId = parent, levelCode = level, values = values ?? [] });
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, string registry, long id, long parent, string? level = null)
        => client.PostAsJsonAsync(new Uri($"/api/v1/registries/{registry}/entries/{id}/move", UriKind.Relative), new { parentEntryId = parent, levelCode = level });

    /// <summary>A number stored on a node directly (data from before the node types: the API refuses it on a group node).</summary>
    private async Task StoreNumberAsync(string registry, long entryId, string number)
    {
        await using var db = new EcrDbContext(Options());
        var def = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Code == registry);
        var field = def.Fields.Single(f => f.Code == "NaeCode");
        var value = await db.RegistryValues.SingleOrDefaultAsync(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == field.Id);
        if (value is null)
        {
            value = new Ecr.Domain.Entities.Dictionaries.RegistryValue(entryId, field.Id);
            db.RegistryValues.Add(value);
        }

        value.Set(CellDataType.String, number, null);
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> PutDefinitionAsync(HttpClient client, string registry, JsonElement definition, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/registries/{registry}/definition", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{definition.GetProperty("definitionVersion").GetInt32()}\"");
        return await client.SendAsync(request);
    }

    private async Task<long> CreatedIdAsync(HttpResponseMessage response)
        => (await ReadAsync(response)).GetProperty("id").GetInt64();

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text} | {serverErrors?.Invoke()}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"naen_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("NAE node types test"));
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
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
