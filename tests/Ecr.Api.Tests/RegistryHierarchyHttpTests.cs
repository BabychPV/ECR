// tests/Ecr.Api.Tests/RegistryHierarchyHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// NAE plan N1: hierarchical registries end to end (HTTP + SQL Server): access matrix of the five new routes, level list
/// validation, placement rules on upsert and move (order of levels, optional levels, cycles, subtree), tree read (validity, search,
/// ordering), soft retire (never deletes), audit, and the move race.
/// </summary>
/// <remarks>
/// Mutation proofs (local, not committed): drop the permission call in a handler → the matching access-matrix case turns 200;
/// bypass <c>CheckPlacement</c> → the level-order tests turn 200; bypass <c>WouldCycle</c> → the cycle tests turn 200;
/// remove the definition-row lock in <c>MoveRegistryEntryHandler</c> → the race test produces a cyclic tree.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryHierarchyHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Hierarchy-2026!";
    private const string View = "Registry.View";
    private const string EditData = "Registry.EditData";
    private const string EditDefinition = "Registry.EditDefinition";

    // ---- access matrix -------------------------------------------------------------------------------------------------

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("", 403, 403, 403, 403, 403)]
    [InlineData(View, 200, 403, 200, 403, 403)]
    [InlineData(EditData, 403, 403, 403, 200, 200)]
    [InlineData(EditDefinition, 403, 200, 403, 403, 403)]
    [InlineData("Registry.View,Registry.EditData,Registry.EditDefinition", 200, 200, 200, 200, 200)]
    public async Task Access_matrix__each_route_needs_its_own_permission(
        string permissions, int getHierarchy, int putHierarchy, int getTree, int move, int retire)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, permissions.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var f = await SeedAsync();
        var leaf = f.Nodes["SRC1"];

        Assert.Equal((HttpStatusCode)getHierarchy, (await client.GetAsync(Url(f, "hierarchy"))).StatusCode);
        Assert.Equal((HttpStatusCode)putHierarchy, (await client.PutAsJsonAsync(Url(f, "hierarchy"), SameLevels())).StatusCode);
        Assert.Equal((HttpStatusCode)getTree, (await client.GetAsync(Url(f, "tree"))).StatusCode);
        Assert.Equal(
            (HttpStatusCode)move,
            (await client.PostAsJsonAsync(
                Url(f, $"entries/{leaf.Id}/move"),
                new { parentEntryId = (long?)leaf.ParentEntryId, levelCode = (string?)null, beforeEntryId = (long?)null })).StatusCode);
        Assert.Equal(
            (HttpStatusCode)retire,
            (await client.PostAsJsonAsync(Url(f, $"entries/{leaf.Id}/retire"), new { on = (string?)null, cascade = false })).StatusCode);
    }

    // ---- level list ----------------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Levels__save_validates_the_list_and_replaces_it_atomically()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync(withNodes: false);

        // Bad lists: nothing is written.
        var tooMany = Enumerable.Range(1, 7).Select(i => Level($"L{i}")).ToArray();
        await AssertProblemAsync(await PutAsync(client, f, true, tooMany), 422, "err.ECR-REG-0422.hierarchyLevelsInvalid");
        await AssertProblemAsync(await PutAsync(client, f, true, []), 422, "err.ECR-REG-0422.hierarchyLevelsInvalid");
        await AssertProblemAsync(await PutAsync(client, f, true, [Level("A"), Level("a")]), 422, "err.ECR-REG-0422.hierarchyLevelsInvalid");
        await AssertProblemAsync(
            await PutAsync(client, f, true, [new { code = "A", name = new { values = new Dictionary<string, string> { ["ru"] = "а" } }, isOptional = false }]),
            422,
            "err.ECR-REG-0422.hierarchyLevelsInvalid");
        await AssertProblemAsync(await PutAsync(client, f, false, [Level("A")]), 422, "err.ECR-REG-0422.hierarchyLevelsInvalid");
        Assert.Equal(5, (await HierarchyAsync(client, f)).GetProperty("levels").GetArrayLength());

        // Swap two levels (no entries use them) — the unique ordinal index must not trip.
        var swapped = await PutAsync(client, f, true, [Level("SITE"), Level("REGION"), Level("SRC")]);
        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        var levels = (await swapped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("levels");
        Assert.Equal(["SITE", "REGION", "SRC"], levels.EnumerateArray().Select(l => l.GetProperty("code").GetString()!).ToArray());
        Assert.Equal([1, 2, 3], levels.EnumerateArray().Select(l => l.GetProperty("ordinal").GetInt32()).ToArray());
        Assert.Equal(1, await CountAuditAsync("SaveHierarchy", "cfg.RegistryDef", f.Id) - 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Levels__in_use_cannot_be_removed_and_an_order_that_breaks_entries_is_refused()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync();

        // SITE holds entries → removing it is a 409 naming the level.
        var removeSite = await PutAsync(client, f, true, [Level("REGION"), Level("AREA", optional: true), Level("EQUIP", optional: true), Level("SRC")]);
        await AssertProblemAsync(removeSite, 409, "err.ECR-REG-0409.levelInUse");

        // Making the required SITE level come AFTER the sources breaks every source under a site → 422, nothing written.
        var reordered = await PutAsync(client, f, true, [Level("REGION"), Level("AREA", optional: true), Level("EQUIP", optional: true), Level("SRC"), Level("SITE")]);
        await AssertProblemAsync(reordered, 422, "err.ECR-REG-0422.hierarchyLevelChange");
        Assert.Equal(
            ["REGION", "AREA", "SITE", "EQUIP", "SRC"],
            (await HierarchyAsync(client, f)).GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("code").GetString()!).ToArray());

        // Turning hierarchy off while levels are in use is refused too.
        await AssertProblemAsync(await PutAsync(client, f, false, []), 409, "err.ECR-REG-0409.levelInUse");
    }

    // ---- placement on upsert -------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Upsert__level_is_required_known_and_ordered()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync(withNodes: false);

        // Level required / unknown.
        await AssertProblemAsync(await UpsertAsync(client, f, "R1", null, null), 422, "err.ECR-REG-0422.levelRequired");
        await AssertProblemAsync(await UpsertAsync(client, f, "R1", null, "NOPE"), 422, "err.ECR-REG-0422.levelUnknown");

        // A root must start at the first level unless the levels above it are optional.
        await AssertProblemAsync(await UpsertAsync(client, f, "S0", null, "SITE"), 422, "err.ECR-REG-0422.levelSkipsRequired");
        var region = await CreatedIdAsync(await UpsertAsync(client, f, "R1", null, "REGION"));

        // Optional AREA may be skipped (SITE straight under REGION); a child on the same or a shallower level is refused.
        var site = await CreatedIdAsync(await UpsertAsync(client, f, "S1", region, "SITE"));
        await AssertProblemAsync(await UpsertAsync(client, f, "S2", site, "SITE"), 422, "err.ECR-REG-0422.levelOrder");
        await AssertProblemAsync(await UpsertAsync(client, f, "S3", site, "REGION"), 422, "err.ECR-REG-0422.levelOrder");

        // A required level may not be skipped: SRC directly under REGION skips the required SITE.
        await AssertProblemAsync(await UpsertAsync(client, f, "N1", region, "SRC"), 422, "err.ECR-REG-0422.levelSkipsRequired");

        // EQUIP is optional → SRC under SITE is fine, SRC under EQUIP is fine.
        var src = await CreatedIdAsync(await UpsertAsync(client, f, "N2", site, "SRC"));
        Assert.True(src > 0);

        // A parent of another registry is the same 404 as a missing one.
        var other = await SeedAsync(withNodes: false);
        var foreignRegion = await CreatedIdAsync(await UpsertAsync(client, other, "R9", null, "REGION"));
        Assert.Equal(HttpStatusCode.NotFound, (await UpsertAsync(client, f, "S9", foreignRegion, "SITE")).StatusCode);

        // The detail carries the level code.
        var detail = await client.GetFromJsonAsync<JsonElement>(Url(f, $"entries/{site}"));
        Assert.Equal("SITE", detail.GetProperty("levelCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Upsert__update_cannot_create_a_cycle_or_strand_descendants()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync();
        var region = f.Nodes["REG1"];
        var site = f.Nodes["SITE1"];
        var src = f.Nodes["SRC1"];

        // Re-parent the region under its own descendant.
        await AssertProblemAsync(await UpsertAsync(client, f, region.Code, src.Id, "SRC", id: region.Id), 422, "err.ECR-REG-0422.hierarchyCycle");

        // Change a site's level to the first one: its sources would still sit deeper, but the site is no longer under REGION's order.
        await AssertProblemAsync(await UpsertAsync(client, f, site.Code, region.Id, "REGION", id: site.Id), 422, "err.ECR-REG-0422.levelOrder");

        // Push a site down to the last level: the required SITE level would be skipped.
        await AssertProblemAsync(await UpsertAsync(client, f, site.Code, region.Id, "SRC", id: site.Id), 422, "err.ECR-REG-0422.levelSkipsRequired");
    }

    // ---- tree ----------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Tree__flat_list_with_levels_childCount_order_validity_and_search()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync();

        var tree = await client.GetFromJsonAsync<JsonElement>(Url(f, "tree"));
        Assert.True(tree.GetProperty("isHierarchical").GetBoolean());
        var nodes = tree.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(f.Nodes.Count, nodes.Count);

        JsonElement Node(string key) => nodes.Single(n => n.GetProperty("id").GetInt64() == f.Nodes[key].Id);
        Assert.Equal("REGION", Node("REG1").GetProperty("levelCode").GetString());
        Assert.Equal(JsonValueKind.Null, Node("REG1").GetProperty("parentEntryId").ValueKind);
        Assert.Equal(2, Node("SITE1").GetProperty("childCount").GetInt32());
        Assert.Equal(f.Nodes["SITE1"].Id, Node("SRC1").GetProperty("parentEntryId").GetInt64());

        // Siblings come ordered by ordinal, then code.
        var order = nodes.Where(n => n.TryGetProperty("parentEntryId", out var p) && p.ValueKind != JsonValueKind.Null && p.GetInt64() == f.Nodes["SITE1"].Id)
            .Select(n => n.GetProperty("code").GetString()!).ToArray();
        Assert.Equal([f.Nodes["SRC1"].Code, f.Nodes["SRC2"].Code], order);

        // Search returns matches with their ancestors; only matches are flagged.
        var found = await client.GetFromJsonAsync<JsonElement>(Url(f, $"tree?q={f.Nodes["SRC2"].Code}"));
        var foundNodes = found.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(
            new List<string> { "REG1", "SITE1", "SRC2" }.Select(k => f.Nodes[k].Id).Order().ToArray(),
            foundNodes.Select(n => n.GetProperty("id").GetInt64()).Order().ToArray());
        Assert.Equal([f.Nodes["SRC2"].Id], foundNodes.Where(n => n.GetProperty("isMatch").GetBoolean()).Select(n => n.GetProperty("id").GetInt64()).ToArray());

        // Retired nodes disappear by default (with their subtree) and come back with includeRetired.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url(f, $"entries/{f.Nodes["SITE1"].Id}/retire"), new { on = "2026-01-01", cascade = true })).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>(Url(f, "tree?asOf=2026-06-01"));
        Assert.Equal(f.Nodes.Count - 3, after.GetProperty("nodes").GetArrayLength());
        var all = await client.GetFromJsonAsync<JsonElement>(Url(f, "tree?asOf=2026-06-01&includeRetired=true"));
        Assert.Equal(f.Nodes.Count, all.GetProperty("nodes").GetArrayLength());
        var before = await client.GetFromJsonAsync<JsonElement>(Url(f, "tree?asOf=2025-12-31"));
        Assert.Equal(f.Nodes.Count, before.GetProperty("nodes").GetArrayLength());
    }

    // ---- move ----------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Move__reparents_reorders_and_audits()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync();
        var site1 = f.Nodes["SITE1"];
        var site2 = f.Nodes["SITE2"];
        var src2 = f.Nodes["SRC2"];

        // src2 → under site2.
        var moved = await MoveAsync(client, f, src2.Id, site2.Id, null, null);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await moved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(site2.Id, body.GetProperty("parentEntryId").GetInt64());
        Assert.Equal("SRC", body.GetProperty("levelCode").GetString());
        await using (var db = new EcrDbContext(Options()))
        {
            Assert.Equal(site2.Id, (await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == src2.Id)).ParentEntryId);
        }

        Assert.Equal(1, await CountAuditAsync("Move", "dic.RegistryEntry", src2.Id));

        // Order: put src1 before the first child of site2 → ordinals 1, 2.
        var first = await ChildrenAsync(client, f, site2.Id);
        var src1 = f.Nodes["SRC1"];
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, f, src1.Id, site2.Id, null, first[0])).StatusCode);
        var ordered = await ChildrenAsync(client, f, site2.Id);
        Assert.Equal([src1.Id, first[0]], ordered.Take(2).ToArray());

        // beforeEntryId that is not a child of the target parent.
        await AssertProblemAsync(await MoveAsync(client, f, src1.Id, site1.Id, null, first[0]), 422, "err.ECR-REG-0422.moveBeforeNotSibling");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Move__refuses_cycles_wrong_levels_foreign_entries_and_flat_registries()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync();
        var reg = f.Nodes["REG1"];
        var site1 = f.Nodes["SITE1"];
        var src1 = f.Nodes["SRC1"];

        // The region under its own descendant, expressed with a deeper level so the order rule alone would pass → pure cycle.
        await AssertProblemAsync(await MoveAsync(client, f, reg.Id, src1.Id, "EQUIP", null), 422, "err.ECR-REG-0422.hierarchyCycle");
        await AssertProblemAsync(await MoveAsync(client, f, site1.Id, site1.Id, "EQUIP", null), 422, "err.ECR-REG-0422.hierarchyCycle");

        // Level order and skipped required level.
        await AssertProblemAsync(await MoveAsync(client, f, src1.Id, f.Nodes["SRC2"].Id, null, null), 422, "err.ECR-REG-0422.levelOrder");
        await AssertProblemAsync(await MoveAsync(client, f, src1.Id, reg.Id, null, null), 422, "err.ECR-REG-0422.levelSkipsRequired");

        // Changing the level of a node whose descendants would no longer be deeper.
        await AssertProblemAsync(await MoveAsync(client, f, site1.Id, reg.Id, "SRC", null), 422, "err.ECR-REG-0422.levelSkipsRequired");

        // Unknown level / missing parent / foreign entry.
        await AssertProblemAsync(await MoveAsync(client, f, src1.Id, site1.Id, "NOPE", null), 422, "err.ECR-REG-0422.levelUnknown");
        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(client, f, src1.Id, 999_999_999, null, null)).StatusCode);
        var other = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await MoveAsync(client, f, other.Nodes["SRC1"].Id, site1.Id, null, null)).StatusCode);

        // A flat registry has no tree to move in.
        var flat = await SeedFlatAsync();
        await AssertProblemAsync(await MoveAsync(client, flat, flat.Nodes["E1"].Id, null, null, null), 422, "err.ECR-REG-0422.hierarchyNotEnabled");

        // Nothing moved.
        await using var db = new EcrDbContext(Options());
        Assert.Equal(site1.Id, (await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == src1.Id)).ParentEntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Move__opposite_moves_never_make_a_cycle()
    {
        // Race: A(l1) and D(l1) are roots, C(l3) is under D. Move A under C (as l4) and D under A (as l2) at the same time:
        // each is valid against the snapshot, together they close a loop. The definition-row lock serialises them, the second one
        // sees the first and is refused. 20 rounds.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedRaceAsync();

        for (var round = 0; round < 20; round++)
        {
            await ResetRaceAsync(f);
            var a = f.Nodes["A"].Id;
            var c = f.Nodes["C"].Id;
            var d = f.Nodes["D"].Id;

            var m1 = MoveAsync(client, f, a, c, "SRC", null);
            var m2 = MoveAsync(client, f, d, a, "AREA", null);
            var responses = await Task.WhenAll(m1, m2);

            var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
            Assert.True(
                statuses.All(s => s is 200 or 422),
                $"round {round}: {string.Join(",", statuses)} {await responses[0].Content.ReadAsStringAsync()} {await responses[1].Content.ReadAsStringAsync()}\n{app.ErrorsText}");

            await using var db = new EcrDbContext(Options());
            var parents = await db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == f.Id)
                .ToDictionaryAsync(e => e.Id, e => e.ParentEntryId);
            foreach (var start in parents.Keys)
            {
                var cursor = (long?)start;
                for (var step = 0; cursor is { } id && step <= 10; step++)
                {
                    cursor = parents[id];
                    Assert.True(step < 10, $"round {round}: cycle through {start}");
                }
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Move__refuses_a_level_change_that_strands_descendants()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedRaceAsync();

        // D (REGION) holds C (SITE). D under A as SITE is a legal place for D alone, but C would no longer be deeper.
        await AssertProblemAsync(await MoveAsync(client, f, f.Nodes["D"].Id, f.Nodes["A"].Id, "SITE", null), 422, "err.ECR-REG-0422.levelOrder");

        // D under A as AREA keeps C (SITE) deeper, so it is allowed.
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(client, f, f.Nodes["D"].Id, f.Nodes["A"].Id, "AREA", null)).StatusCode);
    }

    // ---- retire --------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Retire__is_soft_cascades_on_request_and_never_deletes_a_referenced_node()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData, EditDefinition);
        var f = await SeedAsync();
        var site1 = f.Nodes["SITE1"];
        var src1 = f.Nodes["SRC1"];

        // Live children without cascade → 409, nothing closed.
        await AssertProblemAsync(await RetireAsync(client, f, site1.Id, "2026-03-01", cascade: false), 409, "err.ECR-REG-0409.nodeHasLiveChildren");
        await using (var db = new EcrDbContext(Options()))
        {
            Assert.Null((await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == site1.Id)).ValidTo);
        }

        // A leaf can be retired alone; the response lists exactly it.
        var leaf = await RetireAsync(client, f, src1.Id, "2026-03-01", cascade: false);
        Assert.Equal(HttpStatusCode.OK, leaf.StatusCode);
        Assert.Equal([src1.Id], (await leaf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retiredIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());

        // Cascade closes the node and its remaining live subtree; the already-retired leaf keeps its earlier end date.
        var cascade = await RetireAsync(client, f, site1.Id, "2026-04-01", cascade: true);
        Assert.Equal(HttpStatusCode.OK, cascade.StatusCode);
        await using (var db = new EcrDbContext(Options()))
        {
            var rows = await db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == f.Id).ToDictionaryAsync(e => e.Id);
            Assert.Equal(new DateOnly(2026, 4, 1), rows[site1.Id].ValidTo);
            Assert.Equal(new DateOnly(2026, 4, 1), rows[f.Nodes["SRC2"].Id].ValidTo);
            Assert.Equal(new DateOnly(2026, 3, 1), rows[src1.Id].ValidTo);
            Assert.All(rows.Values, e => Assert.False(e.IsDeleted));
        }

        Assert.True(await CountAuditAsync("Retire", "dic.RegistryEntry", site1.Id) == 1);

        // Hard delete of a node with children stays refused (the guard that protects referenced nodes).
        var reg = f.Nodes["REG1"];
        var del = await client.DeleteAsync(Url(f, $"entries/{reg.Id}"));
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }

    // ---- fixtures ------------------------------------------------------------------------------------------------------

    private static Uri Url(Fixture f, string tail) => new($"/api/v1/registries/{f.Code}/{tail}", UriKind.Relative);

    private static object Level(string code, bool optional = false)
        => new { code, name = new { values = new Dictionary<string, string> { ["en"] = code } }, isOptional = optional };

    private static object SameLevels() => new
    {
        isHierarchical = true,
        levels = new[] { Level("REGION"), Level("AREA", true), Level("SITE"), Level("EQUIP", true), Level("SRC") },
    };

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Fixture f, bool hierarchical, object[] levels)
        => client.PutAsJsonAsync(Url(f, "hierarchy"), new { isHierarchical = hierarchical, levels });

    private static async Task<JsonElement> HierarchyAsync(HttpClient client, Fixture f)
        => await client.GetFromJsonAsync<JsonElement>(Url(f, "hierarchy"));

    private static Task<HttpResponseMessage> UpsertAsync(
        HttpClient client, Fixture f, string code, long? parent, string? level, long? id = null)
        => client.PostAsJsonAsync(
            Url(f, "entries"),
            new
            {
                id,
                registryDefId = f.Id,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = parent,
                values = new Dictionary<string, object?>(),
                levelCode = level,
            });

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Fixture f, long id, long? parent, string? level, long? before)
        => client.PostAsJsonAsync(Url(f, $"entries/{id}/move"), new { parentEntryId = parent, levelCode = level, beforeEntryId = before });

    private static Task<HttpResponseMessage> RetireAsync(HttpClient client, Fixture f, long id, string on, bool cascade)
        => client.PostAsJsonAsync(Url(f, $"entries/{id}/retire"), new { on, cascade });

    private static async Task<long> CreatedIdAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt64();
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string messageKey)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == status, $"expected {status} got {(int)response.StatusCode}: {text}");
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
        }
    }

    private static async Task<long[]> ChildrenAsync(HttpClient client, Fixture f, long parent)
    {
        var tree = await client.GetFromJsonAsync<JsonElement>(Url(f, "tree"));
        return tree.GetProperty("nodes").EnumerateArray()
            .Where(n => n.GetProperty("parentEntryId").ValueKind != JsonValueKind.Null && n.GetProperty("parentEntryId").GetInt64() == parent)
            .OrderBy(n => n.GetProperty("ordinal").GetInt32()).ThenBy(n => n.GetProperty("code").GetString(), StringComparer.Ordinal)
            .Select(n => n.GetProperty("id").GetInt64()).ToArray();
    }

    private async Task<int> CountAuditAsync(string operation, string entityType, long entityId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = @o AND EntityType = @t AND EntityId = @id";
        command.Parameters.AddWithValue("@o", operation);
        command.Parameters.AddWithValue("@t", entityType);
        command.Parameters.AddWithValue("@id", (int)entityId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// A hierarchical registry: levels REGION, AREA (optional), SITE, EQUIP (optional), SRC; with nodes
    /// REG1 → SITE1 → {SRC1, SRC2} and REG1 → SITE2 (AREA skipped).
    /// </summary>
    private async Task<Fixture> SeedAsync(bool withNodes = true)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"NT{tag}"), Name($"Hierarchy {tag}"), isTemporal: false);
        registry.UseHierarchy(true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var levels = new Dictionary<string, RegistryLevelDef>();
        var spec = new[] { ("REGION", false), ("AREA", true), ("SITE", false), ("EQUIP", true), ("SRC", false) };
        for (var i = 0; i < spec.Length; i++)
        {
            var level = new RegistryLevelDef(registry.Id, i + 1, EcrCode.Create(spec[i].Item1), Name(spec[i].Item1), spec[i].Item2);
            db.RegistryLevelDefs.Add(level);
            levels[spec[i].Item1] = level;
        }

        await db.SaveChangesAsync();

        var nodes = new Dictionary<string, RegistryEntry>();
        if (withNodes)
        {
            async Task<RegistryEntry> Add(string key, string level, string? parent, int ordinal)
            {
                var entry = new RegistryEntry(registry.Id, EcrCode.Create($"{key}_{tag}"), Name(key), 0, DateTime.UtcNow);
                entry.SetLevel(levels[level].Id);
                entry.SetOrdinal(ordinal);
                if (parent is not null)
                {
                    entry.SetParent(nodes[parent].Id);
                }

                db.RegistryEntries.Add(entry);
                await db.SaveChangesAsync();
                nodes[key] = entry;
                return entry;
            }

            await Add("REG1", "REGION", null, 1);
            await Add("SITE1", "SITE", "REG1", 1);
            await Add("SITE2", "SITE", "REG1", 2);
            await Add("SRC1", "SRC", "SITE1", 1);
            await Add("SRC2", "SRC", "SITE1", 2);
        }

        return new Fixture(registry.Id, registry.Code, tag, nodes);
    }

    private async Task<Fixture> SeedFlatAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());
        var registry = new RegistryDef(EcrCode.Create($"NF{tag}"), Name($"Flat {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        var entry = new RegistryEntry(registry.Id, EcrCode.Create($"E1_{tag}"), Name("E1"), 0, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return new Fixture(registry.Id, registry.Code, tag, new Dictionary<string, RegistryEntry> { ["E1"] = entry });
    }

    /// <summary>Levels REGION, AREA, SITE; roots A (REGION) and D (REGION), C (AREA) under D.</summary>
    private async Task<Fixture> SeedRaceAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());
        var registry = new RegistryDef(EcrCode.Create($"NR{tag}"), Name($"Race {tag}"), isTemporal: false);
        registry.UseHierarchy(true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var region = new RegistryLevelDef(registry.Id, 1, EcrCode.Create("REGION"), Name("REGION"), false);
        var area = new RegistryLevelDef(registry.Id, 2, EcrCode.Create("AREA"), Name("AREA"), true);
        var site = new RegistryLevelDef(registry.Id, 3, EcrCode.Create("SITE"), Name("SITE"), false);
        var src = new RegistryLevelDef(registry.Id, 4, EcrCode.Create("SRC"), Name("SRC"), false);
        db.RegistryLevelDefs.AddRange(region, area, site, src);
        await db.SaveChangesAsync();

        var nodes = new Dictionary<string, RegistryEntry>();
        foreach (var (key, level, parent) in new[] { ("A", region, (string?)null), ("D", region, null), ("C", site, "D") })
        {
            var entry = new RegistryEntry(registry.Id, EcrCode.Create($"{key}_{tag}"), Name(key), 0, DateTime.UtcNow);
            entry.SetLevel(level.Id);
            if (parent is not null)
            {
                entry.SetParent(nodes[parent].Id);
            }

            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            nodes[key] = entry;
        }

        return new Fixture(registry.Id, registry.Code, tag, nodes);
    }

    private async Task ResetRaceAsync(Fixture f)
    {
        await using var db = new EcrDbContext(Options());
        var regionLevel = (await db.RegistryLevelDefs.SingleAsync(l => l.RegistryDefId == f.Id && l.Code == "REGION")).Id;
        var siteLevel = (await db.RegistryLevelDefs.SingleAsync(l => l.RegistryDefId == f.Id && l.Code == "SITE")).Id;
        var entries = await db.RegistryEntries.Where(e => e.RegistryDefId == f.Id).ToListAsync();
        foreach (var e in entries)
        {
            var key = f.Nodes.Single(n => n.Value.Id == e.Id).Key;
            e.SetParent(key == "C" ? f.Nodes["D"].Id : null);
            e.SetLevel(key == "C" ? siteLevel : regionLevel);
        }

        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"hier_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry hierarchy test"));
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

    private sealed record Fixture(int Id, string Code, string Tag, Dictionary<string, RegistryEntry> Nodes);
}
