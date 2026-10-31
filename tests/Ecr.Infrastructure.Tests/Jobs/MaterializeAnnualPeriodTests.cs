// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeAnnualPeriodTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-462: збір PI на РІЧНОМУ періоді з місяцями в колонках. Колонка місяця m згортає лише точки місяця m, не-місячна
/// колонка — увесь рік; рік погодинних точок згортається обмеженою кількістю запитів (не залежить від кількості
/// точок); усі точки місяця видалено в джерелі — комірка місяця очищується, решта не чіпаються.
/// </summary>
[Collection("SqlServer")]
public sealed class MaterializeAnnualPeriodTests(SqlServerFixture sql)
{
    private static readonly DateTime AfterYear = new(2027, 1, 15, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Північ 1 січня 2026 у поясі проєкту (Asia/Atyrau, +05:00).</summary>
    private static readonly DateTime YearStartUtc = new(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// ⛔ МУТАЦІЯ: <c>RangeOf</c> повертає період цілком для колонок місяців — кожна M{m} отримує 8 760 (суму року).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Рік_погодинних_точок_місяці_згортаються_по_місяцях_рік_цілком_запитів_обмежено()
    {
        var stand = await ArrangeAsync();
        await using var _ = stand;

        foreach (var field in stand.Fields.Values)
        {
            await PointsAsync(stand.Db, stand.EntityId, field, Enumerable.Range(0, 8760).Select(h => YearStartUtc.AddHours(h)));
        }

        var counter = new DbCommandCounter();
        var written = await RunAsync(stand, counter);

        // Кожна колонка місяця — години СВОГО місяця (точка = 1), хоч у поля точки за весь рік; рік — 8 760.
        foreach (var month in Months)
        {
            Assert.Equal(DateTime.DaysInMonth(2026, month) * 24m, written[stand.MonthColumns[month - 1]]);
        }

        Assert.Equal(8760m, written[stand.YearColumn]);

        // ⚠ Запити не залежать від кількості точок: мапінги, період, колонки місяців і по одному читанню точок на
        // пару «поле + діапазон» (4), плюс кілька службових. Рік хвилинних точок дав би ту саму кількість.
        Assert.InRange(counter.Tally.Snapshot().Total, 1, 30);
    }

    /// <summary>Усі точки березня видалено в джерелі — M3 очищено (null), інші місяці й рік — числа.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Видалений_місяць_річного_періоду_очищує_лише_свою_колонку()
    {
        var stand = await ArrangeAsync();
        await using var _ = stand;

        // Березень видалено в джерелі в усіх полях: точки були, видалення зниклих (D-462) їх зняло й лишило
        // позначку (✎ аудит 31.10, G4 — без неї порожній місяць не очищується).
        var marchFrom = new DateTime(2026, 2, 28, 19, 0, 0, DateTimeKind.Utc);
        var marchTo = new DateTime(2026, 3, 31, 19, 0, 0, DateTimeKind.Utc);
        var store = new CollectionStore(stand.Db, new TestClock(AfterYear));
        foreach (var field in stand.Fields.Values)
        {
            await PointsAsync(stand.Db, stand.EntityId, field, Enumerable.Range(0, 8760).Select(h => YearStartUtc.AddHours(h)));
            Assert.Equal(31 * 24, await store.DeleteMissingRawPointsAsync(stand.EntityId, field, marchFrom, marchTo, [], CancellationToken.None));
        }

        var written = await RunAsync(stand, new DbCommandCounter());

        Assert.Null(written[stand.MonthColumns[2]]);
        Assert.Equal(31 * 24m, written[stand.MonthColumns[0]]);
        Assert.Equal(8760m - (31 * 24m), written[stand.YearColumn]);
    }

    private async Task<Dictionary<int, decimal?>> RunAsync(Stand stand, DbCommandCounter counter)
    {
        var patcher = Substitute.For<ICellPatcher>();
        var written = new Dictionary<int, decimal?>();
        patcher
            .ApplyIntegrationAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyList<IntegrationCellValue>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cells = (IReadOnlyList<IntegrationCellValue>)call[3];
                foreach (var cell in cells)
                {
                    written[cell.ColumnDefId] = cell.Value;
                }

                return new IntegrationWriteResult(cells.Count, []);
            });

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        var job = new MaterializeCollectedDataJob(
            db, patcher, Substitute.For<ICoverageJournal>(),
            new IntegrationActor(db, new Ecr.Application.Common.JobActorScope()),
            recalculation: null, clock: new TestClock(AfterYear));

        counter.Tally.Reset();
        await job.ExecuteAsync(
            new MaterializeTask(
                stand.EntityId, stand.Chain.ProjectId, stand.Chain.DocumentId, stand.Chain.TableInstanceId,
                stand.Chain.PeriodKey.Value, YearStartUtc, YearStartUtc.AddYears(1)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        return written;
    }

    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            202601, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        var db = builder.CreateContext();

        // Річний період: 2026-01-01 … 2026-12-31, проєкт `Yearly`.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Period SET PeriodEnd = {new DateOnly(2026, 12, 31)} WHERE ProjectId = {chain.ProjectId} AND PeriodKey = {chain.PeriodKey.Value}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Project SET PeriodKind = {(byte)PeriodKind.Yearly} WHERE Id = {chain.ProjectId}");

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, AfterYear);
        await db.SaveChangesAsync();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "D-462 annual" }),
            ExternalTransport.PiSqlClient, "pi-sql.example", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        // ⚠ Неактивна: активна сутність без завершеного збору фарбує здоров'я джерел у спільній базі.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == chain.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();

        // ⚠ Поле — на ОДНУ комірку (UQ_EntityFieldMap: сутність + поле), тож кожна колонка — власний атрибут.
        var fields = new Dictionary<int, string>();
        foreach (var (column, name) in Months.Select(m => (chain.MonthColumnDefIds[m - 1], $"M{m}_{tag}"))
                     .Append((chain.ColumnDefIds[1], $"Year_{tag}")))
        {
            var map = EntityFieldMap.ToColumn(entity.Id, name, column);
            map.SetMaterialization(rowKey, AggregationKind.Sum);
            db.EntityFieldMaps.Add(map);
            fields[column] = name;
        }

        await db.SaveChangesAsync();

        return new Stand(db, chain, entity.Id, fields, chain.MonthColumnDefIds, chain.ColumnDefIds[1]);
    }

    private static async Task PointsAsync(EcrDbContext db, int entityId, string field, IEnumerable<DateTime> at)
    {
        var points = at.Select(t => new SourceDataPoint(field, t, 1m, null, null, "Good")).ToList();
        var store = new CollectionStore(db, new TestClock(AfterYear));
        var run = await store.StartRunAsync(entityId, YearStartUtc, YearStartUtc.AddYears(1), false, null, CancellationToken.None);
        await store.UpsertRawPointsAsync(run, entityId, points, CancellationToken.None);
    }

    /// <summary>Колонки місяців стенда: січень, березень, грудень.</summary>
    private static readonly int[] Months = [1, 3, 12];

    private sealed record Stand(
        EcrDbContext Db, TestDocument Chain, int EntityId, Dictionary<int, string> Fields, IReadOnlyList<int> MonthColumns, int YearColumn)
        : IAsyncDisposable
    {
        /// <summary>Сирі точки стенда прибираються: спільна база колекції не має роздуватися.</summary>
        public async ValueTask DisposeAsync()
        {
            await Db.RawDataPoints.Where(p => p.SourceEntityId == EntityId).ExecuteDeleteAsync();
            await Db.DisposeAsync();
        }
    }
}
