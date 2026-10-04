using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L10-06 (аудит 2026-10-03, 1F2): видалення чернетки
/// (<see cref="DocumentDeletionStore.DeleteAsync"/>) не падає на FK мапи подій
/// джерела і не лишає сиріт <c>ext.RowWindowValue</c> / <c>calc.CalculationRun</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class DocumentDeletionStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_з_мапою_подій_джерела_дає_409_і_лишається_цілою()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var entityId = await NewSourceEntityAsync(builder, "SEM");

        // ⚠ Мапа — сирим рядком: предмет тесту лише те, що вона ПОСИЛАЄТЬСЯ на
        // документ (FK_SEM_Document), а не правила її складу (SourceEventMap.Create).
        await using (var seed = builder.CreateContext())
        {
            await seed.Database.ExecuteSqlAsync(
                $"INSERT INTO ext.SourceEventMap (SourceEntityId, DocumentId, TableDefId, VolumeMode, IsActive) VALUES ({entityId}, {doc.DocumentId}, {doc.TableDefId}, 0, 1)");
        }

        await using (var db = builder.CreateContext())
        {
            var thrown = await Assert.ThrowsAsync<DomainException>(
                () => new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None));

            Assert.Equal("ECR-DOC-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-DOC-0409.deleteHasEventMap", thrown.Details!["messageKey"]);
        }

        await using var check = builder.CreateContext();
        Assert.True(await check.Documents.AnyAsync(d => d.Id == doc.DocumentId));
        Assert.True(await check.TableInstances.AnyAsync(i => i.DocumentId == doc.DocumentId));
        Assert.True(await check.TableRows.AnyAsync(r => r.TableInstanceId == doc.TableInstanceId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_прибирає_значення_вікон_рядків_і_прогони_документа()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1);
        var entityId = await NewSourceEntityAsync(builder, "RWV");
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        long documentRunId;
        long projectRunId;
        await using (var db = builder.CreateContext())
        {
            var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            var start = new ColumnDef(doc.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
            var end = new ColumnDef(doc.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
            db.ColumnDefs.AddRange(start, end);
            await db.SaveChangesAsync();

            var target = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[1]);
            var map = RowWindowMap.Create(target, start, end, null, RowWindowSummaryKind.Total, isStep: true, unitId);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();

            var rowKey = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync();

            db.RowWindowValues.Add(new RowWindowValue(
                doc.PeriodKey.Value, doc.TableInstanceId, rowKey, target.Id, map.Id, entityId, "Flare HP|Flow",
                At, At.AddSeconds(900), RowWindowSummaryKind.Total, unitId, At.AddHours(1)));

            var documentRun = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, At, doc.DocumentId);
            var projectRun = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, At);
            db.CalculationRuns.AddRange(documentRun, projectRun);
            await db.SaveChangesAsync();

            documentRunId = documentRun.Id;
            projectRunId = projectRun.Id;
        }

        await using (var db = builder.CreateContext())
        {
            await new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None);
        }

        await using var check = builder.CreateContext();
        Assert.False(await check.Documents.AnyAsync(d => d.Id == doc.DocumentId));
        Assert.False(await check.RowWindowValues.AnyAsync(v => v.TableInstanceId == doc.TableInstanceId));
        Assert.False(await check.CalculationRuns.AnyAsync(r => r.Id == documentRunId));

        // Прогін усього проєкту — не цього документа: лишається.
        Assert.True(await check.CalculationRuns.AnyAsync(r => r.Id == projectRunId));
    }

    private static async Task<int> NewSourceEntityAsync(TestDocumentBuilder builder, string prefix)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = builder.CreateContext();

        var dataSource = new DataSource(
            EcrCode.Create($"{prefix}SRC{tag}"), Name("L10-06"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"{prefix}Ent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        return entity.Id;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
