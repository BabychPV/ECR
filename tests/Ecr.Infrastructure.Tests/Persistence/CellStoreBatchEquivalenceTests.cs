using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// P8, застосування імпорту 1b/3: <c>ICellStore.ApplyBatchAsync</c> і
/// <c>IRowStore.CreateRowsBatchAsync</c> на кількох екземплярах дають той
/// самий стан бази, що й послідовність поштучних викликів, — і коштують сталу
/// кількість звернень незалежно від кількості таблиць.
/// </summary>
/// <remarks>
/// ⚠ Порівнюються ДВА однакові світи (два документи тієї самої форми): пакет
/// пише в один, послідовність поштучних — в інший. Ідентифікатори й ключі
/// фікстури в них різні, тому стан зводиться до індексів (таблиця, рядок,
/// колонка), а версії — до «піднялася чи ні» плюс «повернута == у базі».
/// </remarks>
[Collection("SqlServer")]
public sealed class CellStoreBatchEquivalenceTests(SqlServerFixture sql)
{
    /// <summary>Період — не 2026-05…07 (їх архівують <c>ArchiveJobTests</c>).</summary>
    private const int PeriodKeyValue = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_змін_кількох_таблиць_дає_той_самий_стан_що_поштучні_виклики()
    {
        var batchWorld = await ArrangeAsync([2, 2]);
        var singleWorld = await ArrangeAsync([2, 2]);

        var batchBefore = await VersionsAsync(batchWorld);
        var singleBefore = await VersionsAsync(singleWorld);

        // ── Пакет: нові рядки й комірки усіх таблиць двома викликами ──────────
        IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>> batchResult;
        int batchAffected;
        await using (var db = batchWorld.Builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var created = await Rows(db).CreateRowsBatchAsync(
                [.. batchWorld.Tables.Select(t => new RowCreationBatch(t.InstanceId, Period, [RowKey.Create("NEW_1")], 99))],
                CancellationToken.None);

            var store = new NormalizedCellStore(db);
            batchResult = await store.ApplyBatchAsync(
                Changes(batchWorld, batchBefore, [.. created.Select(ids => ids[0])]), CancellationToken.None);
            batchAffected = store.LastUpsertRowsAffected;
            await tx.CommitAsync();
        }

        // ── Послідовність поштучних викликів ──────────────────────────────────
        var singleResult = new Dictionary<long, IReadOnlyDictionary<long, string>>();
        var singleAffected = 0;
        await using (var db = singleWorld.Builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var newRowIds = new List<long>();
            foreach (var table in singleWorld.Tables)
            {
                newRowIds.Add((await Rows(db).CreateRowsAsync(
                    table.InstanceId, Period, [RowKey.Create("NEW_1")], 99, CancellationToken.None))[0]);
            }

            var store = new NormalizedCellStore(db);
            foreach (var set in Changes(singleWorld, singleBefore, newRowIds))
            {
                singleResult[set.TableInstanceId] = await store.ApplyAsync(set, CancellationToken.None);
                singleAffected += store.LastUpsertRowsAffected;
            }

            await tx.CommitAsync();
        }

        var batchState = await StateAsync(batchWorld, batchBefore);
        var singleState = await StateAsync(singleWorld, singleBefore);

        Assert.Equal(singleState, batchState);
        Assert.Equal(singleAffected, batchAffected);

        // ⛔ Проти хибнозеленого: стан справді змінився в кожній таблиці —
        // нові, змінені й очищені комірки, новий рядок, піднята версія.
        Assert.True(batchAffected >= 3 * batchWorld.Tables.Count, $"MERGE змінив лише {batchAffected} комірок.");
        foreach (var index in Enumerable.Range(0, batchWorld.Tables.Count))
        {
            Assert.Contains($"{index}|NEW_1|ord=99|del=False|new", batchState);
            Assert.Contains($"{index}|r0|ord=1|del=False|bumped=True", batchState);
            Assert.DoesNotContain(batchState, line => line.StartsWith($"{index}|r1|c{batchWorld.Tables[index].ColumnIds.Count - 1}|", StringComparison.Ordinal));
        }

        Assert.Contains(batchState, line => line.Contains("|Київ|", StringComparison.Ordinal));

        // Повернуті версії: ті самі рядки, і саме ті, що лежать у базі.
        Assert.Equal(
            await ReturnedAsync(singleWorld, singleResult),
            await ReturnedAsync(batchWorld, batchResult));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Конфлікт_версії_в_пакеті_називає_перший_винний_екземпляр_і_не_лишає_нічого()
    {
        var batchWorld = await ArrangeAsync([1, 1, 1]);
        var singleWorld = await ArrangeAsync([1, 1, 1]);

        var batchBefore = await VersionsAsync(batchWorld);
        var singleBefore = await VersionsAsync(singleWorld);

        // Чужий запис між читанням і записом — у ДВОХ таблицях (2 і 3).
        await BumpAsync(batchWorld.Tables[2].RowIds[0], batchWorld.Tables[3].RowIds[0]);
        await BumpAsync(singleWorld.Tables[2].RowIds[0], singleWorld.Tables[3].RowIds[0]);

        var batchStateBefore = await StateAsync(batchWorld, batchBefore);

        ConcurrencyConflictException batchError;
        await using (var db = batchWorld.Builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            batchError = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new NormalizedCellStore(db)
                .ApplyBatchAsync(Changes(batchWorld, batchBefore, null, claimAll: true), CancellationToken.None));
            await tx.RollbackAsync();
        }

        ConcurrencyConflictException? singleError = null;
        var failedAt = -1;
        await using (var db = singleWorld.Builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var store = new NormalizedCellStore(db);
            var sets = Changes(singleWorld, singleBefore, null, claimAll: true);
            for (var i = 0; i < sets.Count && singleError is null; i++)
            {
                try
                {
                    await store.ApplyAsync(sets[i], CancellationToken.None);
                }
                catch (ConcurrencyConflictException ex)
                {
                    singleError = ex;
                    failedAt = i;
                }
            }

            await tx.RollbackAsync();
        }

        Assert.NotNull(singleError);
        Assert.Equal(2, failedAt);

        Assert.Equal(singleError.ErrorCode, batchError.ErrorCode);
        Assert.Equal(singleError.Message, batchError.Message);
        Assert.Equal(singleError.Details!["messageKey"], batchError.Details!["messageKey"]);
        Assert.Equal(singleError.Details["rowCount"], batchError.Details["rowCount"]);
        Assert.Equal(
            Labels(singleWorld, (IEnumerable<long>)singleError.Details[CellChangeSet.StaleRowIdsDetail]!),
            Labels(batchWorld, (IEnumerable<long>)batchError.Details[CellChangeSet.StaleRowIdsDetail]!));
        Assert.Equal(["2|r0"], Labels(batchWorld, (IEnumerable<long>)batchError.Details[CellChangeSet.StaleRowIdsDetail]!));

        // Blame: той самий ключ і формат, що додає ExcelImporter.Blame.
        Assert.Equal(
            batchWorld.Tables[2].InstanceId.ToString(CultureInfo.InvariantCulture),
            batchError.Details["tableInstanceId"]);
        Assert.False(singleError.Details.ContainsKey("tableInstanceId"));

        // DAT-05: викликач відкотив — жодної комірки, жодної піднятої версії.
        Assert.Equal(batchStateBefore, await StateAsync(batchWorld, batchBefore));

        // Без транзакції викликача — власна на весь пакет, і так само нічого.
        await using (var db = batchWorld.Builder.CreateContext())
        {
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new NormalizedCellStore(db)
                .ApplyBatchAsync(Changes(batchWorld, batchBefore, null, claimAll: true), CancellationToken.None));
        }

