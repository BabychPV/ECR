using System.Reflection;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D3-A (P1-1): м'яко видалений рядок і його значення не повертаються й з АРХІВУ (<c>arc.TableRow.IsDeleted</c>).
/// </summary>
/// <remarks>
/// ⛔ Усі чотири архівні читачі (<c>ArchivedAsync</c>, пакетний <c>ReadArchivedSlicesAsync</c>,
/// <c>ReadArchivedRowIdsAsync</c>, <c>ReadArchivedOrphanFlagsAsync</c>) без <c>r.IsDeleted = 0</c>
/// віддавали б видалений рядок після архівації періоду. Мутація: прибрати предикат з будь-якого —
/// відповідна перевірка нижче червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchivedDeletedRowReadTests(SqlServerFixture sql)
{
    private const int PeriodKey = 209907;
    private const int TableDefId = 801;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалений_рядок_і_його_значення_з_архіву_не_повертаються()
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None);
        var liveId = firstRow;
        var deletedId = firstRow + 1;

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
                VALUES ({PeriodKey}, {instanceId}, 1, {TableDefId}, '2020-01-01', '2020-01-01');
                INSERT INTO arc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
                VALUES ({PeriodKey}, {liveId}, {instanceId}, N'LIVE', NULL, 1, 0, '2020-01-01'),
                       ({PeriodKey}, {deletedId}, {instanceId}, N'GONE', NULL, 2, 1, '2020-01-01');
                INSERT INTO arc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
                                           ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
                VALUES ({PeriodKey}, {liveId}, 1, {TableDefId}, NULL, 11, NULL, NULL, NULL, NULL, 0, 0),
                       ({PeriodKey}, {deletedId}, 1, {TableDefId}, NULL, 99, NULL, NULL, NULL, NULL, 0, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var db = sql.CreateContext();
        var reader = new ArchiveAwareCellReader(db);
        var period = new PeriodKey(PeriodKey);

        // Поштучний і пакетний шлях зрізу.
        var single = await reader.ReadArchivedSliceAsync(instanceId, CancellationToken.None);
        Assert.Equal([liveId], single.Select(c => c.Address.TableRowId).Distinct());
        Assert.DoesNotContain(single, c => c.Value.ValueNumeric == 99m);

        var batch = await reader.ReadArchivedSlicesAsync([instanceId], CancellationToken.None);
        Assert.Equal([liveId], batch[instanceId].Select(c => c.Address.TableRowId).Distinct());

        // Закритий (private) читач комірок періоду, яким ходить ReadAsync для заархівованого проєкту.
        var archived = typeof(ArchiveAwareCellReader).GetMethod(
            "ArchivedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cells = await (Task<List<CellRecord>>)archived.Invoke(
            reader, [instanceId, period, CancellationToken.None])!;
        Assert.Equal([liveId], cells.Select(c => c.Address.TableRowId).Distinct());

        // Ключі й ознаки осиротілості.
        var ids = await reader.ReadArchivedRowIdsAsync(instanceId, period, CancellationToken.None);
        Assert.Equal(["LIVE"], ids.Select(r => r.RowKey));

        var flags = await reader.ReadArchivedOrphanFlagsAsync(instanceId, period, CancellationToken.None);
        Assert.Equal([liveId], flags.Select(f => f.Id));
    }
}
