using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecr.Application.Audit;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Ціна сторінки журналу аркуша на великому журналі: документ розріджений серед шуму (у 4 рази більше змін чужих
/// документів у тих самих місяцях). Власна база - велика заливка не повинна розпирати спільну (стеля колекції).
/// </summary>
public sealed class SheetActivityPerfTests(ITestOutputHelper output)
{
    private const int Period = 201903;
    private const int Own = 200_000;
    private const int Noise = 800_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перша_й_наступна_сторінка_на_розрідженому_журналі_вкладаються_у_бюджет()
    {
        var database = SqlServerFixture.WithOwnDatabase("_sheetlog");
        await database.InitializeAsync();
        try
        {
            var builder = new TestDocumentBuilder(database.ConnectionString);
            var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2, ct: CancellationToken.None);
            await using var db = builder.CreateContext();

            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(CancellationToken.None);
                await Fill(connection, doc.DocumentId, doc, Own, step: 40);
                await Fill(connection, doc.DocumentId + 1_000_000, doc, Noise, step: 10);
            }

            var workflow = Substitute.For<IWorkflowStore>();
            workflow.GetHistoryAsync(doc.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

            // «Зараз» - у кінці даних: вікно 396 діб накриває місячні партиції усього діапазону.
            var now = new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Own * 40L).AddDays(1);
            var query = new SheetActivityQuery(new AuditReader(db), workflow, new SheetActivityReaderTests.FixedClock(now));
            var scope = new SheetActivityScope(doc.DocumentId, Period, "SA", [doc.ColumnDefIds[1], doc.ColumnDefIds[2]]);

            var warm = await query.ReadAsync(scope, 50, null, CancellationToken.None);
            Assert.Equal(50, warm.Items.Count);
            Assert.NotNull(warm.NextCursor);
            await query.ReadAsync(scope, 50, warm.NextCursor, CancellationToken.None);

            const int runs = 5;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < runs; i++)
            {
                await query.ReadAsync(scope, 50, null, CancellationToken.None);
            }

            var first = watch.Elapsed.TotalMilliseconds / runs;

            watch.Restart();
            for (var i = 0; i < runs; i++)
            {
                await query.ReadAsync(scope, 50, warm.NextCursor, CancellationToken.None);
            }

            var second = watch.Elapsed.TotalMilliseconds / runs;

            var window = now - SheetActivityQuery.CellWindow;
            var firstFilter = new CellChangeFilter(
                window, now.AddDays(1), doc.DocumentId,
                VisibleColumnIds: [doc.ColumnDefIds[1], doc.ColumnDefIds[2]], PeriodKey: Period, Descending: true, HumanOnly: true);
            var ioFirst = await Io(database.ConnectionString, firstFilter, after: null);
            var ioDeep = await Io(database.ConnectionString, firstFilter, after: (now.AddSeconds(-(Own * 20L)), long.MaxValue));

            output.WriteLine($"{Own} своїх + {Noise} чужих: перша {first:F1} мс, наступна {second:F1} мс");
            output.WriteLine($"IO перша: {ioFirst}");
            output.WriteLine($"IO з курсором на середині журналу: {ioDeep}");

            Assert.True(first <= 300, $"перша сторінка {first:F1} мс; {ioFirst}");
            Assert.True(second <= 300, $"наступна сторінка {second:F1} мс; {ioDeep}");
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task Fill(SqlConnection connection, long documentId, TestDocument doc, int rows, int step)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandTimeout = 600;
        insert.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            SELECT DATEADD(second, n * @step, CAST('2021-03-01' AS datetime2(3))), @period, @doc, @row, N'R1',
                   @col, N'1', CAST(n AS nvarchar(20)), 1, N'UserEdit', 0
              FROM (SELECT TOP (@rows) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n
                      FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c) AS t;
            """;
        insert.Parameters.Add("@step", SqlDbType.Int).Value = step;
        insert.Parameters.Add("@period", SqlDbType.Int).Value = Period;
        insert.Parameters.Add("@doc", SqlDbType.BigInt).Value = documentId;
        insert.Parameters.Add("@row", SqlDbType.BigInt).Value = doc.RowIds[0];
        insert.Parameters.Add("@col", SqlDbType.Int).Value = doc.ColumnDefIds[1];
        insert.Parameters.Add("@rows", SqlDbType.Int).Value = rows;
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> Io(string connectionString, CellChangeFilter filter, (DateTime At, long Id)? after)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        var messages = new List<string>();
        connection.InfoMessage += (_, args) => messages.Add(args.Message);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET STATISTICS IO ON;";
            await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        var where = AuditReader.BuildCellChangeWhere(filter, command, after).ToString();
        command.CommandText = AuditReader.CellChangesPageSql(where, "ChangedAt DESC, Id DESC", "a.ChangedAt DESC, a.Id DESC");
        command.Parameters.AddWithValue("@take", 51);
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        var parts = messages
            .SelectMany(m => Regex.Matches(m, @"Table '(\w+)'\. Scan count (\d+), logical reads (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5)).Cast<Match>())
            .Where(m => m.Groups[1].Value is "CellChange" or "AuditCellChange")
            .Select(m => $"{m.Groups[1].Value}: scans {m.Groups[2].Value}, logical reads {m.Groups[3].Value}");
        return string.Join("; ", parts);
    }
}
