using System.Xml.Linq;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// NAE purge (human 04.11): the where-used check before a physical delete (<see cref="RegistryStore.FindHardReferencesAsync"/>)
/// at volume — 20 000 cells and 2 000 registry values pointing at ANOTHER entry of the same registry.
/// </summary>
/// <remarks>
/// <para>
/// The unused entry gets an empty answer, the used one gets every kind (capped at <see cref="RegistryStore.HardReferenceCap"/>),
/// and the cells / children queries — the big tables — read by their indexes (<c>IX_CellValue_RegistryEntry</c>,
/// <c>IX_RegistryEntry_Parent</c>), taken from the plan cache by tag like <c>RegistryWhereUsedPlanTests</c> (plan, not reads: on a
/// small shared test DB a scan costs as little as a seek). ⚠ <c>dic.RegistryValue.ValueRefEntryId</c> has no index (the FK on the
/// DELETE scans it too) — an index is a separate migration; this test only bounds the answer, not that plan.
/// </para>
/// <para>
/// Mutation proof (local, not committed): drop the explicit <c>ValueRegistryEntryId != null</c> from the cells query → the
/// filtered index stops matching and the plan shows <c>PK_CellValue</c>; cap removed → the used entry's counts read 20 000.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryHardReferencesScaleTests(SqlServerFixture sql)
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private const int Rows = 500;
    private const int Columns = 40;
    private const int Values = 2_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Unused_entry_stays_on_indexes_among_twenty_thousand_references_to_its_neighbour()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: Columns, rowCount: Rows);

        int registryId;
        int holderId;
        long used;
        long unused;
        await using (var db = builder.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create($"HRS{tag}"), Name($"Scale {tag}"), isTemporal: false);
            registry.UseHierarchy(true);
            var holder = new RegistryDef(EcrCode.Create($"HRH{tag}"), Name($"Holder {tag}"), isTemporal: false);
            db.RegistryDefs.AddRange(registry, holder);
            await db.SaveChangesAsync();

            var a = new RegistryEntry(registry.Id, EcrCode.Create($"A{tag}"), Name("A"));
            var b = new RegistryEntry(registry.Id, EcrCode.Create($"B{tag}"), Name("B"));
            db.RegistryEntries.AddRange(a, b);
            var field = new RegistryFieldDef(holder.Id, EcrCode.Create("Ref"), Name("Ref"), CellDataType.Lookup, 1);
            field.PointTo(registry.Id);
            db.RegistryFieldDefs.Add(field);
            await db.SaveChangesAsync();

            var child = new RegistryEntry(registry.Id, EcrCode.Create($"C{tag}"), Name("C"));
            child.SetParent(a.Id);
            child.SoftDelete();
            db.RegistryEntries.Add(child);
            await db.SaveChangesAsync();

            (registryId, holderId, used, unused) = (registry.Id, holder.Id, a.Id, b.Id);

            // 2 000 holder entries, each with a Lookup value onto A — set-based, in one statement each.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO dic.RegistryEntry (RegistryDefId, Code, DisplayL10n, IsActive, IsDeleted, Ordinal, CreatedAt, CreatedByUserId)
                SELECT {holder.Id}, CONCAT(N'H', n.i), CONCAT(NCHAR(123), N'"en":"h"', NCHAR(125)), 1, 0, 0, SYSUTCDATETIME(), 0
                FROM (SELECT TOP ({Values}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns x CROSS JOIN sys.all_columns y) n;
                INSERT INTO dic.RegistryValue (RegistryEntryId, RegistryFieldDefId, ValueRefEntryId)
                SELECT e.Id, {field.Id}, {(int)a.Id} FROM dic.RegistryEntry e WHERE e.RegistryDefId = {holder.Id};
                """);
        }

        // 20 000 cells onto A, bulk like the real load.
        var cells = new List<CellRecord>(Rows * Columns);
        foreach (var rowId in document.RowIds)
        {
            foreach (var columnId in document.ColumnDefIds)
            {
                cells.Add(new CellRecord(new CellAddress(document.PeriodKey, rowId, columnId), document.TableDefId, new CellValueData { ValueRegistryEntryId = used }));
            }
        }

        await new BulkCellLoader(sql.ConnectionString, 10_000).LoadAsync(cells, CancellationToken.None);

        try
        {
            await PrepareMeasurementAsync();

            await using var db = builder.CreateContext();
            var store = new RegistryStore(db);
            var none = await store.FindHardReferencesAsync(await db.RegistryEntries.SingleAsync(e => e.Id == unused), CancellationToken.None);
            Assert.Empty(none);

            Assert.Equal(["[IX_CellValue_RegistryEntry]"], await IndexesInCachedPlanAsync(RegistryStore.HardReferenceCellsTag, "[CellValue]"));
            Assert.Equal(["[IX_RegistryEntry_Parent]"], await IndexesInCachedPlanAsync(RegistryStore.HardReferenceChildrenTag, "[RegistryEntry]"));

            var all = await store.FindHardReferencesAsync(await db.RegistryEntries.SingleAsync(e => e.Id == used), CancellationToken.None);
            Assert.Equal(RegistryStore.HardReferenceCap, all[RegistryEntryHardReferences.Cells]);
            Assert.Equal(RegistryStore.HardReferenceCap, all[RegistryEntryHardReferences.RegistryValues]);
            Assert.Equal(1, all[RegistryEntryHardReferences.ChildEntries]); // the deleted child still blocks
        }
        finally
        {
            await CleanUpAsync(document.TableDefId, holderId, registryId);
        }
    }

    private async Task PrepareMeasurementAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE STATISTICS doc.CellValue;
            UPDATE STATISTICS dic.RegistryEntry;
            UPDATE STATISTICS dic.RegistryValue;
            ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<string>> IndexesInCachedPlanAsync(string tag, string table)
    {
        var indexes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var text in await PlanCache.PlansAsync(sql.ConnectionString, tag))
        {
            foreach (var target in XDocument.Parse(text).Descendants(XName.Get("Object", ShowPlanNs)))
            {
                if ((string?)target.Attribute("Table") == table)
                {
                    indexes.Add((string?)target.Attribute("Index") ?? "(heap)");
                }
            }
        }

        return [.. indexes];
    }

    /// <summary>The test DB has a size ceiling (TestDatabaseSizeTests): the 20 000 cells and 2 000 values go, in chunks.</summary>
    private async Task CleanUpAsync(int tableDefId, int holderId, int registryId)
    {
        await using var db = sql.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            WHILE 1 = 1
            BEGIN
                DELETE TOP (10000) FROM doc.CellValue WHERE TableDefId = {tableDefId};
                IF @@ROWCOUNT = 0 BREAK;
                CHECKPOINT;
            END;
            DELETE v FROM dic.RegistryValue v JOIN dic.RegistryEntry e ON e.Id = v.RegistryEntryId WHERE e.RegistryDefId = {holderId};
            DELETE FROM dic.RegistryEntry WHERE RegistryDefId = {holderId};
            DELETE FROM dic.RegistryEntry WHERE RegistryDefId = {registryId} AND ParentEntryId IS NOT NULL;
            """);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