        Assert.Equal(batchStateBefore, await StateAsync(batchWorld, batchBefore));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Повтор_екземпляра_в_пакеті_відхиляється()
    {
        var world = await ArrangeAsync([1]);
        var before = await VersionsAsync(world);
        var sets = Changes(world, before, null);

        await using var db = world.Builder.CreateContext();
        await Assert.ThrowsAsync<ArgumentException>(() => new NormalizedCellStore(db)
            .ApplyBatchAsync([sets[0], sets[1], sets[0]], CancellationToken.None));
    }

    /// <summary>
    /// Храповик: пакетний запис коштує ОДНАКОВО на 3 і на 12 таблицях.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетний_запис_коштує_сталу_кількість_звернень_на_3_і_12_таблиць()
    {
        var world = await ArrangeAsync([3, 3, 3, 2]);
        Assert.Equal(12, world.Tables.Count);
        var before = await VersionsAsync(world);
        var sets = Changes(world, before, null, claimAll: true);

        await using var db = world.Builder.CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.StatisticsEnabled = true;
        var store = new NormalizedCellStore(db);

        // ⚠ Команди, що їх NormalizedCellStore виконує напряму через SqlCommand,
        // DbCommandInterceptor EF не бачить — рахує сам SqlClient
        // (UnpreparedExecs + PreparedExecs — виконані команди з'єднання).
        long Executions()
        {
            var stats = connection.RetrieveStatistics();
            return Convert.ToInt64(stats["UnpreparedExecs"], CultureInfo.InvariantCulture)
                   + Convert.ToInt64(stats["PreparedExecs"], CultureInfo.InvariantCulture);
        }

        async Task<long> Measure(IReadOnlyCollection<CellChangeSet> changes)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            connection.ResetStatistics();
            await store.ApplyBatchAsync(changes, CancellationToken.None);
            var executions = Executions();
            await tx.RollbackAsync();
            return executions;
        }

