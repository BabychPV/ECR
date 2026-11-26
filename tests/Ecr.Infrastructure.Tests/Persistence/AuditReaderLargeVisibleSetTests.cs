using System.Data;
using System.Diagnostics;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ audit-filters-live D1: журнал документа з десятками тисяч видимих колонок (документ Land: видимі колонки × версії
/// шаблону — на копії live 125 263 Id) відповідає в межах 2 с, а межі читання діють як і раніше.
/// </summary>
/// <remarks>
/// ⛔ На базі 7afb19c97 тест ЧЕРВОНИЙ: умова <c>ColumnDefId IN (SELECT … FROM OPENJSON(@visibleColumns))</c> розбирала
/// JSON на кожен рядок журналу документа — копія live, документ 96: понад 45 с (live: Execution Timeout → 503).
/// Тут 7 000 змін і 125 001 Id — на базі сторінка не вкладається в тайм-аут команди (30 с).
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderLargeVisibleSetTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int PerColumn = 3_500;
    private const int FakeVisible = 125_000;
    private static readonly DateTime Start = new(2019, 7, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_документа_з_тисячами_видимих_колонок_відповідає_за_2_с_і_не_показує_приховане()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2, ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken.None);
            await FillAsync(connection, doc, doc.ColumnDefIds[1], offsetSeconds: 0);
            await FillAsync(connection, doc, doc.ColumnDefIds[2], offsetSeconds: 1);
        }

        // Видима лише колонка 1 — серед 125 000 Id колонок, яких у журналі документа немає (родовід інших версій).
        IReadOnlyList<int> visible = [doc.ColumnDefIds[1], .. Enumerable.Range(50_000_000, FakeVisible)];
        var filter = new CellChangeFilter(
            Start, Start.AddDays(2), DocumentId: doc.DocumentId, VisibleColumnIds: visible, Sort: AuditSort.ChangedAtDesc);
        var reader = new AuditReader(db);

        var watch = Stopwatch.StartNew();
        var page = await reader.ReadCellChangesAsync(filter, new CursorRequest(100), CancellationToken.None);
        var pageMs = watch.ElapsedMilliseconds;

        watch.Restart();
        var counts = await reader.CountCellChangesByColumnAsync(filter, Start.AddDays(1), CancellationToken.None);
        var countMs = watch.ElapsedMilliseconds;

        watch.Restart();
        var next = await reader.ReadCellChangesAsync(filter, new CursorRequest(100, page.NextCursor), CancellationToken.None);
        var nextMs = watch.ElapsedMilliseconds;

        output.WriteLine($"сторінка {pageMs} мс, підрахунок {countMs} мс, наступна {nextMs} мс");

        Assert.Equal(100, page.Items.Count);
        Assert.All(page.Items.Concat(next.Items), c => Assert.Equal(doc.ColumnDefIds[1], c.ColumnDefId));
        Assert.Equal(100, next.Items.Count);
        Assert.Empty(page.Items.Select(c => c.Id).Intersect(next.Items.Select(c => c.Id)));
        Assert.Equal([(doc.ColumnDefIds[1], (long)PerColumn)], counts.Select(c => (c.ColumnDefId, c.Total)));

        Assert.True(pageMs <= 2_000, $"сторінка {pageMs} мс");
        Assert.True(countMs <= 2_000, $"підрахунок {countMs} мс");
        Assert.True(nextMs <= 2_000, $"наступна сторінка {nextMs} мс");

        // Перелік без жодної колонки журналу документа — порожньо (звуження не відкриває прихованого).
        var none = filter with { VisibleColumnIds = [.. Enumerable.Range(50_000_000, FakeVisible)] };
        Assert.Empty((await reader.ReadCellChangesAsync(none, new CursorRequest(100), CancellationToken.None)).Items);
        Assert.Empty(await reader.CountCellChangesByColumnAsync(none, Start.AddDays(1), CancellationToken.None));
    }

    private static async Task FillAsync(SqlConnection connection, TestDocument doc, int columnDefId, int offsetSeconds)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandTimeout = 300;
        insert.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            SELECT DATEADD(second, n * 2 + @offset, @start), 201907, @doc, @row, N'R1',
                   @col, N'1', CAST(n AS nvarchar(20)), 1, N'UserEdit', 0
              FROM (SELECT TOP (@rows) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n
                      FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t;
            """;
        insert.Parameters.Add("@offset", SqlDbType.Int).Value = offsetSeconds;
        var start = insert.Parameters.Add("@start", SqlDbType.DateTime2);
        start.Scale = 3;
        start.Value = Start;
        insert.Parameters.Add("@doc", SqlDbType.BigInt).Value = doc.DocumentId;
        insert.Parameters.Add("@row", SqlDbType.BigInt).Value = doc.RowIds[0];
        insert.Parameters.Add("@col", SqlDbType.Int).Value = columnDefId;
        insert.Parameters.Add("@rows", SqlDbType.Int).Value = PerColumn;
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
