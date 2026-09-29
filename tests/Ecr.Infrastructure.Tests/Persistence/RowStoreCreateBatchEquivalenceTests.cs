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
/// P8, застосування імпорту 1b/3: <c>IRowStore.CreateRowsBatchAsync</c> на
/// кількох екземплярах == поштучний <c>CreateRowsAsync</c> на кожен, з тією
/// самою відмовою на зайнятому ключі, — і сталою кількістю звернень.
/// </summary>
[Collection("SqlServer")]
public sealed class RowStoreCreateBatchEquivalenceTests(SqlServerFixture sql)
{
    private const int PeriodKeyValue = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_нових_рядків_кількох_таблиць_тотожний_поштучним()
    {
        var batchWorld = await ArrangeAsync([2, 2]);
        var singleWorld = await ArrangeAsync([2, 2]);

        IReadOnlyList<IReadOnlyList<long>> batchIds;
        await using (var db = batchWorld.Builder.CreateContext())
        {
            batchIds = await Store(db).CreateRowsBatchAsync(Requests(batchWorld), CancellationToken.None);
        }

        var singleIds = new List<IReadOnlyList<long>>();
        await using (var db = singleWorld.Builder.CreateContext())
        {
            foreach (var request in Requests(singleWorld))
            {
                singleIds.Add(await Store(db).CreateRowsAsync(
                    request.TableInstanceId, request.PeriodKey, request.RowKeys, request.Ordinal, CancellationToken.None));
            }
        }

        // Форма результату: той самий порядок наборів і ключів.
        Assert.Equal(singleIds.Select(ids => ids.Count), batchIds.Select(ids => ids.Count));
        Assert.Empty(batchIds[3]);

        // Один діапазон SEQUENCE у порядку входу.
        var flat = batchIds.SelectMany(ids => ids).ToList();
        Assert.Equal(Enumerable.Range(0, flat.Count).Select(k => flat[0] + k), flat);

        var batchState = await StateAsync(batchWorld, batchIds);
        var singleState = await StateAsync(singleWorld, singleIds);
        Assert.Equal(singleState, batchState);
        Assert.Equal(7, batchState.Count(line => line.Contains("|N", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Зайнятий_ключ_у_пакеті_дає_ту_саму_відмову_з_першим_винним_екземпляром_і_не_лишає_нічого()
    {
        var batchWorld = await ArrangeAsync([1, 1, 1]);
        var singleWorld = await ArrangeAsync([1, 1, 1]);

        // Ключ DUP уже зайнятий у таблицях 2 і 3 обох світів.
        foreach (var world in new[] { batchWorld, singleWorld })
        {
            await using var db = world.Builder.CreateContext();
            foreach (var index in new[] { 2, 3 })
            {
                await Store(db).CreateRowsAsync(
                    world.Instances[index], Period, [RowKey.Create("DUP")], 5, CancellationToken.None);
            }
        }

        static List<RowCreationBatch> Clashing(World world)
            =>
            [
                new(world.Instances[0], Period, [RowKey.Create("OK_1")], 7),
                new(world.Instances[1], Period, [RowKey.Create("OK_2"), RowKey.Create("OK_3")], 7),
                new(world.Instances[2], Period, [RowKey.Create("OK_4"), RowKey.Create("DUP")], 7),
                new(world.Instances[3], Period, [RowKey.Create("DUP")], 7),
            ];

        var before = await CountAsync(batchWorld);

        BusinessRuleException batchError;
        await using (var db = batchWorld.Builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            batchError = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Store(db).CreateRowsBatchAsync(Clashing(batchWorld), CancellationToken.None));
            await tx.RollbackAsync();
        }

        BusinessRuleException? singleError = null;
        var failedAt = -1;
        await using (var db = singleWorld.Builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var requests = Clashing(singleWorld);
            for (var i = 0; i < requests.Count && singleError is null; i++)
            {
                try
                {
                    await Store(db).CreateRowsAsync(
                        requests[i].TableInstanceId, Period, requests[i].RowKeys, 7, CancellationToken.None);
                }
                catch (BusinessRuleException ex)
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
        Assert.Equal((IEnumerable<string>)singleError.Details["rowKeys"]!, (IEnumerable<string>)batchError.Details["rowKeys"]!);
        Assert.Equal(
            batchWorld.Instances[2].ToString(CultureInfo.InvariantCulture),
            batchError.Details["tableInstanceId"]);

        // DAT-05: нічого не лишилось — ні в транзакції викликача, ні без неї.
        Assert.Equal(before, await CountAsync(batchWorld));
        await using (var db = batchWorld.Builder.CreateContext())
        {
            await Assert.ThrowsAsync<BusinessRuleException>(
                () => Store(db).CreateRowsBatchAsync(Clashing(batchWorld), CancellationToken.None));
        }

        Assert.Equal(before, await CountAsync(batchWorld));

        // Дублікат УСЕРЕДИНІ пакета — винний другий набір того самого екземпляра.
        await using (var db = batchWorld.Builder.CreateContext())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Store(db).CreateRowsBatchAsync(
                [
                    new(batchWorld.Instances[0], Period, [RowKey.Create("TWICE")], 7),
                    new(batchWorld.Instances[1], Period, [RowKey.Create("ONCE")], 7),
                    new(batchWorld.Instances[0], Period, [RowKey.Create("TWICE")], 7),
                ],
                CancellationToken.None));

            Assert.Equal("ECR-ROW-0409", error.ErrorCode);
            Assert.Equal("err.ECR-ROW-0409.rowKeyExists", error.Details!["messageKey"]);
            Assert.Equal(batchWorld.Instances[0].ToString(CultureInfo.InvariantCulture), error.Details["tableInstanceId"]);
        }

        Assert.Equal(before, await CountAsync(batchWorld));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_нових_рядків_коштує_сталу_кількість_звернень_на_3_і_12_таблиць()
    {
        var world = await ArrangeAsync([3, 3, 3, 2]);
        Assert.Equal(12, world.Instances.Count);

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);

        async Task<CommandTallySnapshot> Measure(IEnumerable<long> instances, string key)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            counter.Tally.Reset();
            await Store(db).CreateRowsBatchAsync(
                [.. instances.Select(id => new RowCreationBatch(id, Period, [RowKey.Create(key)], 1))],
                CancellationToken.None);
            var snapshot = counter.Tally.Snapshot();
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            return snapshot;
        }

        var three = await Measure(world.Instances.Take(3), "RATCHET_3");
        var twelve = await Measure(world.Instances, "RATCHET_12");

        // ⚠ Лічильник бачить команди EF; діапазон SEQUENCE іде окремим
        // з'єднанням BulkCellLoader — один на пакет за побудовою.
        Assert.True(three.Total == twelve.Total, $"CreateRowsBatchAsync: 3 → {three.Format()}; 12 → {twelve.Format()}");
        Assert.Equal(1, twelve.Total);
    }

    private RowStore Store(EcrDbContext db)
        => new(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));

    private sealed record World(TestDocumentBuilder Builder, IReadOnlyList<long> Instances);

    private async Task<World> ArrangeAsync(IReadOnlyList<int> tablesPerSheet)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2, ct: CancellationToken.None);
        var extra = await MultiTableDocument.AddTablesAsync(
            builder, doc, tablesPerSheet, rowCount: 2, ct: CancellationToken.None);