        var three = await Measure([.. sets.Take(3)]);
        var twelve = await Measure(sets);

        // Поштучно — для порівняння: звернення ростуть із кількістю таблиць.
        long sequential;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            connection.ResetStatistics();
            foreach (var set in sets)
            {
                await store.ApplyAsync(set, CancellationToken.None);
            }

            sequential = Executions();
            await tx.RollbackAsync();
        }

        Assert.True(three == twelve, $"ApplyBatchAsync: 3 → {three}; 12 → {twelve}; поштучно 12 → {sequential}");

        // Захоплення, видалення, MERGE, «дотик» — по одному.
        Assert.Equal(4, twelve);
        Assert.Equal(4 * 12, sequential);
    }

    private RowStore Rows(EcrDbContext db)
        => new(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));

    private sealed record Table(long InstanceId, int TableDefId, IReadOnlyList<int> ColumnIds, IReadOnlyList<long> RowIds);

    private sealed record World(TestDocumentBuilder Builder, IReadOnlyList<Table> Tables);

    /// <summary>
    /// Документ за 202610: базова таблиця (3 колонки, перша текстова) і додаткові
    /// аркуші з таблицями по 2 числові колонки; у кожній 3 рядки, у рядках 0 і 1
    /// вже є значення останньої колонки.
    /// </summary>
    private async Task<World> ArrangeAsync(IReadOnlyList<int> tablesPerSheet)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 3, rowCount: 3, ct: CancellationToken.None);
        var extra = await MultiTableDocument.AddTablesAsync(
            builder, doc, tablesPerSheet, columnCount: 2, rowCount: 3, ct: CancellationToken.None);

        var tables = new List<Table> { new(doc.TableInstanceId, doc.TableDefId, doc.ColumnDefIds, doc.RowIds) };
        tables.AddRange(extra.Select(t => new Table(t.TableInstanceId, t.TableDefId, t.ColumnDefIds, t.RowIds)));

        await using var db = builder.CreateContext();
        var store = new NormalizedCellStore(db);
        foreach (var table in tables)
        {
            await store.ApplyAsync(
                new CellChangeSet(
                    table.InstanceId,
                    [Cell(table, 0, 1m), Cell(table, 1, 2m)],
                    [],
                    [table.RowIds[0], table.RowIds[1]],
                    ChangedByUserId: 1,
                    IsLateEdit: false),
                CancellationToken.None);
        }

        return new World(builder, tables);
    }

    /// <summary>
    /// Однаковий за формою набір змін на кожну таблицю: змінити комірку рядка 0,
    /// очистити рядка 1, нова комірка в рядку 2 і в новому рядку. Версії рядків
    /// 0 і 1 заявляються на парних таблицях (або на всіх — <paramref name="claimAll"/>),
    /// непарні пишуть без звірки, як <c>RecalculationService</c>.
    /// </summary>
    private static List<CellChangeSet> Changes(
        World world, Dictionary<long, string> versions, List<long>? newRowIds, bool claimAll = false)
    {
        var result = new List<CellChangeSet>();
        for (var i = 0; i < world.Tables.Count; i++)
        {
            var table = world.Tables[i];
            var last = table.ColumnIds.Count - 1;
            var upserts = new List<CellRecord> { Cell(table, 0, 100m + i), Cell(table, 2, 200m + i) };
            var touched = new List<long> { table.RowIds[0], table.RowIds[1], table.RowIds[2] };

            if (newRowIds is not null)
            {
                upserts.Add(new CellRecord(
                    new CellAddress(Period, newRowIds[i], table.ColumnIds[last]),
                    table.TableDefId,
                    new CellValueData { ValueNumeric = 300m + i }));
                touched.Add(newRowIds[i]);
            }

            if (i == 0)
            {
                upserts.Add(new CellRecord(
                    new CellAddress(Period, table.RowIds[2], table.ColumnIds[0]),
                    table.TableDefId,
                    new CellValueData { ValueString = "Київ" }));
            }

            var expected = claimAll || i % 2 == 0
                ? new Dictionary<long, string>
                {
                    [table.RowIds[0]] = versions[table.RowIds[0]],
                    [table.RowIds[1]] = versions[table.RowIds[1]],
                }
                : null;

            result.Add(new CellChangeSet(
                table.InstanceId,
                upserts,
                [new CellAddress(Period, table.RowIds[1], table.ColumnIds[last])],
                touched,
                ChangedByUserId: 1,
                IsLateEdit: false,
                expected));
        }

        return result;
    }

    private static CellRecord Cell(Table table, int row, decimal value)
        => new(
            new CellAddress(Period, table.RowIds[row], table.ColumnIds[^1]),
            table.TableDefId,
            new CellValueData { ValueNumeric = value });

    private async Task<Dictionary<long, string>> VersionsAsync(World world)
    {
        var result = new Dictionary<long, string>();
        await QueryAsync(
            $"SELECT Id, RowVersion FROM doc.TableRow WHERE PeriodKey = {PeriodKeyValue} AND TableInstanceId IN ({InList(world)})",
            reader => result[reader.GetInt64(0)] = Convert.ToBase64String(reader.GetFieldValue<byte[]>(1)));
        return result;
    }

    /// <summary>
    /// Стан світу в індексах: рядки (порядок, видалення, чи піднялася версія
    /// відносно <paramref name="before"/>) і комірки (значення, ознаки).
    /// </summary>
    private async Task<List<string>> StateAsync(World world, Dictionary<long, string> before)
    {
        var lines = new List<string>();
        await QueryAsync(
            $"""
            SELECT r.TableInstanceId, r.Id, r.RowKey, r.Ordinal, r.IsDeleted, r.RowVersion,
                   c.ColumnDefId, c.TableDefId, c.ValueString, c.ValueNumeric, c.IsEmpty, c.IsCalculated
            FROM doc.TableRow AS r
            LEFT JOIN doc.CellValue AS c ON c.PeriodKey = r.PeriodKey AND c.TableRowId = r.Id
            WHERE r.PeriodKey = {PeriodKeyValue} AND r.TableInstanceId IN ({InList(world)})
            """,
            reader =>
            {
                var instance = reader.GetInt64(0);
                var ti = IndexOfInstance(world, instance);
                var rowId = reader.GetInt64(1);
                var row = RowLabel(world.Tables[ti], rowId, reader.GetString(2));
                var version = Convert.ToBase64String(reader.GetFieldValue<byte[]>(5));
                var bumped = before.TryGetValue(rowId, out var old) ? $"bumped={old != version}" : "new";

                lines.Add($"{ti}|{row}|ord={reader.GetInt32(3)}|del={reader.GetBoolean(4)}|{bumped}");

                if (!reader.IsDBNull(6))
                {
                    var table = world.Tables[ti];
                    var column = table.ColumnIds.ToList().IndexOf(reader.GetInt32(6));
                    var tableDef = world.Tables.ToList().FindIndex(t => t.TableDefId == reader.GetInt32(7));
                    var text = reader.IsDBNull(8) ? "∅" : reader.GetString(8);
                    var number = reader.IsDBNull(9) ? "∅" : reader.GetDecimal(9).ToString(CultureInfo.InvariantCulture);
                    lines.Add($"{ti}|{row}|c{column}|t{tableDef}|{text}|{number}|{reader.GetBoolean(10)}|{reader.GetBoolean(11)}");
                }
            });

        return [.. lines.Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>Повернуті версії в індексах; кожна мусить збігатися з базою.</summary>
    private async Task<List<string>> ReturnedAsync(
        World world, IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>> returned)
    {
        var current = await VersionsAsync(world);
        var keys = new Dictionary<long, string>();
        await QueryAsync(
            $"SELECT Id, RowKey FROM doc.TableRow WHERE PeriodKey = {PeriodKeyValue} AND TableInstanceId IN ({InList(world)})",
            reader => keys[reader.GetInt64(0)] = reader.GetString(1));

        var lines = new List<string>();
        foreach (var (instance, versions) in returned)
        {
            var ti = IndexOfInstance(world, instance);
            foreach (var (rowId, version) in versions)
            {
                Assert.Equal(current[rowId], version);
                lines.Add($"{ti}|{RowLabel(world.Tables[ti], rowId, keys[rowId])}");
            }
        }

        Assert.NotEmpty(lines);
        return [.. lines.Order(StringComparer.Ordinal)];
    }

    private static List<string> Labels(World world, IEnumerable<long> rowIds)
        => [.. rowIds.Select(id =>
        {
            var ti = world.Tables.ToList().FindIndex(t => t.RowIds.Contains(id));
            return $"{ti}|{RowLabel(world.Tables[ti], id, "?")}";
        })];

    private static int IndexOfInstance(World world, long instanceId)
        => world.Tables.ToList().FindIndex(t => t.InstanceId == instanceId);

    private static string RowLabel(Table table, long rowId, string rowKey)
    {
        var index = table.RowIds.ToList().IndexOf(rowId);
        return index >= 0 ? $"r{index}" : rowKey;
    }

    private static string InList(World world)
        => string.Join(",", world.Tables.Select(t => t.InstanceId.ToString(CultureInfo.InvariantCulture)));

    private async Task BumpAsync(params long[] rowIds)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = {PeriodKeyValue} " +
            $"AND Id IN ({string.Join(",", rowIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        Assert.Equal(rowIds.Length, await command.ExecuteNonQueryAsync());
    }

    private async Task QueryAsync(string text, Action<SqlDataReader> onRow)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            onRow(reader);
        }
    }
}
