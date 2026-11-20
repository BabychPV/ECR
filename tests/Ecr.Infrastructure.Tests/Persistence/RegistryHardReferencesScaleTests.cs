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

            // ⛔ 20.11: план беремо НЕ з кешу планів за міткою, а фактичним планом (SET STATISTICS XML) ТОГО САМОГО тексту й
            // параметрів, які щойно виконав EF (перехоплено; параметри передаються так само, тож план під ті самі значення). У повному прогоні Infrastructure (53 хв, спільний сервер) план
            // запиту комірок за міткою в кеші не зʼявлявся ЖОДНОГО з 5 повторів поспіль (планів `cells = 0`, `children = 1`),
            // хоча окремо клас зелений, а ті самі запити в базі після прогону дають index seek: стан кешу планів — не те, що
            // перевіряє тест. Форма плану (Index Seek по IX_…) від кешу не залежить, і перевіряється так само суворо.
            var capture = new TaggedCommandCapture(RegistryStore.HardReferenceCellsTag, RegistryStore.HardReferenceChildrenTag);
            await using var db = sql.CreateContext(capture);
            var store = new RegistryStore(db);
            var none = await store.FindHardReferencesAsync(await db.RegistryEntries.SingleAsync(e => e.Id == unused), CancellationToken.None);
            Assert.Empty(none);

            var cellsPlan = await ActualPlanAsync(capture.Get(RegistryStore.HardReferenceCellsTag));
            var childPlan = await ActualPlanAsync(capture.Get(RegistryStore.HardReferenceChildrenTag));
            Assert.Equal(["[IX_CellValue_RegistryEntry]"], IndexesInPlans([cellsPlan], "[CellValue]"));
            Assert.Equal(["[IX_RegistryEntry_Parent]"], IndexesInPlans([childPlan], "[RegistryEntry]"));

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

    private static IReadOnlyList<string> IndexesInPlans(IReadOnlyList<string> plans, string table)
    {
        var indexes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var text in plans)
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

    /// <summary>
    /// Фактичний план (<c>SET STATISTICS XML</c>) перехопленої команди EF: текст і параметри ті самі, виконується так само (RPC із
    /// параметрами), тож план компілюється під ті самі значення параметрів, що й у бойовому виклику.
    /// </summary>
    private async Task<string> ActualPlanAsync(CapturedCommand command)
    {
        var connectionString = new SqlConnectionStringBuilder(sql.ConnectionString) { Pooling = false }.ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET STATISTICS XML ON;");
        try
        {
            await using var plan = connection.CreateCommand();
            plan.CommandText = command.Text;
            foreach (var (name, value, type) in command.Parameters)
            {
                plan.Parameters.Add(new SqlParameter(name, type) { Value = value ?? DBNull.Value });
            }

            await using var reader = await plan.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal) && await reader.ReadAsync())
                {
                    return reader.GetString(0);
                }
            }
            while (await reader.NextResultAsync());

            throw new Xunit.Sdk.XunitException("SET STATISTICS XML не повернув плану.");
        }
        finally
        {
            await ExecuteAsync(connection, "SET STATISTICS XML OFF;");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record CapturedCommand(string Text, IReadOnlyList<(string Name, object? Value, System.Data.DbType Type)> Parameters);

    /// <summary>Запамʼятовує останню команду EF, текст якої містить одну з міток.</summary>
    private sealed class TaggedCommandCapture(params string[] tags) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly Dictionary<string, CapturedCommand> _byTag = [];

        public CapturedCommand Get(string tag)
            => _byTag.TryGetValue(tag, out var command)
                ? command
                : throw new Xunit.Sdk.XunitException($"EF не виконав запиту з міткою '{tag}'.");

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> ScalarExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        private void Capture(System.Data.Common.DbCommand command)
        {
            foreach (var tag in tags)
            {
                if (command.CommandText.Contains(tag, StringComparison.Ordinal))
                {
                    var parameters = command.Parameters.Cast<System.Data.Common.DbParameter>()
                        .Select(p => (p.ParameterName, p.Value, p.DbType))
                        .ToList();
                    _byTag[tag] = new CapturedCommand(command.CommandText, parameters);
                }
            }
        }
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
