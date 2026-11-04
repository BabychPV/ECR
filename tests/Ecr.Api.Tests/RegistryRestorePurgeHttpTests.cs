// tests/Ecr.Api.Tests/RegistryRestorePurgeHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// NAE tree (human 04.11: «a Restore button and full deletion if it is not used anywhere») end to end over HTTP + SQL Server:
/// <c>POST …/entries/{id}/unretire</c> and <c>DELETE …/entries/{id}/purge</c> — access, parent rule, NAE number and registry
/// key conflicts on restore, the two-step purge (retire first), where-used kinds, audit, and the DB-level FK coverage.
/// </summary>
/// <remarks>
/// Mutation proofs (local worktree, not committed) — see the report of the lane: drop the permission call in either handler →
/// the access theory turns 200/204; drop the <c>purgeActive</c> check → the active purge test deletes a live entry; drop the
/// children / cells / registry-values / snapshots kind in <c>RegistryStore.FindHardReferencesAsync</c> → the matching
/// <c>Purge__refused_while_used</c> case turns 409 <c>other</c> (FK) or 204 (snapshot, no FK); drop <c>EnsureCodeFreeAsync</c> in
/// unretire → the NAE case turns 200.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryRestorePurgeHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Restore-Purge-2026!";
    private const string View = "Registry.View";
    private const string EditData = "Registry.EditData";
    private const string Root = @"\\AF\ECR\ECR_01_Air";

    private Func<string>? serverErrors;

    // ---- access --------------------------------------------------------------------------------------------------------

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("", 403, 403)]
    [InlineData(View, 403, 403)]
    [InlineData(EditData, 200, 204)]
    public async Task Access__restore_and_purge_need_EditData_or_a_write_grant(string permissions, int unretire, int purge)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, permissions.Split(',', StringSplitOptions.RemoveEmptyEntries));
        var f = await SeedAsync();
        await CloseAsync(f.Nodes["SRC1"].Id, new DateOnly(2026, 1, 1));
        await CloseAsync(f.Nodes["SRC2"].Id, new DateOnly(2026, 1, 1));

        Assert.Equal((HttpStatusCode)unretire, (await UnretireAsync(client, f, f.Nodes["SRC1"].Id)).StatusCode);
        Assert.Equal((HttpStatusCode)purge, (await PurgeAsync(client, f, f.Nodes["SRC2"].Id)).StatusCode);
        Assert.Equal(purge != 204, await ExistsAsync(f.Nodes["SRC2"].Id));
    }

    // ---- unretire ------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Unretire__reopens_the_node_only_switches_it_on_and_audits()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedAsync();
        var site1 = f.Nodes["SITE1"];

        Assert.Equal(HttpStatusCode.OK, (await RetireAsync(client, f, site1.Id, "2026-01-01", cascade: true)).StatusCode);
        await DeactivateAsync(site1.Id);

        var restored = await ReadAsync(await UnretireAsync(client, f, site1.Id));
        Assert.Equal([site1.Id], restored.GetProperty("restoredIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());

        await using (var db = new EcrDbContext(Options()))
        {
            var rows = await db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == f.Id).ToDictionaryAsync(e => e.Id);
            Assert.Null(rows[site1.Id].ValidTo);
            Assert.True(rows[site1.Id].IsActive);

            // Children stay retired.
            Assert.Equal(new DateOnly(2026, 1, 1), rows[f.Nodes["SRC1"].Id].ValidTo);
            Assert.Equal(new DateOnly(2026, 1, 1), rows[f.Nodes["SRC2"].Id].ValidTo);
        }

        Assert.Equal(1, await CountAuditAsync("Unretire", site1.Id));
        var audit = await AuditJsonAsync("Unretire", site1.Id);
        Assert.Contains("2026-01-01", audit.Old, StringComparison.Ordinal);

        // A live node: no-op, no second audit row.
        var again = await ReadAsync(await UnretireAsync(client, f, site1.Id));
        Assert.Equal(0, again.GetProperty("restoredIds").GetArrayLength());
        Assert.Equal(1, await CountAuditAsync("Unretire", site1.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Unretire__under_a_retired_parent_is_refused_unless_withAncestors_restores_the_chain()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedAsync();
        var (reg, site1, src1, src2) = (f.Nodes["REG1"], f.Nodes["SITE1"], f.Nodes["SRC1"], f.Nodes["SRC2"]);

        Assert.Equal(HttpStatusCode.OK, (await RetireAsync(client, f, reg.Id, "2026-01-01", cascade: true)).StatusCode);

        using (var refused = await UnretireAsync(client, f, src1.Id))
        {
            var problem = await ProblemAsync(refused, 409, "err.ECR-REG-0409.parentRetired");
            Assert.Equal(
                new[] { site1.Id, reg.Id },
                problem.GetProperty("ancestorIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());
        }

        Assert.Equal(new DateOnly(2026, 1, 1), (await EntryAsync(src1.Id)).ValidTo);

        var chain = await ReadAsync(await UnretireAsync(client, f, src1.Id, withAncestors: true));
        Assert.Equal([reg.Id, site1.Id, src1.Id], chain.GetProperty("restoredIds").EnumerateArray().Select(x => x.GetInt64()).ToArray());
        Assert.Null((await EntryAsync(reg.Id)).ValidTo);
        Assert.Null((await EntryAsync(site1.Id)).ValidTo);
        Assert.Null((await EntryAsync(src1.Id)).ValidTo);
        Assert.Equal(new DateOnly(2026, 1, 1), (await EntryAsync(src2.Id)).ValidTo);
        Assert.Equal(new DateOnly(2026, 1, 1), (await EntryAsync(f.Nodes["SITE2"].Id)).ValidTo);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Unretire_and_purge__another_registrys_entry_is_404_and_a_deleted_entry_is_not_restorable()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedAsync();
        var other = await SeedAsync();
        var foreign = other.Nodes["SRC1"].Id;
        await CloseAsync(foreign, new DateOnly(2026, 1, 1));

        Assert.Equal(HttpStatusCode.NotFound, (await UnretireAsync(client, f, foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PurgeAsync(client, f, foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PurgeAsync(client, f, 999_999_999)).StatusCode);
        Assert.True(await ExistsAsync(foreign));

        // Soft-deleted: restore is a 404 (Restore of a deleted entry is not this action), purge is allowed (below).
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(f, $"entries/{f.Nodes["SRC2"].Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UnretireAsync(client, f, f.Nodes["SRC2"].Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PurgeAsync(client, f, f.Nodes["SRC2"].Id)).StatusCode);
        Assert.False(await ExistsAsync(f.Nodes["SRC2"].Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Unretire__registry_key_held_by_a_live_entry_is_refused()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedKeyedAsync();

        // A (K = x) valid until 2026-03-01; B takes K = x from 2026-03-01 (no overlap, temporal key).
        var a = await CreatedIdAsync(await UpsertAsync(client, f, "A", "x"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url(f, $"entries/{a}/validity"), new { from = (string?)null, to = "2026-03-01" })).StatusCode);
        var b = await CreatedIdAsync(await UpsertAsync(client, f, "B", "y"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Url(f, $"entries/{b}/validity"), new { from = "2026-03-01", to = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UpsertAsync(client, f, "B", "x", id: b)).StatusCode);

        // Reopening A would overlap B's window with the same key.
        using (var taken = await UnretireAsync(client, f, a))
        {
            var text = await taken.Content.ReadAsStringAsync();
            Assert.True(taken.StatusCode == HttpStatusCode.Conflict, $"{taken.StatusCode}: {text}");
            Assert.Equal("ECR-REG-4092", JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString());
        }

        Assert.Equal(new DateOnly(2026, 3, 1), (await EntryAsync(a)).ValidTo);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Unretire__an_nae_source_whose_number_is_now_taken_is_refused_and_its_code_stays_unique()
    {
        var (client, code, app) = await NaeTreeAsync();
        using var _ = app;
        using var __ = client;
        var plant = (await NaeNodesAsync(client, code))["S_ATYRAU_ONSHORE_PLANT"];

        var a = await NaeCreatedIdAsync(await NaeUpsertAsync(client, code, "RP_A", plant, "SRC", "A", new() { ["NaeCode"] = "7101" }));
        var b = await NaeCreatedIdAsync(await NaeUpsertAsync(client, code, "RP_B", plant, "SRC", "B", new() { ["NaeCode"] = "7102" }));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(new Uri($"/api/v1/registries/{code}/entries/{a}/retire", UriKind.Relative), new { on = "2026-01-01", cascade = false })).StatusCode);

        // While A is retired its number goes to B (stored directly: the scope rule ignores retired rivals).
        await StoreNaeNumberAsync(code, b, "7101");

        using (var taken = await client.PostAsJsonAsync(new Uri($"/api/v1/registries/{code}/entries/{a}/unretire", UriKind.Relative), new { withAncestors = false }))
        {
            var text = await taken.Content.ReadAsStringAsync();
            Assert.True(taken.StatusCode == HttpStatusCode.Conflict, $"{taken.StatusCode}: {text}");
            Assert.Equal("err.ECR-REG-4094.naeCodeTaken", JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
        }

        Assert.Equal(new DateOnly(2026, 1, 1), (await EntryAsync(a)).ValidTo);

        // The entry code cannot collide on restore: a live node cannot take the code of a retired one in the first place.
        using var sameCode = await NaeUpsertAsync(client, code, "RP_A", plant, "SRC", "A2", new() { ["NaeCode"] = "7103" });
        Assert.False(sameCode.IsSuccessStatusCode && sameCode.StatusCode == HttpStatusCode.Created, await sameCode.Content.ReadAsStringAsync());

        // With the number free again the restore goes through.
        await StoreNaeNumberAsync(code, b, "7102");
        using var ok = await client.PostAsJsonAsync(new Uri($"/api/v1/registries/{code}/entries/{a}/unretire", UriKind.Relative), new { withAncestors = false });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Null((await EntryAsync(a)).ValidTo);
    }

    // ---- purge ---------------------------------------------------------------------------------------------------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Purge__an_active_entry_is_refused_retire_first()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedAsync();

        using (var active = await PurgeAsync(client, f, f.Nodes["SRC1"].Id))
        {
            await ProblemAsync(active, 409, "err.ECR-REG-0409.purgeActive");
        }

        Assert.True(await ExistsAsync(f.Nodes["SRC1"].Id));
        Assert.Equal(0, await CountAuditAsync("Purge", f.Nodes["SRC1"].Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Purge__deletes_a_retired_unused_leaf_with_its_values_and_key_rows_and_audits_the_row()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedAsync(withNote: true);
        var src1 = f.Nodes["SRC1"];
        await CloseAsync(src1.Id, new DateOnly(2026, 1, 1));

        Assert.Equal(HttpStatusCode.NoContent, (await PurgeAsync(client, f, src1.Id)).StatusCode);

        await using (var db = new EcrDbContext(Options()))
        {
            Assert.False(await db.RegistryEntries.AnyAsync(e => e.Id == src1.Id));
            Assert.False(await db.RegistryValues.AnyAsync(v => v.RegistryEntryId == src1.Id));
            Assert.False(await db.RegistryEntryKeys.AnyAsync(k => k.RegistryEntryId == src1.Id));
        }

        var audit = await AuditJsonAsync("Purge", src1.Id);
        Assert.Contains(src1.Code, audit.Old, StringComparison.Ordinal);
        Assert.Contains($"note {f.Tag}", audit.Old, StringComparison.Ordinal);
        Assert.Equal((byte)ChangeClass.Breaking, audit.ChangeClass);

        // The sibling and the parent are untouched.
        Assert.True(await ExistsAsync(f.Nodes["SRC2"].Id));
        Assert.True(await ExistsAsync(f.Nodes["SITE1"].Id));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(RegistryEntryHardReferences.ChildEntries)]
    [InlineData(RegistryEntryHardReferences.Cells)]
    [InlineData(RegistryEntryHardReferences.RegistryValues)]
    [InlineData(RegistryEntryHardReferences.Snapshots)]
    public async Task Purge__refused_while_used(string kind)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, View, EditData);
        var f = await SeedAsync();
        var target = kind == RegistryEntryHardReferences.ChildEntries ? f.Nodes["SITE1"] : f.Nodes["SRC1"];

        // Retired children (closed with the parent) still block: the purge goes leaf first.
        Assert.Equal(HttpStatusCode.OK, (await RetireAsync(client, f, target.Id, "2026-01-01", cascade: true)).StatusCode);

        switch (kind)
        {
            case RegistryEntryHardReferences.Cells:
                await ReferenceFromCellAsync(target.Id);
                break;
            case RegistryEntryHardReferences.RegistryValues:
                await ReferenceFromDeletedOwnerAsync(target.Id);
                break;
            case RegistryEntryHardReferences.Snapshots:
                await ReferenceFromSnapshotAsync(f.Id, target.Id);
                break;
        }

        using (var used = await PurgeAsync(client, f, target.Id))
        {
            var problem = await ProblemAsync(used, 409, "err.ECR-REG-0409.entryUsed");
            Assert.True(problem.GetProperty("referenceKinds").GetProperty(kind).GetInt32() >= 1, problem.ToString());
            Assert.True(problem.GetProperty("references").GetInt32() >= 1);
        }

        Assert.True(await ExistsAsync(target.Id));
        Assert.Equal(0, await CountAuditAsync("Purge", target.Id));

        if (kind == RegistryEntryHardReferences.ChildEntries)
        {
            // Leaves first, then the parent goes too.
            Assert.Equal(HttpStatusCode.NoContent, (await PurgeAsync(client, f, f.Nodes["SRC1"].Id)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await PurgeAsync(client, f, f.Nodes["SRC2"].Id)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await PurgeAsync(client, f, target.Id)).StatusCode);
            Assert.False(await ExistsAsync(target.Id));
        }
    }

    // ---- DB-level FK coverage ------------------------------------------------------------------------------------------

    /// <summary>
    /// The deployed schema (migrations AND <c>Sql/*.sql</c>): every FK onto <c>dic.RegistryEntry</c> is a where-used kind or an
    /// owned row — the twin of the EF-model guard <c>RegistryEntryForeignKeyCoverageTests</c> (Architecture), which cannot see
    /// FKs added by raw scripts.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Schema__every_foreign_key_onto_registry_entries_is_checked_or_owned()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fk.name FROM sys.foreign_keys fk WHERE fk.referenced_object_id = OBJECT_ID(N'dic.RegistryEntry') ORDER BY fk.name";
        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(names);
        var undecided = names
            .Where(n => !RegistryEntryHardReferences.CheckedForeignKeys.ContainsKey(n) && !RegistryEntryHardReferences.OwnedForeignKeys.Contains(n))
            .ToList();
        Assert.True(undecided.Count == 0, "FK onto dic.RegistryEntry without a decision: " + string.Join(", ", undecided));
    }

    // ---- references ----------------------------------------------------------------------------------------------------

    private async Task ReferenceFromCellAsync(long entryId)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        await using var db = new EcrDbContext(Options());
        db.CellValues.Add(new CellValue(
            new CellAddress(document.PeriodKey, document.RowIds[0], document.ColumnDefIds[0]),
            document.TableDefId,
            new CellValueData { ValueRegistryEntryId = entryId }));
        await db.SaveChangesAsync();
    }

    /// <summary>An entry of a neighbouring registry points at ours with a Lookup field — and is itself deleted logically.</summary>
    private async Task ReferenceFromDeletedOwnerAsync(long entryId)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());
        var target = await db.RegistryEntries.AsNoTracking().Where(e => e.Id == entryId).Select(e => e.RegistryDefId).SingleAsync();

        var holder = new RegistryDef(EcrCode.Create($"RPH{tag}"), Name($"Holder {tag}"), isTemporal: false);
        db.RegistryDefs.Add(holder);
        await db.SaveChangesAsync();

        var field = new RegistryFieldDef(holder.Id, EcrCode.Create("Ref"), Name("Ref"), CellDataType.Lookup, 1);
        field.PointTo(target);
        db.RegistryFieldDefs.Add(field);
        var owner = new RegistryEntry(holder.Id, EcrCode.Create($"H{tag}"), Name($"Holder entry {tag}"));
        db.RegistryEntries.Add(owner);
        await db.SaveChangesAsync();

        var value = new RegistryValue(owner, field.Id);
        value.Set(CellDataType.Lookup, entryId, null);
        db.RegistryValues.Add(value);
        owner.SoftDelete();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// D-67: a submitted snapshot holds the id (its cell was later cleared — no live cell, no FK). The template has a Lookup
    /// column onto the registry, which is what lets the where-used check look at its snapshots at all.
    /// </summary>
    private async Task ReferenceFromSnapshotAsync(int registryDefId, long entryId)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        await using var db = new EcrDbContext(Options());
        var column = await db.ColumnDefs.SingleAsync(c => c.Id == document.ColumnDefIds[0]);
        await db.Database.ExecuteSqlAsync($"UPDATE cfg.ColumnDef SET LookupRegistryDefId = {registryDefId} WHERE Id = {column.Id}");

        var payload = $"[{{\"row\":{document.RowIds[0]},\"column\":{column.Id},\"value\":\"{entryId}\",\"type\":\"ref\"}}]";
        db.SubmissionSnapshots.Add(new SubmissionSnapshot(
            document.DocumentId, document.SheetDefId, document.PeriodKey.Value, document.TemplateVersionId,
            "[]", null, null, payload, new byte[32], DateTime.UtcNow.AddMinutes(1), 0));
        await db.SaveChangesAsync();
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static Uri Url(Fixture f, string tail) => new($"/api/v1/registries/{f.Code}/{tail}", UriKind.Relative);

    private static Task<HttpResponseMessage> UnretireAsync(HttpClient client, Fixture f, long id, bool withAncestors = false)
        => client.PostAsJsonAsync(Url(f, $"entries/{id}/unretire"), new { withAncestors });

    private static Task<HttpResponseMessage> PurgeAsync(HttpClient client, Fixture f, long id)
        => client.DeleteAsync(Url(f, $"entries/{id}/purge"));

    private static Task<HttpResponseMessage> RetireAsync(HttpClient client, Fixture f, long id, string on, bool cascade)
        => client.PostAsJsonAsync(Url(f, $"entries/{id}/retire"), new { on, cascade });

    private static Task<HttpResponseMessage> UpsertAsync(HttpClient client, Fixture f, string code, string key, long? id = null)
        => client.PostAsJsonAsync(
            Url(f, "entries"),
            new
            {
                id,
                registryDefId = f.Id,
                code = $"{code}_{f.Tag}",
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["K"] = key },
                levelCode = "L1",
            });

    private static async Task<long> CreatedIdAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt64();
        }
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

    private async Task<JsonElement> ProblemAsync(HttpResponseMessage response, int status, string messageKey)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"expected {status} got {(int)response.StatusCode}: {text} | {serverErrors?.Invoke()}");
        var problem = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
        return problem;
    }

    private async Task<RegistryEntry> EntryAsync(long id)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private async Task<bool> ExistsAsync(long id)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.AnyAsync(e => e.Id == id);
    }

    /// <summary>Closes an entry's window directly (an arranged retired state, without the retire route).</summary>
    private async Task CloseAsync(long id, DateOnly on)
    {
        await using var db = new EcrDbContext(Options());
        var entry = await db.RegistryEntries.SingleAsync(e => e.Id == id);
        entry.SetValidity(entry.ValidFrom, on);
        await db.SaveChangesAsync();
    }

    private async Task DeactivateAsync(long id)
    {
        await using var db = new EcrDbContext(Options());
        (await db.RegistryEntries.SingleAsync(e => e.Id == id)).Deactivate();
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAuditAsync(string operation, long entityId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = @o AND EntityType = N'dic.RegistryEntry' AND EntityId = @id";
        command.Parameters.AddWithValue("@o", operation);
        command.Parameters.AddWithValue("@id", (int)entityId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(string Old, byte ChangeClass)> AuditJsonAsync(string operation, long entityId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP 1 OldJson, ChangeClass FROM aud.StructureChange WHERE Operation = @o AND EntityType = N'dic.RegistryEntry' AND EntityId = @id ORDER BY Id DESC";
        command.Parameters.AddWithValue("@o", operation);
        command.Parameters.AddWithValue("@id", (int)entityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"no {operation} audit row for {entityId}");
        return (reader.IsDBNull(0) ? "" : reader.GetString(0), Convert.ToByte(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture));
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Levels REGION, SITE, SRC; nodes REG1 → SITE1 → {SRC1, SRC2}, REG1 → SITE2. Optional field NOTE with a value on SRC1.</summary>
    private async Task<Fixture> SeedAsync(bool withNote = false)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"RP{tag}"), Name($"Restore purge {tag}"), isTemporal: false);
        registry.UseHierarchy(true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var levels = new Dictionary<string, RegistryLevelDef>();
        var spec = new[] { "REGION", "SITE", "SRC" };
        for (var i = 0; i < spec.Length; i++)
        {
            var level = new RegistryLevelDef(registry.Id, i + 1, EcrCode.Create(spec[i]), Name(spec[i]), false);
            db.RegistryLevelDefs.Add(level);
            levels[spec[i]] = level;
        }

        RegistryFieldDef? note = null;
        if (withNote)
        {
            note = new RegistryFieldDef(registry.Id, EcrCode.Create("NOTE"), Name("Note"), CellDataType.String, 1);
            db.RegistryFieldDefs.Add(note);
        }

        await db.SaveChangesAsync();

        var nodes = new Dictionary<string, RegistryEntry>();
        async Task Add(string key, string level, string? parent)
        {
            var entry = new RegistryEntry(registry.Id, EcrCode.Create($"{key}_{tag}"), Name(key), 0, DateTime.UtcNow);
            entry.SetLevel(levels[level].Id);
            if (parent is not null)
            {
                entry.SetParent(nodes[parent].Id);
            }

            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            nodes[key] = entry;
        }

        await Add("REG1", "REGION", null);
        await Add("SITE1", "SITE", "REG1");
        await Add("SITE2", "SITE", "REG1");
        await Add("SRC1", "SRC", "SITE1");
        await Add("SRC2", "SRC", "SITE1");

        if (note is not null)
        {
            var value = new RegistryValue(nodes["SRC1"], note.Id);
            value.Set(CellDataType.String, $"note {tag}", null);
            db.RegistryValues.Add(value);
            await db.SaveChangesAsync();
        }

        return new Fixture(registry.Id, registry.Code, tag, nodes);
    }

    /// <summary>A temporal hierarchical registry (one level L1) with a primary key on field K.</summary>
    private async Task<Fixture> SeedKeyedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"RK{tag}"), Name($"Keyed {tag}"), isTemporal: true);
        registry.UseHierarchy(true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        db.RegistryLevelDefs.Add(new RegistryLevelDef(registry.Id, 1, EcrCode.Create("L1"), Name("L1"), false));
        var k = new RegistryFieldDef(registry.Id, EcrCode.Create("K"), Name("K"), CellDataType.String, 1);
        k.Update(Name("K"), 1, isRequired: true);
        db.RegistryFieldDefs.Add(k);
        await db.SaveChangesAsync();

        db.RegistryKeyDefs.Add(new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Name("Primary"), [k], isPrimary: true, ignoreCase: true, createdByUserId: 0, DateTime.UtcNow));
        await db.SaveChangesAsync();

        return new Fixture(registry.Id, registry.Code, tag, []);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"rpur_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Restore purge test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        serverErrors = () => app.ErrorsText;
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    // ---- NAE tree (the same AF fixture as NaeNodeTypesHttpTests, trimmed) ----------------------------------------------

    private static NaeAfElement El(string relative, string? template, string? en = null)
        => new($@"{Root}\{relative}", relative.Split('\\')[^1], en, null, null, null, null, template);

    private sealed class FakeAf(IEnumerable<NaeAfElement> elements) : INaeAfReader
    {
        public Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct)
            => Task.FromResult(new NaeAfSnapshot([.. elements], true));
    }

    private async Task<(HttpClient Client, string Code, EcrApiFactory App)> NaeTreeAsync()
    {
        var code = $"NAERP{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        List<NaeAfElement> elements =
        [
            El(@"Atyrau", "Air_Region"),
            El(@"Atyrau\Onshore", "Air_OnShoreOffShore"),
            El(@"Atyrau\Onshore\Plant", "Air_Area", "Plant"),
            El(@"Atyrau\Onshore\Plant\9001", "Equipment", "Stack 9001"),
        ];
        var app = new EcrApiFactory(sql)
        {
            ExtraServices = s =>
            {
                s.AddSingleton<INaeAfReader>(new FakeAf(elements));
                s.AddSingleton(AirLimitsSettings.Default with { AreaRegistryCode = "NOAREAS_" + code });
            },
        };
        serverErrors = () => app.ErrorsText;
        var client = await SignedInAsync(app, View, "Registry.EditDefinition", EditData, "Registry.Publish");
        var report = await ReadAsync(await client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/af-import", UriKind.Relative),
            new { dataSourceId = 1, rootPath = Root, registryCode = code, dryRun = false }));
        Assert.True(report.GetProperty("applied").GetBoolean(), report.ToString());
        return (client, code, app);
    }

    private async Task<Dictionary<string, long>> NaeNodesAsync(HttpClient client, string code)
        => (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/tree", UriKind.Relative))))
            .GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("code").GetString()!, n => n.GetProperty("id").GetInt64());

    private static async Task<HttpResponseMessage> NaeUpsertAsync(
        HttpClient client, string registry, string code, long parent, string level, string en, Dictionary<string, object?> values)
    {
        var registryId = (await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/registries/{registry}/definition", UriKind.Relative))).GetProperty("id").GetInt32();
        return await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry}/entries", UriKind.Relative),
            new { id = (long?)null, registryDefId = registryId, code, display = new { values = new Dictionary<string, string> { ["en"] = en } }, parentEntryId = parent, levelCode = level, values });
    }

    private async Task<long> NaeCreatedIdAsync(HttpResponseMessage response) => (await ReadAsync(response)).GetProperty("id").GetInt64();

    private async Task StoreNaeNumberAsync(string registry, long entryId, string number)
    {
        await using var db = new EcrDbContext(Options());
        var def = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Code == registry);
        var field = def.Fields.Single(f => f.Code == "NaeCode");
        var value = await db.RegistryValues.SingleAsync(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == field.Id);
        value.Set(CellDataType.String, number, null);
        await db.SaveChangesAsync();
    }

    private sealed record Fixture(int Id, string Code, string Tag, Dictionary<string, RegistryEntry> Nodes);
}
