using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Архівна комірка з посиланням на запис довідника читається, а не падає.
/// </summary>
/// <remarks>
/// ⛔ Що було: <c>arc.CellValue.ValueRegistryEntryId</c> (як і
/// <c>doc.CellValue</c>) — <c>int</c>, а сирий SQL архівного читача
/// (<c>ArchiveAwareCellReader.ArchivedAsync</c>) матеріалізував його в
/// <c>long?</c> запису <c>ArchivedCell</c>. SqlClient не розширює тип сам:
/// <c>InvalidCastException: Int32 → Int64</c> на першій же НЕпорожній
/// довідниковій комірці. Тобто заархівований рік із бодай одним вибором із
/// довідника не відкривався взагалі — ні сітка, ні експорт. Гарячий шлях
/// (LINQ через модель EF) цього не мав.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchivedRegistryValueReadTests(SqlServerFixture sql)
{
    /// <summary>Період лише цього тесту.</summary>
    private const int PeriodKey = 209908;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Архівна_комірка_з_записом_довідника_читається()
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
                VALUES ({PeriodKey}, {instanceId}, 1, 800, '2020-01-01', '2020-01-01');
                INSERT INTO arc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
                VALUES ({PeriodKey}, {rowId}, {instanceId}, N'R1', NULL, 1, 0, '2020-01-01');
                INSERT INTO arc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
                                           ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
                VALUES ({PeriodKey}, {rowId}, 1, 800, NULL, NULL, NULL, NULL, 2147483647, NULL, 0, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var db = sql.CreateContext();
        var reader = new ArchiveAwareCellReader(db);

        var cell = Assert.Single(await reader.ReadArchivedSliceAsync(instanceId, CancellationToken.None));
        Assert.Equal(2147483647L, cell.Value.ValueRegistryEntryId);
    }
}
