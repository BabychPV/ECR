using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Архівний фолбек пакетного читання зрізів
/// (<c>ArchiveAwareCellReader.ReadArchivedSlicesAsync</c>, F-13): звернень до
/// БД не більшає з кількістю екземплярів таблиць.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було: на кожен екземпляр — <c>SELECT TOP 1 PeriodKey FROM
/// arc.TableInstance</c> і окремий запит комірок, тобто 2N звернень на пакет,
/// щойно в гарячій схемі немає жодної комірки пакета (заархівований рік,
/// експорт документа з ~90 таблицями).
/// </para>
/// <para>
/// ⚠ Дані кладуться прямо в <c>arc.*</c> з унікальними ідентифікаторами з
/// послідовностей, а не справжньою архівацією: <c>arc.usp_ArchiveYear</c>
/// звільняє ПАРТИЦІЮ періоду для всіх проєктів спільної тестової бази. Читач
/// дивиться лише в <c>arc.*</c> за ідентифікатором екземпляра — більше йому
/// нічого не треба.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ArchivedSlicesQueryCountTests(SqlServerFixture sql)
{
    /// <summary>Період лише цього тесту — поза діапазонами інших архівних тестів.</summary>
    private const int PeriodKey = 209907;

    private const int RowsPerInstance = 2;

    private const int ColumnsPerRow = 3;

    /// <summary>Стеля звернень на один пакет — незалежно від кількості екземплярів.</summary>
    /// <remarks>⛔ Лише знижується. До фіксу: 3 екземпляри — 6, 12 — 24 (2N).</remarks>
    private const int MaxCommands = 1;

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "P3")]
    public async Task Архівний_пакет_читається_стільки_ж_звернень_на_3_і_на_12_екземплярах()
    {
        var warm = await ArrangeAsync(1);
        await ReadMeasuredAsync(warm);

        var small = await ArrangeAsync(3);
        var large = await ArrangeAsync(12);

        var (smallCount, smallResult) = await ReadMeasuredAsync(small);
        var (largeCount, largeResult) = await ReadMeasuredAsync(large);

        // Пакет справді прочитав усе — інакше «мало звернень» доводило б лише порожнечу.
        Assert.Equal(3, smallResult.Count);
        Assert.Equal(12, largeResult.Count);
        Assert.All(largeResult.Values, cells => Assert.Equal(RowsPerInstance * ColumnsPerRow, cells.Count));

        Assert.True(
            largeCount == smallCount,
            $"Архівний пакет: 3 екземпляри — {smallCount} звернень, 12 — {largeCount}. "
            + "Кількість звернень росте з кількістю екземплярів (N+1).");
        Assert.True(largeCount <= MaxCommands, $"Архівний пакет: {largeCount} звернень, стеля {MaxCommands}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "P3")]
    public async Task Архівний_пакет_дорівнює_поштучному_читанню_кожного_екземпляра()
    {
        var ids = await ArrangeAsync(4);

        // Екземпляр, якого в архіві немає, у результат не потрапляє — той самий
        // контракт, що й у гарячого `ReadSlicesAsync`.
        var missing = ids.Max() + 1_000_000_000L;
        var requested = ids.Append(missing).ToList();

        await using var db = sql.CreateContext();
        var reader = new ArchiveAwareCellReader(db);

        var expected = new Dictionary<long, string>();
        foreach (var id in requested)
        {
            var cells = await reader.ReadArchivedSliceAsync(id, CancellationToken.None);
            if (cells.Count > 0)
            {
                expected[id] = Describe(cells);
            }
        }

        var actual = await reader.ReadArchivedSlicesAsync(requested, CancellationToken.None);

        Assert.Equal(4, expected.Count);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (id, text) in expected)
        {
            Assert.Equal(text, Describe(actual[id]));
        }

        Assert.Empty(await reader.ReadArchivedSlicesAsync([], CancellationToken.None));
    }

    /// <summary>Порядок і кожне поле комірки — рядком, щоб порівняння було побайтним.</summary>
    private static string Describe(IReadOnlyList<CellRecord> cells)
    {
        var text = new StringBuilder();
        foreach (var c in cells)
        {
            var v = c.Value;
            text.Append(CultureInfo.InvariantCulture,
                $"{c.Address.PeriodKey.Value}|{c.Address.TableRowId}|{c.Address.ColumnDefId}|{c.TableDefId}|"
                + $"{v.ValueString}|{v.ValueNumeric}|{v.ValueDate:O}|{v.ValueBool}|{v.ValueRegistryEntryId}|"
                + $"{v.ValueUnitId}|{v.IsCalculated}|{v.IsEmpty};");
        }

        return text.ToString();
    }

    private async Task<(int Total, IReadOnlyDictionary<long, IReadOnlyList<CellRecord>> Result)> ReadMeasuredAsync(
        IReadOnlyList<long> ids)
    {
        await using var db = sql.CreateContext();
        var reader = new ArchiveAwareCellReader(db);

        using var counter = new SqlClientCommandCounter(
            new CommandTally(), _ => Measuring.Value is { Value: true });

        counter.Tally.Reset();
        var flag = new StrongBox<bool>(true);
        Measuring.Value = flag;
        IReadOnlyDictionary<long, IReadOnlyList<CellRecord>> result;
        try
        {
            result = await reader.ReadArchivedSlicesAsync(ids, CancellationToken.None);
        }
        finally
        {
            flag.Value = false;
            Measuring.Value = null;
        }

        counter.AssertObserved();
        return (counter.Tally.Snapshot().Total, result);
    }

    /// <summary>
    /// <paramref name="count"/> архівних екземплярів із рядками й комірками
    /// (усі типи значень, щоб еквівалентність зачепила кожне поле).
    /// </summary>
    private async Task<IReadOnlyList<long>> ArrangeAsync(int count)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var firstInstance = await loader.ReserveIdsAsync("doc.TableInstanceSeq", count, CancellationToken.None);
        var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", count * RowsPerInstance, CancellationToken.None);

        var sqlText = new StringBuilder();
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var instanceId = firstInstance + i;
            ids.Add(instanceId);
            var tableDefId = 700 + i;
            sqlText.Append(CultureInfo.InvariantCulture, $"""
                INSERT INTO arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
                VALUES ({PeriodKey}, {instanceId}, 1, {tableDefId}, '2020-01-01', '2020-01-01');

                """);

            for (var r = 0; r < RowsPerInstance; r++)
            {
                var rowId = firstRow + (i * RowsPerInstance) + r;
                sqlText.Append(CultureInfo.InvariantCulture, $"""
                    INSERT INTO arc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
                    VALUES ({PeriodKey}, {rowId}, {instanceId}, N'R{r}', NULL, {r + 1}, 0, '2020-01-01');
                    INSERT INTO arc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
                                               ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
                    VALUES ({PeriodKey}, {rowId}, 3, {tableDefId}, NULL, NULL, NULL, 1, NULL, NULL, 0, 0),
                           ({PeriodKey}, {rowId}, 1, {tableDefId}, N'т{i}-{r}', NULL, NULL, NULL, 5, NULL, 0, 0),
                           ({PeriodKey}, {rowId}, 2, {tableDefId}, NULL, {i}.{r}25, '2020-02-0{r + 1}', NULL, NULL, 7, 1, 0);

                    """);
            }
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText.ToString();
        await command.ExecuteNonQueryAsync();

        return ids;
    }
}
