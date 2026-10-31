// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncAnnualTests.D465.cs
using System.Data;
using System.Xml.Linq;
using Ecr.Application.Integration.RawData;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-465: значення атрибутів подій за останнім прочитанням (<c>ext.SourceEventAttributeValue</c>) на РІЧНОМУ періоді —
/// синк року пише лише змінене, перегляд показує розбіжність «PI каже / у комірці» з причиною, повна звірка знімає
/// значення разом із видаленим рядком, а журнал видалень читається за вікном. Плюс сталі звернення запису й seek-плани
/// читань перегляду.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — точковою правкою):
/// <list type="bullet">
/// <item>у <c>SourceEventAttributeValueWriter.MergeSql</c> прибрати <c>AND t.ValueHash &lt;&gt; s.h</c> — червоніє
/// «незмінне не переписується» (RunId другого прогону);</item>
/// <item>у <c>JournalAndDeleteAsync</c> прибрати <c>DELETE FROM ext.SourceEventAttributeValue</c> — значення видаленої
/// події лишаються сиротами;</item>
/// <item>у <c>PiEventComparison.LinkReason</c> прибрати гілку поданого аркуша — розбіжність стає <c>NotUpdated</c>.</item>
/// </list>
/// </remarks>
public sealed partial class SourceEventSyncAnnualTests
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Річний_синк_пише_лише_змінене_перегляд_показує_розбіжність_а_видалення_знімає_значення()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("FEB", Feb, 1m), Ev("MAY", May, 1m));
        await RunAsync(stand, source);

        var first = await StoredValuesAsync(stand);
        Assert.Equal(2, first.Count);
        Assert.All(first, v => Assert.Equal("Volume", v.Attribute));

        // Повторне прочитання року без змін у PI — жодного запису (RunId/ChangedAt першого прогону).
        await RunAsync(stand, source);
        Assert.Equal(first, await StoredValuesAsync(stand));

        // Аркуш подано; у PI змінився об'єм травневої події — значення прочитано (лише воно), а комірка лишилась.
        await SubmitSheetAsync(stand);
        source.Result = new SourceEventResult([Ev("FEB", Feb, 1m), Ev("MAY", May, 3m)], false, null);
        await RunAsync(stand, source);

        var second = await StoredValuesAsync(stand);
        var feb = (await LinksAsync(stand)).Single(l => l.SourceEventId == "FEB");
        var may = (await LinksAsync(stand)).Single(l => l.SourceEventId == "MAY");
        Assert.Equal(first.Single(v => v.LinkId == feb.Id), second.Single(v => v.LinkId == feb.Id));
        Assert.NotEqual(first.Single(v => v.LinkId == may.Id).RunId, second.Single(v => v.LinkId == may.Id).RunId);
        Assert.Equal(3m, second.Single(v => v.LinkId == may.Id).Numeric);
        Assert.Equal(1m, await VolumeAsync(stand, "EF-MAY"));

        var view = await ViewAsync(stand);
        var mayVolume = view["MAY"].Single(a => a.Attribute == "Volume");
        Assert.Equal(nameof(PiValueComparison.NotWritten), mayVolume.Comparison);
        Assert.Equal(nameof(PiValueReason.SheetLocked), mayVolume.Reason);
        Assert.Equal("3", mayVolume.ExpectedValue);
        Assert.Equal("1", mayVolume.CellValue);
        Assert.Equal(nameof(PiValueComparison.Match), view["FEB"].Single(a => a.Attribute == "Volume").Comparison);
        Assert.Equal(nameof(PiValueComparison.Match), view["FEB"].Single(a => a.Attribute == SourceEventMap.StartAttribute).Comparison);

        // Повернення аркуша; у PI лютневої події більше немає — рядок видалено, значення зв'язку зняті, журнал видалень
        // читається за вікном, а травнева комірка тепер збігається з PI.
        await ExecuteAsync($"DELETE FROM wf.ApprovalState WHERE DocumentId = {stand.DocumentId}");
        source.Result = new SourceEventResult([Ev("MAY", May, 3m)], false, null);
        await RunAsync(stand, source);

        Assert.Equal(["EF-MAY"], await RowKeysAsync(stand));
        Assert.DoesNotContain(await StoredValuesAsync(stand), v => v.LinkId == feb.Id);
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventAttributeValue WHERE LinkId = {feb.Id}"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(nameof(PiValueComparison.Match), (await ViewAsync(stand))["MAY"].Single(a => a.Attribute == "Volume").Comparison);

        await using (var db = sql.CreateContext())
        {
            var store = new SourceEventViewStore(db, new UnitCatalog(db));
            var removed = await store.RemovalsAsync(stand.EntityId, YearStartUtc, Now.AddDays(1), 10, CancellationToken.None);
            var row = Assert.Single(removed);
            Assert.Equal("FEB", row.EventId);
            Assert.Equal("EF-FEB", row.RowKey);
            Assert.Equal(stand.MapId, row.MapId);
            Assert.Equal(202601, row.PeriodKey);
        }
    }

    /// <summary>
    /// Запис значень — сталі звернення: одна команда на сторінку зв'язків (500), незалежно від кількості атрибутів;
    /// повторний запис тих самих значень нічого не змінює, зміна одного — один рядок.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Запис_значень_одна_команда_на_сторінку_і_лише_змінене()
    {
        var baseId = 9_100_000_000L + (Random.Shared.NextInt64(1, 1_000_000) * 10_000);
        try
        {
            var few = Readings(baseId, 10, 1m);
            var many = Readings(baseId + 1_000, 1_200, 1m);

            var (fewCommands, fewResult) = await CountCommandsAsync(few);
            var (manyCommands, manyResult) = await CountCommandsAsync(many);

            Assert.Equal(1, fewCommands);
            Assert.Equal(3, manyCommands);
            Assert.Equal(fewResult.Statements, fewCommands);
            Assert.Equal(manyResult.Statements, manyCommands);
            Assert.Equal(10 * 3, fewResult.Changed);

            // Те саме ще раз — нуль змін; одна подія з іншим значенням — рівно один рядок.
            Assert.Equal(0, (await CountCommandsAsync(many)).Result.Changed);
            var changed = many.Select((r, i) => i == 7 ? Readings(r.LinkId, 1, 2m)[0] : r).ToList();
            Assert.Equal(1, (await CountCommandsAsync(changed)).Result.Changed);

            // Зняття зв'язків — та сама команда незалежно від їхньої кількості.
            var (dropCommands, dropResult) = await CountCommandsAsync([], [.. few.Select(r => r.LinkId)]);
            Assert.Equal(1, dropCommands);
            Assert.Equal(30, dropResult.Changed);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM ext.SourceEventAttributeValue WHERE LinkId >= {baseId} AND LinkId < {baseId + 10_000}");
        }

        static List<SourceEventReading> Readings(long from, int count, decimal volume)
            => [.. Enumerable.Range(0, count).Select(i => new SourceEventReading(
                from + i,
                new SourceEvent(
                    $"E{from + i}", "FlareEvent", "Flaring", Feb, Feb.AddHours(1), Feb, null, null,
                    [
                        new SourceEventAttribute("Volume", SourceEventAttributeScope.Event, volume, null, "Sm3"),
                        new SourceEventAttribute("Category", SourceEventAttributeScope.Event, null, "Sweet", null),
                        new SourceEventAttribute("Unit", SourceEventAttributeScope.PrimaryElement, null, "U-1", null),
                    ])))];
    }

    public static TheoryData<string, string, string> ViewQueries => new()
    {
        { nameof(SourceEventViewStore.LinksSql), SourceEventViewStore.LinksSql, "[SourceEventLink]" },
        { nameof(SourceEventViewStore.CountSql), SourceEventViewStore.CountSql, "[SourceEventLink]" },
        { nameof(SourceEventViewStore.ValuesSql), SourceEventViewStore.ValuesSql, "[SourceEventAttributeValue]" },
        { nameof(SourceEventAttributeValueWriter.MergeSql), SourceEventAttributeValueWriter.MergeSql, "[SourceEventAttributeValue]" },
        { nameof(SourceEventViewStore.RemovalsSql), SourceEventViewStore.RemovalsSql, "[StructureChange]" },
    };

    /// <summary>
    /// Читання перегляду й запис значень — seek, а не скан (бойові тексти): зв'язки — <c>IX_SEL_MapStart</c>, значення —
    /// префікс ключа, журнал видалень — кластерний ключ за часом. Контроль — зв'язки без предиката мапінгу — скан.
    /// </summary>
    [Theory]
    [MemberData(nameof(ViewQueries))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Читання_подій_перегляду_seekом_а_не_сканом(string name, string text, string table)
    {
        await using var stand = await ArrangeAsync();
        await RunAsync(stand, new FakeEventSource(Ev("FEB", Feb, 1m), Ev("MAY", May, 1m)));
        var noise = await NoiseAsync(stand);

        var ops = await PlanAsync(text, stand, table);
        var control = await PlanAsync(
            "SELECT COUNT_BIG(*) FROM ext.SourceEventLink AS l WHERE l.StartUtc >= @from AND l.EventName LIKE N'%x%';",
            stand,
            "[SourceEventLink]");

        Assert.True(noise > 0);
        Assert.Contains(control, op => op.Contains("Scan", StringComparison.Ordinal));
        Assert.NotEmpty(ops);
        Assert.True(
            !ops.Any(op => op.Contains("Scan", StringComparison.Ordinal)),
            $"{name}: {string.Join(", ", ops)}");
    }

    // ── D-465: допоміжне ─────────────────────────────────────────────────────

    private sealed record StoredValue(long LinkId, string Attribute, decimal? Numeric, string RunId, DateTime ChangedAt);

    private async Task<List<StoredValue>> StoredValuesAsync(Stand stand)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.LinkId, v.Attribute, v.ValueNumeric, v.RunId, v.ChangedAt
              FROM ext.SourceEventAttributeValue AS v
              JOIN ext.SourceEventLink AS l ON l.Id = v.LinkId
             WHERE l.SourceEventMapId = @map
             ORDER BY v.LinkId, v.Attribute;
            """;
        command.Parameters.Add("@map", SqlDbType.Int).Value = stand.MapId;
        var result = new List<StoredValue>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new StoredValue(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.GetString(3), reader.GetDateTime(4)));
        }

        return result;
    }

    /// <summary>Перегляд мапінгу стенда — тим самим сховищем і порівнянням, що обробник; документ видно.</summary>
    private async Task<Dictionary<string, IReadOnlyList<PiEventAttributeView>>> ViewAsync(Stand stand)
    {
        await using var scope = stand.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
        var store = new SourceEventViewStore(db, scope.ServiceProvider.GetRequiredService<IUnitCatalog>());

        var map = (await store.MapAsync(stand.MapId, CancellationToken.None))!;
        var context = await store.ContextAsync(map, CancellationToken.None);
        var links = await store.LinksAsync(
            map.MapId,
            new PiEventFilter(YearStartUtc, Now.AddDays(1), [], null, null),
            null,
            100,
            CancellationToken.None);
        var values = await store.ValuesAsync([.. links.Select(l => l.Id)], CancellationToken.None);
        var cells = await store.CellsAsync(
            [.. links.Where(l => l.RowKey is not null).Select(l => new PiRowAddress(l.PeriodKey!.Value, l.TableInstanceId!.Value, l.RowKey!, stand.DocumentId))],
            [.. context.Fields.Select(f => f.ColumnDefId)],
            CancellationToken.None);

        return links.ToDictionary(
            l => l.SourceEventId,
            l => PiEventComparison.Compare(
                l,
                context,
                Atyrau,
                [.. values.Where(v => v.LinkId == l.Id)],
                column => cells.FirstOrDefault(c => c.RowKey == l.RowKey && c.ColumnDefId == column),
                _ => true));
    }

    private async Task<(int Commands, SourceEventAttributeWriteResult Result)> CountCommandsAsync(
        IReadOnlyList<SourceEventReading> readings, IReadOnlyCollection<long>? dropped = null)
    {
        await using var db = sql.CreateContext();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        connection.StatisticsEnabled = true;
        connection.ResetStatistics();

        var result = await SourceEventAttributeValueWriter.WriteAsync(
            db, readings, dropped ?? [], Guid.NewGuid().ToString("N"), Now, CancellationToken.None);

        var stats = connection.RetrieveStatistics();
        return ((int)(long)stats["ServerRoundtrips"]!, result);
    }

    // Шум кладеться ОДИН раз на базу колекції: кожен випадок теорії інакше додавав би свої тисячі рядків, а стеля
    // файлів тестової бази (TestDatabaseSizeTests) спільна на колекцію.
    private static readonly SemaphoreSlim NoiseGate = new(1, 1);
    private static string? _noiseDatabase;

    /// <summary>Шум для планів (раз на базу): 2 000 зв'язків чужого мапінгу й 4 000 значень.</summary>
    private async Task<int> NoiseAsync(Stand stand)
    {
        await NoiseGate.WaitAsync();
        try
        {
            if (_noiseDatabase != sql.DatabaseName)
            {
                await InsertNoiseAsync(stand);
                _noiseDatabase = sql.DatabaseName;
            }

            return 2000;
        }
        finally
        {
            NoiseGate.Release();
        }
    }

    private async Task InsertNoiseAsync(Stand stand)
    {
        int noiseMapId;
        await using (var db = sql.CreateContext())
        {
            var other = new SourceEntity(
                await db.SourceEntities.Where(e => e.Id == stand.EntityId).Select(e => e.DataSourceId).SingleAsync(),
                $"Noise{Guid.NewGuid():N}"[..20],
                RegistrySourceKind.External);
            other.Deactivate();
            db.SourceEntities.Add(other);
            await db.SaveChangesAsync();

            var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == stand.TableDefId);
            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == stand.TableDefId && c.DataType == CellDataType.Date).ToListAsync();
            var map = SourceEventMap.Create(
                other.Id,
                stand.DocumentId,
                table,
                [new(columns[0], SourceEventMap.StartAttribute), new(columns[1], SourceEventMap.EndAttribute)],
                SourceEventVolumeMode.None);
            map.Deactivate();
            db.SourceEventMaps.Add(map);
            await db.SaveChangesAsync();
            noiseMapId = map.Id;
        }

        await ExecuteAsync($"""
            INSERT INTO ext.SourceEventLink (SourceEventMapId, SourceEventId, EventName, StartUtc, [Status], FirstSeenAt, LastSeenAt, LastSyncAt)
            SELECT {noiseMapId}, CONCAT(N'N', t.n), CONCAT(N'Noise ', t.n), DATEADD(MINUTE, t.n * 90, '2026-01-01'), N'Open',
                   '2026-10-01', '2026-10-01', '2026-10-01'
              FROM (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS t;
            INSERT INTO ext.SourceEventAttributeValue (LinkId, Scope, Attribute, ValueNumeric, ValueText, IsTruncated, SourceUnit, ValueHash, SourceModifiedUtc, ChangedAt, RunId)
            SELECT l.Id, 0, a.name, 1, NULL, 0, NULL, HASHBYTES('SHA2_256', a.name), NULL, '2026-10-01', 'noise'
              FROM ext.SourceEventLink AS l
             CROSS JOIN (VALUES (N'A'), (N'B')) AS a(name)
             WHERE l.SourceEventMapId = {noiseMapId};
            UPDATE STATISTICS ext.SourceEventLink; UPDATE STATISTICS ext.SourceEventAttributeValue;
            CHECKPOINT;
            """);
    }

    private async Task<List<string>> PlanAsync(string text, Stand stand, string table)
    {
        var linkIds = (await LinksAsync(stand)).Select(l => l.Id).ToList();

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + text + "\nSET STATISTICS XML OFF;";
        command.CommandTimeout = 120;
        command.Parameters.Add("@take", SqlDbType.Int).Value = 51;
        command.Parameters.Add("@map", SqlDbType.Int).Value = stand.MapId;
        command.Parameters.Add("@from", SqlDbType.DateTime2).Value = YearStartUtc;
        command.Parameters.Add("@to", SqlDbType.DateTime2).Value = Now.AddDays(1);
        command.Parameters.Add("@upper", SqlDbType.DateTime2).Value = Now.AddDays(1);
        command.Parameters.Add("@afterId", SqlDbType.BigInt).Value = long.MaxValue;
        command.Parameters.Add("@statuses", SqlDbType.NVarChar, -1).Value = "[\"Synced\",\"Open\"]";
        command.Parameters.Add("@element", SqlDbType.NVarChar, 210).Value = DBNull.Value;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 420).Value = "%Flar%";
        command.Parameters.Add("@links", SqlDbType.NVarChar, -1).Value = System.Text.Json.JsonSerializer.Serialize(linkIds);
        command.Parameters.Add("@values", SqlDbType.NVarChar, -1).Value = "[]";
        command.Parameters.Add("@run", SqlDbType.VarChar, 32).Value = "plan";
        command.Parameters.Add("@at", SqlDbType.DateTime2).Value = Now;
        command.Parameters.Add("@entity", SqlDbType.NVarChar, 32).Value = stand.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                var isPlan = reader.FieldCount == 1
                             && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal);
                while (await reader.ReadAsync())
                {
                    if (isPlan)
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        return plans
            .SelectMany(p => XDocument.Parse(p).Descendants(Showplan + "RelOp"))
            .Where(op => op.Elements().Any(e => e.Elements(Showplan + "Object")
                .Any(o => (string?)o.Attribute("Table") == table)))
            .Select(op => (string)op.Attribute("PhysicalOp")!)
            .ToList();
    }
}
