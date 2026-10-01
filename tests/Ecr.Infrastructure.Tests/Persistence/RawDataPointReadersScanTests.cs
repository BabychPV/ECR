// tests/Ecr.Infrastructure.Tests/Persistence/RawDataPointReadersScanTests.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using NSubstitute;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// R2a: читачі <c>ext.RawDataPoint</c> не сканують таблицю — кількість логічних читань не росте
/// з кількістю ЧУЖИХ рядків.
/// </summary>
/// <remarks>
/// ⛔ Доказ — <c>SET sys.dm_exec_sessions.logical_reads</c> на реальній базі, а не читання коду: на порожній таблиці
/// скан і seek однакові, тому кожен тест спершу міряє читання, потім додає 40 000 чужих рядків і
/// міряє знову. Мутаційний доказ: повернути в <c>UnitStore</c> <c>RawDataPoints.AnyAsync(UnitId ==)</c>
/// або прибрати з EXISTS у <c>KeepClosedWithRawPointsAsync</c> зв'язок із <c>ext.EntityFieldMap</c> —
/// читання ростуть на сотні сторінок, обидва тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class RawDataPointReadersScanTests(SqlServerFixture sql)
{
    private const int ForeignRows = 40_000;
    private const int Slack = 4;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R2a")]
    public async Task Використання_одиниці_сирими_точками_не_читає_чужих_рядків()
    {
        var (entityId, runId, used, unused) = await ArrangeUnitsAsync();

        // Точка в одиниці `used` по змапленому шляху; мапінг називає `used` джерелом.
        Assert.True(await UsesRawAsync(used));
        Assert.False(await UsesRawAsync(unused));

        var usedBefore = await ReadsAsync(db => new UnitStore(db).FindUnitUsageAsync(used, 10, CancellationToken.None));
        var unusedBefore = await ReadsAsync(db => new UnitStore(db).FindUnitUsageAsync(unused, 10, CancellationToken.None));

        await InsertForeignRowsAsync(entityId, runId, "Foreign_" + _tag);

        // Розігрів: перший запит після масової вставки платить за синхронне оновлення статистики
        // (~2 тис. читань) — це не скан читача, і міряти його не можна.
        await ReadsAsync(db => new UnitStore(db).FindUnitUsageAsync(used, 10, CancellationToken.None));
        await ReadsAsync(db => new UnitStore(db).FindUnitUsageAsync(unused, 10, CancellationToken.None));

        var usedAfter = await ReadsAsync(db => new UnitStore(db).FindUnitUsageAsync(used, 10, CancellationToken.None));
        var unusedAfter = await ReadsAsync(db => new UnitStore(db).FindUnitUsageAsync(unused, 10, CancellationToken.None));

        // ⛔ Невикористана одиниця — саме той випадок, де AnyAsync(UnitId) читав ВСЮ таблицю.
        Assert.True(
            unusedAfter <= unusedBefore + Slack,
            $"Невикористана одиниця: читань RawDataPoint {unusedBefore} -> {unusedAfter} після {ForeignRows} чужих рядків.");
        Assert.True(
            usedAfter <= usedBefore + Slack,
            $"Використана одиниця: читань RawDataPoint {usedBefore} -> {usedAfter} після {ForeignRows} чужих рядків.");
        Assert.True(await UsesRawAsync(used));
        Assert.False(await UsesRawAsync(unused));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R2a")]
    public async Task Фільтр_закритих_періодів_читає_лише_змаплені_шляхи_сутності()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        int entityId;
        long runId;
        var field = $"Mapped_{_tag}";
        var at = new DateTime(chain.PeriodKey.Year, chain.PeriodKey.Sequence, 15, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = builder.CreateContext())
        {
            var dataSource = new DataSource(
                EcrCode.Create($"Src{_tag}"), Name("scan"), ExternalTransport.PiWebApi, "https://example.test", "secret");
            db.DataSources.Add(dataSource);
            await db.SaveChangesAsync(CancellationToken.None);

            var entity = new SourceEntity(dataSource.Id, $"Ent{_tag}", RegistrySourceKind.External);
            entity.Deactivate();
            db.SourceEntities.Add(entity);
            await db.SaveChangesAsync(CancellationToken.None);
            entityId = entity.Id;

            var rowKey = await db.TableRows
                .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
                .Select(r => r.RowKey)
                .SingleAsync();
            var map = EntityFieldMap.ToColumn(entityId, field, chain.ColumnDefIds[1]);
            map.SetMaterialization(rowKey.Value, AggregationKind.Sum);
            db.EntityFieldMaps.Add(map);
            await db.SaveChangesAsync(CancellationToken.None);

            var store = new CollectionStore(db, new TestClock(at));
            runId = await store.StartRunAsync(entityId, at, at.AddDays(1), false, null, CancellationToken.None);

            // Точка НЕзмапленого шляху в періоді: матеріалізувати нею нічого.
            await store.UpsertRawPointsAsync(
                runId, entityId, [new SourceDataPoint($"Unmapped_{_tag}", at, 1m, null, null, "Good")], CancellationToken.None);
        }

        await using (var db = builder.CreateContext())
        {
            var period = await db.Periods.SingleAsync(
                p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
            period.AdvanceTo(PeriodState.Grace, at);
            period.TransitionTo(PeriodState.Closed, at);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Фільтр закритих періодів — через публічний планувальник (тип цілей internal):
        // «залишився» = задача поставлена для сутності.
        async Task<(int Kept, long Reads)> RunAsync()
        {
            var jobs = Substitute.For<IBackgroundJobScheduler>();
            var reads = await ReadsAsync(db => new MaterializationScheduler(db, jobs)
                .EnqueueAfterTransitionAsync(chain.ProjectId, [chain.PeriodKey.Value], CancellationToken.None));
            var kept = jobs.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueAsync))
                .Select(c => (MaterializeTask)c.GetArguments()[0]!)
                .Count(t => t.SourceEntityId == entityId);
            return (kept, reads);
        }

        // Лише точка незмапленого шляху — закритий період задачі не отримує.
        Assert.Equal(0, (await RunAsync()).Kept);

        await using (var db = builder.CreateContext())
        {
            await new CollectionStore(db, new TestClock(at)).UpsertRawPointsAsync(
                runId, entityId, [new SourceDataPoint(field, at, 1m, null, null, "Good")], CancellationToken.None);
        }

        var before = await RunAsync();
        Assert.Equal(1, before.Kept);

        // Чужі рядки ТІЄЇ Ж сутності: старий EXISTS без шляху читав їх усі.
        await InsertForeignRowsAsync(entityId, runId, "Foreign_" + _tag);

        await RunAsync(); // розігрів: оновлення статистики після масової вставки
        var after = await RunAsync();
        Assert.Equal(1, after.Kept);
        Assert.True(
            after.Reads <= before.Reads + Slack,
            $"Фільтр: читань RawDataPoint {before.Reads} -> {after.Reads} після {ForeignRows} рядків чужого шляху.");
    }

    private async Task<(int EntityId, long RunId, int Used, int Unused)> ArrangeUnitsAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var dataSource = new DataSource(
            EcrCode.Create($"Src{_tag}"), Name("scan"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ent{_tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);

        var dimension = await db.Dimensions.OrderBy(d => d.Id).FirstAsync();
        var used = new Unit(EcrCode.Create($"UU{_tag}"), Name("uu"), Name("used"), dimension.Id, false, 1m, 0m);
        var unused = new Unit(EcrCode.Create($"UN{_tag}"), Name("un"), Name("unused"), dimension.Id, false, 1m, 0m);
        db.Units.AddRange(used, unused);
        await db.SaveChangesAsync(CancellationToken.None);

        var field = $"Mapped_{_tag}";
        var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
        map.SetUnits(used.Id, null);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync(CancellationToken.None);

        var at = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var store = new CollectionStore(db, new TestClock(at));
        var runId = await store.StartRunAsync(entity.Id, at, at.AddDays(1), false, null, CancellationToken.None);
        await store.UpsertRawPointsAsync(
            runId, entity.Id, [new SourceDataPoint(field, at, 1m, null, $"UU{_tag}", "Good")], CancellationToken.None);

        return (entity.Id, runId, used.Id, unused.Id);
    }

    private async Task<bool> UsesRawAsync(int unitId)
    {
        await using var db = sql.CreateContext();
        var usage = await new UnitStore(db).FindUnitUsageAsync(unitId, 50, CancellationToken.None);
        return usage.Items.Any(i => i.Kind == UsageKinds.Data && i.Label == "ext.RawData");
    }

    /// <summary>Логічні читання сесії за дію (<c>sys.dm_exec_sessions.logical_reads</c>).</summary>
    /// <remarks>
    /// Міряється сесія, а не одна таблиця: решта читань у дії стала (довідники), а зростання
    /// дає лише скан <c>ext.RawDataPoint</c>. DMV, а не <c>STATISTICS IO</c>: повідомлення
    /// InfoMessage від EF-запитів на цьому з'єднанні не приходять.
    /// </remarks>
    private async Task<long> ReadsAsync(Func<EcrDbContext, Task> action)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        async Task<long> SessionReadsAsync()
        {
            await using var q = connection.CreateCommand();
            q.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            return Convert.ToInt64(await q.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connection).Options);
        var before = await SessionReadsAsync();
        await action(db);
        return await SessionReadsAsync() - before;
    }
    private async Task InsertForeignRowsAsync(int entityId, long runId, string path)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            INSERT ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, UnitId, Quality, RetrievedAt, CollectionRunId)
            SELECT TOP (@n) @e, @p, DATEADD(MINUTE, ROW_NUMBER() OVER (ORDER BY (SELECT 1)), '2020-01-01'),
                   1, NULL, N'Good', SYSUTCDATETIME(), @r
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            """;
        command.Parameters.AddWithValue("@n", ForeignRows);
        command.Parameters.AddWithValue("@e", entityId);
        command.Parameters.AddWithValue("@p", path);
        command.Parameters.AddWithValue("@r", runId);
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
