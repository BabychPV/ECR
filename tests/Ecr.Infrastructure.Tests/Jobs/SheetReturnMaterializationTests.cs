// tests/Ecr.Infrastructure.Tests/Jobs/SheetReturnMaterializationTests.cs
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
/// D-462: повернутий (відхилений, відкликаний, перевідкритий) аркуш отримує матеріалізацію своїх екземплярів таблиць
/// з мапінгами збору на ВЕСЬ період (річний — увесь рік); аркуш без мапінгів — жодної задачі.
/// </summary>
[Collection("SqlServer")]
public sealed class SheetReturnMaterializationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Повернення_аркуша_з_мапінгом_ставить_матеріалізацію_на_весь_річний_період()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(202601, columnCount: 2, rowCount: 1);
        await using var db = builder.CreateContext();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Period SET PeriodEnd = {new DateOnly(2026, 12, 31)} WHERE ProjectId = {chain.ProjectId} AND PeriodKey = {chain.PeriodKey.Value}");
        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync();

        var rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == chain.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();
        await MapAsync(db, chain.ColumnDefIds[1], rowKey);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await new MaterializationScheduler(db, jobs).EnqueueAfterSheetReturnAsync(
            chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value, CancellationToken.None);

        var call = Assert.Single(jobs.ReceivedCalls());
        Assert.Equal(typeof(IMaterializeCollectedDataJob), call.GetMethodInfo().GetGenericArguments()[0]);
        var task = Assert.IsType<MaterializeTask>(call.GetArguments()[0]);
        Assert.Equal(chain.TableInstanceId, task.TableInstanceId);

        // Увесь рік у поясі проєкту (+05:00), а не вікно збору.
        Assert.Equal(new DateTime(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc), task.FromUtc);
        Assert.Equal(new DateTime(2026, 12, 31, 19, 0, 0, DateTimeKind.Utc), task.ToUtc);
    }

    /// <remarks>⛔ МУТАЦІЯ: прибрати відбір за документом/аркушем — задача йде й аркушу без мапінгів.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-462")]
    public async Task Аркуш_без_мапінгів_збору_задачі_не_отримує()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(202601, columnCount: 2, rowCount: 1);
        await using var db = builder.CreateContext();

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await new MaterializationScheduler(db, jobs).EnqueueAfterSheetReturnAsync(
            chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value, CancellationToken.None);

        Assert.Empty(jobs.ReceivedCalls());
    }

    private static async Task MapAsync(EcrDbContext db, int columnDefId, string rowKey)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "D-462 sheet return" }),
            ExternalTransport.PiSqlClient, "pi-sql.example", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var map = EntityFieldMap.ToColumn(entity.Id, $"Flow_{tag}", columnDefId);
        map.SetMaterialization(rowKey, AggregationKind.Sum);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync();
    }
}
