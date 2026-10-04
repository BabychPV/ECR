using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогони_документа_прибираються_з_відсічкою_партицій_трейсу()
    {
        // Рев'ю AN-37 P2-1: без предиката за PeriodKey видалення кроків прогону
        // документа — скан усіх партицій calc.CalculationStep у транзакції.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        long periodRunId;
        long yearRunId;
        await using (var db = builder.CreateContext())
        {
            // Прогін за період і річний (PeriodKey NULL) — кроки обох у періоді документа.
            var periodRun = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, At, doc.DocumentId);
            var yearRun = new CalculationRun(doc.ProjectId, null, null, At, doc.DocumentId);
            db.CalculationRuns.AddRange(periodRun, yearRun);
            await db.SaveChangesAsync();

            foreach (var (runId, code) in new[] { (periodRun.Id, "STEP_P"), (yearRun.Id, "STEP_Y") })
            {
                // Id кроку видає послідовність (ValueGeneratedNever) — як у CalculationResultStore.
                var step = new CalculationStep(runId, doc.PeriodKey.Value, 1, code);
                var id = (await db.Database
                    .SqlQuery<long>($"SELECT NEXT VALUE FOR calc.CalculationResultSeq AS Value")
                    .ToListAsync())[0];
                typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(step, id);
                db.CalculationSteps.Add(step);
            }

            await db.SaveChangesAsync();

            periodRunId = periodRun.Id;
            yearRunId = yearRun.Id;
        }

        var recorder = new StepDeleteRecorder();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(recorder)
            .Options))
        {
            await new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None);
        }

        await using (var check = builder.CreateContext())
        {
            Assert.False(await check.CalculationRuns.AnyAsync(r => r.Id == periodRunId || r.Id == yearRunId));
            Assert.False(await check.CalculationSteps.AnyAsync(s => s.CalculationRunId == periodRunId || s.CalculationRunId == yearRunId));
        }

        var delete = Assert.Single(recorder.Seen);

        // Той самий DELETE ще раз — під STATISTICS XML і з відкатом: скільки
        // партицій calc.CalculationStep він фактично торкнувся.
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        int fanout;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT fanout FROM sys.partition_functions WHERE name = N'pf_ByPeriodKey';";
            fanout = (int)(await count.ExecuteScalarAsync())!;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET STATISTICS XML ON;\n" + delete.Text + "\nSET STATISTICS XML OFF;";
        foreach (var (name, type, value) in delete.Parameters)
        {
            command.Parameters.Add(new SqlParameter(name, type) { Value = value });
        }

        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal))
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        await transaction.RollbackAsync();

        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var accessed = plans
            .SelectMany(p => System.Xml.Linq.XDocument.Parse(p).Descendants(ns + "RelOp"))
            .Where(op => op.Element(ns + "RunTimePartitionSummary") is not null
                         && op.Descendants(ns + "Object").Any(o => (string?)o.Attribute("Table") == "[CalculationStep]"))
            .Select(op => (int)op.Element(ns + "RunTimePartitionSummary")!.Element(ns + "PartitionsAccessed")!.Attribute("PartitionCount")!)
            .ToList();

        Assert.NotEmpty(accessed);
        Assert.All(accessed, n => Assert.True(n < fanout, $"прочитано {n} партицій calc.CalculationStep із {fanout}:\n{delete.Text}"));
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

/// <summary>Запам'ятовує DELETE кроків трейсу разом із параметрами — для повтору під планом.</summary>
internal sealed class StepDeleteRecorder : DbCommandInterceptor
{
    public List<(string Text, List<(string Name, System.Data.SqlDbType Type, object Value)> Parameters)> Seen { get; } = [];

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("DELETE", StringComparison.Ordinal)
            && command.CommandText.Contains("[CalculationStep]", StringComparison.Ordinal))
        {
            Seen.Add((command.CommandText, [.. command.Parameters.Cast<SqlParameter>()
                .Select(p => (p.ParameterName, p.SqlDbType, p.Value))]));
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
