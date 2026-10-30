// tests/Ecr.Infrastructure.Tests/Persistence/SheetChangeStoreTests.cs
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Маркер змін аркуша (<see cref="SheetChangeStore"/>): довга транзакція не губиться між опитуваннями, і ціна проби
/// на великому синтетичному аркуші (власна база — заливка не розпирає спільну).
/// </summary>
public sealed class SheetChangeStoreTests(ITestOutputHelper output)
{
    /// <summary>Рядків в одному екземплярі аркуша: ≈ 6× найбільшого аркуша Land (810 рядків на 15 таблиць).</summary>
    private const int SheetRows = 5_000;

    /// <summary>Рядків чужого документа в тій самій партиції періоду: спершу стільки, потім ще втричі більше.</summary>
    private const int NoiseRows = 100_000;

    /// <summary>Стеля логічних читань проби «нічого не змінилось» на <see cref="SheetRows"/> рядках.</summary>
    /// <remarks>
    /// ⚠ Ціна лінійна від рядків СВОГО аркуша (seek + key lookup версії, ≈ 3 читання на рядок), а від чужих даних
    /// не залежить зовсім — це стереже друга половина тесту (шум ×4, читань стільки ж).
    /// </remarks>
    private const long MaxProbeReads = 4L * SheetRows;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Незафіксована_зміна_не_губиться_а_зявляється_в_наступному_опитуванні()
    {
        var database = SqlServerFixture.WithOwnDatabase("_sheetchg");
        await database.InitializeAsync();
        try
        {
            await RcsiOnAsync(database.ConnectionString);
            var builder = new TestDocumentBuilder(database.ConnectionString);
            var doc = await builder.BuildAsync(columnCount: 2, rowCount: 3, ct: CancellationToken.None);
            await using var db = builder.CreateContext();
            var store = new SheetChangeStore(db);

            var m0 = await store.CurrentMarkerAsync(CancellationToken.None);
            var idle = await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId], m0, 50, CancellationToken.None);
            Assert.Empty(idle.Rows);
            Assert.True(idle.Marker >= m0);