        return new World(builder, [doc.TableInstanceId, .. extra.Select(t => t.TableInstanceId)]);
    }

    /// <summary>Нові рядки: 2, 1, 3 ключі, порожній набір і ще 1 — різні порядкові номери.</summary>
    private static List<RowCreationBatch> Requests(World world)
        =>
        [
            new(world.Instances[0], Period, [RowKey.Create("N1"), RowKey.Create("N2")], 10),
            new(world.Instances[1], Period, [RowKey.Create("N1")], 11),
            new(world.Instances[2], Period, [RowKey.Create("N3"), RowKey.Create("N1"), RowKey.Create("N2")], 12),
            new(world.Instances[3], Period, [], 13),
            new(world.Instances[4], Period, [RowKey.Create("N9")], 14),
        ];

    /// <summary>
    /// Рядки світу в індексах екземплярів; нові рядки звірено з повернутими
    /// <c>Id</c> (ключ ↔ ідентифікатор).
    /// </summary>
    private async Task<List<string>> StateAsync(World world, IReadOnlyList<IReadOnlyList<long>> returned)
    {
        var requests = Requests(world);
        var expectedKeys = new Dictionary<long, (long Instance, string Key)>();
        for (var i = 0; i < requests.Count; i++)
        {
            for (var k = 0; k < requests[i].RowKeys.Count; k++)
            {
                expectedKeys[returned[i][k]] = (requests[i].TableInstanceId, requests[i].RowKeys[k].Value);
            }
        }

        var lines = new List<string>();
        var seen = 0;
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT TableInstanceId, Id, RowKey, Ordinal, ModifiedAt, IsDeleted, IsOrphaned FROM doc.TableRow " +
            $"WHERE PeriodKey = {PeriodKeyValue} AND TableInstanceId IN " +
            $"({string.Join(",", world.Instances.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var instance = reader.GetInt64(0);
            var id = reader.GetInt64(1);
            var key = reader.GetString(2);
            if (expectedKeys.TryGetValue(id, out var expected))
            {
                Assert.Equal((instance, key), expected);
                seen++;
            }
            else
            {
                key = "fixture";
            }

            var index = world.Instances.ToList().IndexOf(instance);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{index}|{key}|{reader.GetInt32(3)}|{reader.GetDateTime(4):O}|{reader.GetBoolean(5)}|{reader.GetBoolean(6)}"));
        }

        Assert.Equal(expectedKeys.Count, seen);
        return [.. lines.Order(StringComparer.Ordinal)];
    }

    private async Task<int> CountAsync(World world)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {PeriodKeyValue} AND TableInstanceId IN " +
            $"({string.Join(",", world.Instances.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
