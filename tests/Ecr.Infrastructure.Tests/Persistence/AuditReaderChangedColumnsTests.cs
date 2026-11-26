using System.Data;
using System.Diagnostics;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ audit-filters-contract AC-8 / AC-13: перелік змінених колонок документа (фільтр «Column») і номер рядка в журналі.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ (AC-8): прибрати з <c>BuildCellChangeWhere</c> умову <c>ColumnDefId IN (SELECT Id FROM @visibleSet)</c>
/// — у переліку з'являється прихована колонка 2, обидва тести переліку червоні. AC-13: прибрати <c>tr.Ordinal</c> з
/// <c>PageWithNames</c> (або умову <c>tr.RowKey = a.RowKey</c>) — червоний тест номера рядка.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderChangedColumnsTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int PerColumn = 2_000;
    private const int FakeVisible = 125_000;
    private static readonly DateTime Start = new(2019, 8, 5, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_колонок_документа_з_тисячами_видимих_Id_за_2_с_і_без_прихованих()
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

        var reader = new AuditReader(db);
        var window = new CellChangeFilter(Start, Start.AddDays(2), DocumentId: doc.DocumentId);

        // Видима лише колонка 1 — серед 125 000 Id родоводу, яких у журналі документа немає (документ Land на live).
        var large = window with { VisibleColumnIds = [doc.ColumnDefIds[1], .. Enumerable.Range(60_000_000, FakeVisible)] };
        var watch = Stopwatch.StartNew();
        var columns = await reader.ReadChangedColumnsAsync(large, CancellationToken.None);
        output.WriteLine($"перелік колонок {watch.ElapsedMilliseconds} мс");

        var only = Assert.Single(columns);
        Assert.Equal(doc.ColumnDefIds[1], only.ColumnDefId);
        Assert.False(string.IsNullOrEmpty(only.ColumnCode));
        Assert.Equal(doc.SheetCode, only.SheetCode);
        Assert.False(string.IsNullOrEmpty(only.TableCode));
        Assert.True(watch.ElapsedMilliseconds <= 2_000, $"перелік колонок {watch.ElapsedMilliseconds} мс");

        // Малий перелік (без попереднього звуження) — та сама межа в SQL.
        var small = window with { VisibleColumnIds = [doc.ColumnDefIds[1]] };
        Assert.Equal([doc.ColumnDefIds[1]], (await reader.ReadChangedColumnsAsync(small, CancellationToken.None)).Select(c => c.ColumnDefId));

        // Жодної видимої колонки журналу — порожньо; порожній перелік — теж.
        Assert.Empty(await reader.ReadChangedColumnsAsync(
            window with { VisibleColumnIds = [.. Enumerable.Range(60_000_000, FakeVisible)] }, CancellationToken.None));
        Assert.Empty(await reader.ReadChangedColumnsAsync(window with { VisibleColumnIds = [] }, CancellationToken.None));

        // Без меж (наскрізний журнал) — обидві колонки, кожна один раз.
        Assert.Equal(
            [doc.ColumnDefIds[1], doc.ColumnDefIds[2]],
            (await reader.ReadChangedColumnsAsync(window, CancellationToken.None)).Select(c => c.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_журналу_несе_номер_рядка_документа_а_чужий_ключ_номера_не_отримує()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 3, ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var row = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[1])
            .Select(r => new { r.RowKeyValue, r.Ordinal })
            .SingleAsync();

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken.None);
            await InsertAsync(connection, doc, doc.RowIds[1], row.RowKeyValue, Start.AddHours(2));

            // Той самий TableRowId, але чужий ключ (вигадана адреса тестів, рядок іншого документа) — номер не підставляється.
            await InsertAsync(connection, doc, doc.RowIds[1], "FOREIGN-KEY", Start.AddHours(1));
        }

        var page = await new AuditReader(db).ReadCellChangesAsync(
            new CellChangeFilter(Start, Start.AddDays(1), DocumentId: doc.DocumentId),
            new CursorRequest(10),
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(row.Ordinal, Assert.Single(page.Items, c => c.RowKey == row.RowKeyValue).RowOrdinal);
        Assert.Equal(2, row.Ordinal);
        Assert.Null(Assert.Single(page.Items, c => c.RowKey == "FOREIGN-KEY").RowOrdinal);
    }

    private static async Task InsertAsync(SqlConnection connection, TestDocument doc, long rowId, string rowKey, DateTime at)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @doc, @row, @key, @col, N'1', N'2', 1, N'UserEdit', 0);
            """;
        var moment = insert.Parameters.Add("@at", SqlDbType.DateTime2);
        moment.Scale = 3;
        moment.Value = at;
        insert.Parameters.Add("@period", SqlDbType.Int).Value = doc.PeriodKey.Value;
        insert.Parameters.Add("@doc", SqlDbType.BigInt).Value = doc.DocumentId;
        insert.Parameters.Add("@row", SqlDbType.BigInt).Value = rowId;
        insert.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = rowKey;
        insert.Parameters.Add("@col", SqlDbType.Int).Value = doc.ColumnDefIds[0];
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task FillAsync(SqlConnection connection, TestDocument doc, int columnDefId, int offsetSeconds)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandTimeout = 300;
        insert.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            SELECT DATEADD(second, n * 2 + @offset, @start), 201908, @doc, @row, N'R1',
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