            // Довга транзакція (імпорт): рядок «торкнуто», але не зафіксовано.
            await using var writer = new SqlConnection(database.ConnectionString);
            await writer.OpenAsync(CancellationToken.None);
            await using var tx = (SqlTransaction)await writer.BeginTransactionAsync(CancellationToken.None);
            await using (var touch = writer.CreateCommand())
            {
                touch.Transaction = tx;
                touch.CommandText = "UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = @p AND Id = @id;";
                touch.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
                touch.Parameters.AddWithValue("@id", doc.RowIds[1]);
                await touch.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // Опитування під час транзакції: рядка не видно, а межа НЕ проходить повз його версію.
            var during = await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId], m0, 50, CancellationToken.None);
            Assert.Empty(during.Rows);

            await tx.CommitAsync(CancellationToken.None);

            // Наступне опитування від маркера «під час» бачить рядок.
            var after = await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId], during.Marker, 50, CancellationToken.None);
            var row = Assert.Single(after.Rows);
            Assert.Equal(doc.RowIds[1], row.TableRowId);
            Assert.Equal(doc.TableInstanceId, row.TableInstanceId);
            Assert.Equal(doc.TableDefId, row.TableDefId);

            // Інша таблиця (не названа викликачем) у пробу не потрапляє.
            var other = await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId + 1_000_000], during.Marker, 50, CancellationToken.None);
            Assert.Empty(other.Rows);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проба_великого_аркуша_без_змін_не_читає_шуму_і_вкладається_в_бюджет()
    {
        var database = SqlServerFixture.WithOwnDatabase("_sheetchgperf");
        await database.InitializeAsync();
        try
        {
            await RcsiOnAsync(database.ConnectionString);
            var builder = new TestDocumentBuilder(database.ConnectionString);
            var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1, ct: CancellationToken.None);
            var noise = await builder.BuildAsync(columnCount: 3, rowCount: 1, ct: CancellationToken.None);

            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(CancellationToken.None);
                await FillRowsAsync(connection, doc.PeriodKey.Value, doc.TableInstanceId, SheetRows);
                await FillRowsAsync(connection, noise.PeriodKey.Value, noise.TableInstanceId, NoiseRows);
                await using var stats = connection.CreateCommand();
                stats.CommandTimeout = 600;
                stats.CommandText = "UPDATE STATISTICS doc.TableRow; UPDATE STATISTICS doc.TableInstance;";
                await stats.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using var db = builder.CreateContext();
            var store = new SheetChangeStore(db);
            var marker = await store.CurrentMarkerAsync(CancellationToken.None);

            // Розігрів і замір «нічого не змінилось» (стан 99 % опитувань).
            await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId], marker, 50, CancellationToken.None);
            const int runs = 10;
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < runs; i++)
            {
                var probe = await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId], marker, 50, CancellationToken.None);
                Assert.Empty(probe.Rows);
            }

            var idleMs = watch.Elapsed.TotalMilliseconds / runs;
            var idleReads = await ProbeReadsAsync(database.ConnectionString, doc.DocumentId, doc.PeriodKey.Value, doc.TableDefId, marker);

            // Шуму вчетверо більше — проба читає стільки ж (скану партиції періоду немає).
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(CancellationToken.None);
                await FillRowsAsync(connection, noise.PeriodKey.Value, noise.TableInstanceId, 3 * NoiseRows, offset: NoiseRows);
                await using var stats = connection.CreateCommand();
                stats.CommandTimeout = 600;
                stats.CommandText = "UPDATE STATISTICS doc.TableRow;";
                await stats.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var moreNoiseReads = await ProbeReadsAsync(database.ConnectionString, doc.DocumentId, doc.PeriodKey.Value, doc.TableDefId, marker);
            Assert.True(
                Math.Abs(moreNoiseReads - idleReads) <= 10,
                $"проба залежить від чужих даних: {idleReads} → {moreNoiseReads} логічних читань після шуму ×4");

            // Змінено 300 рядків чужим користувачем: проба (50 найсвіжіших) + авторство.
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(CancellationToken.None);
                await using var change = connection.CreateCommand();
                change.CommandText = """
                    UPDATE TOP (300) doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = @p AND TableInstanceId = @i;
                    INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                                ChangedByUserId, Origin, IsLateEdit)
                    SELECT SYSUTCDATETIME(), r.PeriodKey, @doc, r.Id, r.RowKey, @col, NULL, N'1', 777, N'UserEdit', 0
                      FROM doc.TableRow r WHERE r.PeriodKey = @p AND r.TableInstanceId = @i AND r.RowVersion >= @since;
                    """;
                change.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
                change.Parameters.AddWithValue("@i", doc.TableInstanceId);
                change.Parameters.AddWithValue("@doc", doc.DocumentId);
                change.Parameters.AddWithValue("@col", doc.ColumnDefIds[1]);
                change.Parameters.Add("@since", SqlDbType.Binary, 8).Value = SheetChangeStore.FromVersion(marker);
                await change.ExecuteNonQueryAsync(CancellationToken.None);
            }

            watch.Restart();
            var changed = await store.ProbeAsync(doc.DocumentId, doc.PeriodKey.Value, [doc.TableDefId], marker, 50, CancellationToken.None);
            var attribution = await store.AttributeAsync(
                doc.DocumentId, [.. changed.Rows.Select(r => r.TableRowId)], [], DateTime.UtcNow.AddMinutes(-15), 1, CancellationToken.None);
            var changedMs = watch.Elapsed.TotalMilliseconds;

            Assert.Equal(50, changed.Rows.Count);
            Assert.True(changed.HasMore);
            Assert.Equal(50, attribution.ForeignRowIds.Count);
            Assert.Equal(777, attribution.LastForeignUserId);

            output.WriteLine($"{SheetRows} рядків аркуша + {NoiseRows} шуму: без змін {idleMs:F1} мс, {idleReads} логічних читань TableRow");
            output.WriteLine($"шум ×4: {moreNoiseReads} логічних читань TableRow");
            output.WriteLine($"300 змінених рядків: проба + авторство {changedMs:F1} мс");

            Assert.True(idleReads <= MaxProbeReads, $"проба без змін: {idleReads} логічних читань, стеля {MaxProbeReads}");

            // ⚠ Час — запас для спільного CI-раннера; на робочій машині ≈ одиниці мс (див. вивід), Land ≈ 2,7 мс.
            Assert.True(idleMs <= 100, $"проба без змін {idleMs:F1} мс на {SheetRows} рядках");
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task RcsiOnAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @s nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;'; EXEC (@s);";
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        // ROLLBACK IMMEDIATE рве чужі з'єднання бази — пул не повинен віддати розірване наступному виклику.
        SqlConnection.ClearAllPools();
    }

    private static async Task FillRowsAsync(SqlConnection connection, int periodKey, long instanceId, int rows, int offset = 0)
    {
        await using var insert = connection.CreateCommand();
        insert.CommandTimeout = 600;
        insert.CommandText = """
            INSERT INTO doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, Ordinal, ModifiedAt)
            SELECT @p, NEXT VALUE FOR doc.TableRowSeq, @i, CONCAT(N'S', t.n + @offset), 100 + t.n + @offset, SYSUTCDATETIME()
              FROM (SELECT TOP (@rows) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n
                      FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS t;
            """;
        insert.Parameters.Add("@p", SqlDbType.Int).Value = periodKey;
        insert.Parameters.Add("@i", SqlDbType.BigInt).Value = instanceId;
        insert.Parameters.Add("@rows", SqlDbType.Int).Value = rows;
        insert.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Логічні читання <c>doc.TableRow</c> тієї самої команди проби (<c>SET STATISTICS IO</c>).</summary>
    private static async Task<long> ProbeReadsAsync(string connectionString, long documentId, int periodKey, int tableDefId, ulong since)
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
        command.CommandText = SheetChangeStore.ProbeSql;
        command.Parameters.Add("@period", SqlDbType.Int).Value = periodKey;
        command.Parameters.Add("@doc", SqlDbType.BigInt).Value = documentId;
        command.Parameters.Add("@tables", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(new[] { tableDefId });
        command.Parameters.Add("@since", SqlDbType.Binary, 8).Value = SheetChangeStore.FromVersion(since);
        command.Parameters.Add("@sample", SqlDbType.Int).Value = 50;
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.NextResultAsync(CancellationToken.None))
            {
            }
        }

        return messages
            .SelectMany(m => Regex.Matches(m, @"Table 'TableRow'\. Scan count \d+, logical reads (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5)).Cast<Match>())
            .Sum(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
