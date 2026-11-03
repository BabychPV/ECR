// tests/Ecr.Infrastructure.Tests/Jobs/SheetCopyIntegrationWritersTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// RS (Q5): інтеграція — збір і матеріалізація, події джерел, рядки з дозволу — пише ЛИШЕ в оригінал аркуша; копія —
/// порожня форма, яку заповнює людина.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (локально): прибрати <c>OriginalOnly()</c> у <c>MaterializationTargets.FindAsync</c> — червоніє
/// <see cref="Адресати_збору_не_містять_екземпляра_копії"/>; у <c>MaterializeCollectedDataJob</c> — червоніє
/// <see cref="Задача_матеріалізації_на_копію_нічого_не_пише"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetCopyIntegrationWritersTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Адресати_збору_не_містять_екземпляра_копії()
    {
        var (chain, copyInstance, entityId, db) = await ArrangeAsync();
        await using var _ = db;

        var targets = await MaterializationTargets.FindAsync(db, entityId, null, null, null, null, 100, CancellationToken.None);

        var target = Assert.Single(targets);
        Assert.Equal(chain.TableInstanceId, target.TableInstanceId);
        Assert.DoesNotContain(copyInstance, targets.Select(t => t.TableInstanceId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Задача_матеріалізації_на_копію_нічого_не_пише()
    {
        var (chain, copyInstance, entityId, db) = await ArrangeAsync();
        await using var _ = db;

        var patcher = Substitute.For<ICellPatcher>();
        var job = new MaterializeCollectedDataJob(
            db, patcher, Substitute.For<ICoverageJournal>(), new IntegrationActor(db, new Ecr.Application.Common.JobActorScope()));

        var onCopy = Substitute.For<IJobProgress>();
        await job.ExecuteAsync(MaterializeFor(entityId, chain, copyInstance), onCopy, CancellationToken.None);

        Assert.Empty(patcher.ReceivedCalls());
        await onCopy.Received(1).ReportKeyAsync(100, "jobs.materializeNoMappings", Arg.Any<CancellationToken>());

        // Контроль: та сама задача на оригінал мапінг знаходить.
        var onOriginal = Substitute.For<IJobProgress>();
        await job.ExecuteAsync(MaterializeFor(entityId, chain, chain.TableInstanceId), onOriginal, CancellationToken.None);
        await onOriginal.DidNotReceive().ReportKeyAsync(100, "jobs.materializeNoMappings", Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Запис_інтеграції_в_екземпляр_копії_відхиляється()
    {
        var (chain, copyInstance, _, db) = await ArrangeAsync();
        await using var __ = db;

        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
        var patcher = new IntegrationCellPatcher(db, rows, new NormalizedCellStore(db), null!, null!, null!);
        var rowKey = await db.TableRows.Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKeyValue).SingleAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => patcher.ApplyIntegrationAsync(
            chain.DocumentId, copyInstance, chain.PeriodKey,
            [new IntegrationCellValue(rowKey, chain.ColumnDefIds[1], 5m)], CancellationToken.None));
        Assert.Contains("копії аркуша", error.Message, StringComparison.Ordinal);
    }

    private static MaterializeTask MaterializeFor(int sourceEntityId, TestDocument chain, long instanceId)
        => new(
            sourceEntityId, chain.ProjectId, chain.DocumentId, instanceId, chain.PeriodKey.Value,
            new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// Ланцюг із відкритим періодом, копією аркуша і її екземпляром; сутність джерела з матеріалізованим мапінгом у
    /// числову колонку таблиці.
    /// </summary>
    private async Task<(TestDocument Chain, long CopyInstance, int EntityId, EcrDbContext Db)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await builder.OpenPeriodAsync(chain);
        var copyId = await SheetCopyInstanceTests.AddCopyAsync(builder, chain);

        var db = builder.CreateContext();
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
        await rows.EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, CancellationToken.None);
        var copyInstance = await db.TableInstances
            .Where(t => t.PeriodKeyValue == chain.PeriodKey.Value && t.SheetCopyId == copyId)
            .Select(t => t.Id)
            .SingleAsync();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var source = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("RS copies"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        await db.SaveChangesAsync();

        // Неактивна навмисно (як у MaterializeMappingScopeTests): інакше SourcesHealthCheck жовтіє.
        var entity = new SourceEntity(source.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var rowKey = await db.TableRows.Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKeyValue).SingleAsync();
        var map = EntityFieldMap.ToColumn(entity.Id, $"F_{tag}", chain.ColumnDefIds[1]);
        map.SetMaterialization(rowKey, AggregationKind.Sum);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync();

        return (chain, copyInstance, entity.Id, db);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
