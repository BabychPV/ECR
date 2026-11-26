using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ 25.11: видалення проєкту з усіма даними (<see cref="ProjectPurgeStore"/>) — прибирає КОЖЕН рядок проєкту і не
/// чіпає жодного рядка іншого; факти архіву (B69) і залежних (B80) — з бази.
/// </summary>
/// <remarks>
/// ⛔ Перелік таблиць НЕ записаний у тесті: він читається з <c>sys.columns</c> — кожна таблиця схем doc/calc/wf/ext/cfg/itg
/// зі стовпцем <c>DocumentId</c> або <c>ProjectId</c> (крім історії темпоральних і журналу задач <c>itg.JobProgress</c>), плюс
/// <c>doc.TableRow</c>/<c>doc.CellValue</c> через екземпляр. Нова таблиця з посиланням на документ, яку видалення забуло,
/// червонить перший тест без його правки.
/// <para>
/// Мутаційні докази (вручну, RED → GREEN): (1) у <c>PurgeAsync</c> прибрати видалення <c>wf.ApprovalEvent</c> — перший тест
/// падає на FK (547); (2) прибрати видалення прогонів рівня проєкту — перший тест: <c>calc.CalculationRun</c> лишився;
/// (3) прибрати запит <c>arc.TableInstance</c> у <c>ReadFactsAsync</c> — червоніє <c>Архівний_період…</c>;
/// (4) у <c>SourceBindingDependantsAsync</c> пропустити рядок <c>d.ProjectId != projectId</c>… — червоніє
/// <c>Документ_джерело…</c> (власний документ стає «залежним»); прибрати весь пошук — теж червоніє.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectPurgeStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 2, 3, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повне_видалення_прибирає_всі_рядки_проєкту_і_не_чіпає_іншого()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 3, rowCount: 2);
        var b = await builder.BuildAsync(columnCount: 3, rowCount: 2);
        await PopulateAsync(builder, a);
        await PopulateAsync(builder, b);

        var beforeA = await RowCountsAsync(builder, a.ProjectId);
        var beforeB = await RowCountsAsync(builder, b.ProjectId);

        // Наповнення справді дійшло до таблиць, які видалення чернетки НЕ чіпає (історія погодження, прогони проєкту).
        foreach (var table in new[] { "doc.Document", "doc.CellValue", "doc.TableRow", "wf.ApprovalState", "wf.ApprovalEvent",
                     "calc.SubmissionSnapshot", "calc.CalculationRun", "calc.CalculationIssue", "doc.Period", "doc.Project" })
        {
            Assert.True(beforeA.GetValueOrDefault(table) > 0, $"{table}: наповнення не дійшло");
        }

        ProjectPurgeFacts facts;
        await using (var db = builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var store = Store(db, EmptyGraph());
            facts = (await store.ReadFactsAsync(a.ProjectId, lockForDelete: true, CancellationToken.None))!;
            var result = await store.PurgeAsync(a.ProjectId, [.. facts.Documents.Select(d => d.DocumentId)], CancellationToken.None);
            await tx.CommitAsync();

            Assert.Equal(beforeA["doc.CellValue"], result.Cells);
        }

        var doc = Assert.Single(facts.Documents);
        Assert.Equal(a.DocumentId, doc.DocumentId);
        Assert.StartsWith("DOC", doc.BusinessKey, StringComparison.Ordinal);
        Assert.Equal(beforeA["doc.CellValue"], doc.Cells);
        Assert.Equal(1, facts.Counts.ApprovalEvents);
        Assert.Equal(1, facts.Counts.SubmissionSnapshots);
        Assert.Equal(2, facts.Counts.CalculationRuns);
        Assert.Equal([a.PeriodKey.Value], facts.DataMonths);
        Assert.Empty(facts.ArchivedPeriodKeys);
        Assert.Empty(facts.Dependants);
        Assert.Empty(facts.EventMaps);
        Assert.Empty(facts.Warnings);
        // ✎ R3 C2-f: затверджений місяць — у фактах (і далі в журналі): видаляється без повернення.
        Assert.Equal([a.PeriodKey.Value], facts.ApprovedMonths);

        var afterA = await RowCountsAsync(builder, a.ProjectId);
        var afterB = await RowCountsAsync(builder, b.ProjectId);

        Assert.All(afterA, pair => Assert.True(pair.Value == 0, $"{pair.Key}: лишилось {pair.Value} рядків видаленого проєкту"));
        Assert.Equal(beforeB, afterB);
    }

    /// <remarks>
    /// ✎ R3 C2-d: задачі документів проєкту скасовуються тією самою семантикою, що «Скасувати» в черзі; чужі — ні.
    /// Мутація: прибрати UPDATE itg.JobProgress у <c>PurgeCoreAsync</c> — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Задачі_документів_проєкту_скасовуються_а_чужі_ні()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var b = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var tag = Guid.NewGuid().ToString("N")[..10];
        string Id(string s) => $"purge-{tag}-{s}";

        await using (var db = builder.CreateContext())
        {
            foreach (var (id, state, doc) in new[] { (Id("aq"), "Queued", a.DocumentId), (Id("ar"), "Running", a.DocumentId), (Id("bq"), "Queued", b.DocumentId) })
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO itg.JobProgress (JobId, JobCode, [State], StartedAt, UpdatedAt, DocumentId)
                    VALUES ({id}, N'Recalculate', {state}, {At}, {At}, {doc})
                    """);
            }
        }

        ProjectPurgeResult result;
        await using (var db = builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var store = Store(db, EmptyGraph());
            var facts = (await store.ReadFactsAsync(a.ProjectId, lockForDelete: true, CancellationToken.None))!;
            result = await store.PurgeAsync(a.ProjectId, [.. facts.Documents.Select(d => d.DocumentId)], CancellationToken.None);
            await tx.CommitAsync();
        }

        Assert.Equal(2, result.CancelledJobs);
        await using var check = builder.CreateContext();
        var rows = await check.Database.SqlQuery<JobRow>($"""
            SELECT JobId, [State], CAST(CASE WHEN CancelRequestedAt IS NULL THEN 0 ELSE 1 END AS bit) AS CancelRequested
              FROM itg.JobProgress WHERE JobId LIKE {"purge-" + tag + "%"}
            """).ToListAsync();
        Assert.Equal("Cancelled", rows.Single(r => r.JobId == Id("aq")).State);
        Assert.True(rows.Single(r => r.JobId == Id("ar")).CancelRequested);
        Assert.Equal("Queued", rows.Single(r => r.JobId == Id("bq")).State);
    }

    /// <remarks>
    /// ✎ R3 C2-e: документ, створений у проєкті ПІСЛЯ читання фактів (його немає в переліку), — 409
    /// <c>purgeConcurrentChange</c>, а не 500; транзакція відкочується, проєкт цілий.
    /// Мутація: прибрати <c>catch (SqlException) when Number == 547</c> — червоніє (SqlException замість 409).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_створений_під_час_видалення_дає_409_і_нічого_не_видалено()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        await using (var db = builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var store = Store(db, EmptyGraph());
            var facts = (await store.ReadFactsAsync(a.ProjectId, lockForDelete: false, CancellationToken.None))!;

            // «Паралельний» користувач створив документ, якого немає у фактах (у тій самій транзакції — без очікування блокувань).
            db.Documents.Add(new Document(a.ProjectId, $"LATE{Guid.NewGuid().ToString("N")[..8]}", 1, At));
            await db.SaveChangesAsync();

            var error = await Assert.ThrowsAsync<Ecr.Application.Errors.ConcurrencyConflictException>(
                () => store.PurgeAsync(a.ProjectId, [.. facts.Documents.Select(d => d.DocumentId)], CancellationToken.None));
            Assert.Equal("ECR-PRD-0409", error.ErrorCode);
            Assert.Equal(Ecr.Application.Projects.PurgeProjectHandler.ConcurrentChangeKey, error.Details!["messageKey"]);
            await tx.RollbackAsync();
        }

        await using var check = builder.CreateContext();
        Assert.True(await check.Projects.AnyAsync(p => p.Id == a.ProjectId));
        Assert.True(await check.Documents.AnyAsync(d => d.Id == a.DocumentId));
    }

    /// <remarks>
    /// ✎ R3 C2-b: мапа подій на ВЛАСНОМУ документі — у <c>EventMaps</c>, не в «залежних інших проєктів».
    /// ✎ R3 C2-g: проєкт із кодом золотих тестів HSE400 — попередження <c>hse400Tools</c> (не блок).
    /// Мутації: повернути мапи в <c>Dependants</c> — червоніє; прибрати попередження — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Мапа_подій_власного_документа_окремо_від_залежних()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = builder.CreateContext();
        var source = new Ecr.Domain.Entities.External.DataSource(
            EcrCode.Create($"PSRC{tag}"), Name("purge"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        var entity = new Ecr.Domain.Entities.External.SourceEntity(source.Id, $"PEnt{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO ext.SourceEventMap (SourceEntityId, DocumentId, TableDefId, VolumeMode, IsActive) VALUES ({entity.Id}, {a.DocumentId}, {a.TableDefId}, 0, 1)");

        // ⚠ Попередження GOLD перевіряє PurgeProjectTests (ProjectPurgeWarnings.For): у спільній тестовій базі код HSE400_GOLD
        // уже зайнятий іншими тестами (UQ_Project_Code), тож тут — лише те, що звичайний проєкт попередження не має.
        var facts = await Store(db, EmptyGraph()).ReadFactsAsync(a.ProjectId, lockForDelete: false, CancellationToken.None);

        var map = Assert.Single(facts!.EventMaps);
        Assert.Equal(a.DocumentId, map.DocumentId);
        Assert.Empty(facts.Dependants);
        Assert.Empty(facts.Warnings);
        Assert.Equal(Ecr.Application.Projects.PurgeProjectHandler.EventMapKey, Ecr.Application.Projects.PurgeProjectHandler.BlockerKey(facts));

        await db.Database.ExecuteSqlAsync($"DELETE FROM ext.SourceEventMap WHERE DocumentId = {a.DocumentId}");
    }

    private sealed class JobRow
    {
        public string JobId { get; set; } = null!;
        public string State { get; set; } = null!;
        public bool CancelRequested { get; set; }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Архівний_період_B69_видно_у_фактах()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        await using (var db = builder.CreateContext())
        {
            // ⚠ Рядок архіву — сирим SQL: предмет тесту — лише те, що дані документа є в arc.* (B69).
            var archivedId = await new BulkCellLoader(sql.ConnectionString, 1000)
                .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt, SheetCopyId)
                VALUES (202512, {archivedId}, {a.DocumentId}, {a.TableDefId}, {At}, {At}, NULL)
                """);
        }

        await using var read = builder.CreateContext();
        var facts = await Store(read, EmptyGraph()).ReadFactsAsync(a.ProjectId, lockForDelete: false, CancellationToken.None);

        Assert.Equal([202512], facts!.ArchivedPeriodKeys);
        Assert.Equal(Ecr.Application.Projects.PurgeProjectHandler.ArchivedKey, Ecr.Application.Projects.PurgeProjectHandler.BlockerKey(facts));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_джерело_B80_дає_залежних_з_інших_проєктів()
    {
        const int Year = 2026;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var source = await builder.BuildAsync(periodKey: (Year * 100) + 1, columnCount: 1, rowCount: 1);
        var consumer = await builder.BuildAsync(periodKey: (Year * 100) + 1, columnCount: 1, rowCount: 1);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int bindingId;
        int registryId;
        await using (var db = builder.CreateContext())
        {
            var table = new TableDef(
                source.SheetDefId, EcrCode.Create("FG_Internal"), Name("FG_Internal"), 50,
                TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();
            var instanceId = await new BulkCellLoader(sql.ConnectionString, 1000)
                .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(source.PeriodKey, instanceId, source.DocumentId, table.Id, At));

            var registry = new RegistryDef(EcrCode.Create($"HP{tag}"), Name("Composition"), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();

            var binding = new DocumentSourceBinding($"PURGE_{tag}", source.TemplateId, registry.Id);
            db.DocumentSourceBindings.Add(binding);
            await db.SaveChangesAsync();
            await db.DocumentSourceBindings.Where(x => x.Id == binding.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadFromDocument, true));

            bindingId = binding.Id;
            registryId = registry.Id;

            // ⚠ Документ шаблону-споживача і в САМОМУ проєкті-джерелі: він зникне разом із проєктом, тож залежним
            // (тим, що блокує) не є — лише документи ІНШИХ проєктів.
            var own = new Document(source.ProjectId, $"OWN{tag}", 1, At);
            db.Documents.Add(own);
            await db.SaveChangesAsync();
            var ownInstance = await new BulkCellLoader(sql.ConnectionString, 1000)
                .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(source.PeriodKey, ownInstance, own.Id, consumer.TableDefId, At));
            await db.SaveChangesAsync();
        }

        var graph = Graph(new TemplateDependency(source.TemplateId, consumer.TemplateId, bindingId, registryId, default, true, false));

        await using var read = builder.CreateContext();
        var facts = await Store(read, graph).ReadFactsAsync(source.ProjectId, lockForDelete: false, CancellationToken.None);

        var dependant = Assert.Single(facts!.Dependants);
        Assert.Equal(consumer.DocumentId, dependant.DocumentId);
        Assert.Equal($"SourceBinding:PURGE_{tag}:{Year}", dependant.Reason);
        Assert.Equal(Ecr.Application.Projects.PurgeProjectHandler.DependantsKey, Ecr.Application.Projects.PurgeProjectHandler.BlockerKey(facts));

        // Проєкт-споживач сам нікого не годує: його видаленню залежні не заважають.
        var consumerFacts = await Store(read, graph).ReadFactsAsync(consumer.ProjectId, lockForDelete: false, CancellationToken.None);
        Assert.Empty(consumerFacts!.Dependants);
    }

    private static ProjectPurgeStore Store(Ecr.Infrastructure.Persistence.EcrDbContext db, IDependencyGraphProvider graph)
        => new(db, new DocumentDeletionStore(db), new ProjectStore(db), graph);

    private static IDependencyGraphProvider EmptyGraph() => Graph();

    private static IDependencyGraphProvider Graph(params TemplateDependency[] dependencies)
    {
        var snapshot = new DependencyGraphSnapshot("test", [], [], dependencies, [], [], []);
        var graph = Substitute.For<IDependencyGraphProvider>();
        graph.GetAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        graph.GetFreshAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        return graph;
    }

    /// <summary>Дані документа, яких у чернетки немає: комірки, погодження з історією, зріз подання, прогони, проблема.</summary>
    private static async Task PopulateAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();
        foreach (var row in doc.RowIds)
        {
            foreach (var column in doc.ColumnDefIds.Skip(1))
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                    VALUES ({doc.PeriodKey.Value}, {row}, {column}, {doc.TableDefId}, 1.5, 0, 0)
                    """);
            }
        }

        var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
        state.Submit(1, At);
        state.Approve(1, At.AddHours(1));
        db.ApprovalStates.Add(state);
        db.ApprovalEvents.Add(new ApprovalEvent(
            doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, DocumentStatus.Submitted, DocumentStatus.Approved,
            ApprovalAction.Approve, byUserId: null, At.AddHours(1)));
        db.SubmissionSnapshots.Add(new SubmissionSnapshot(
            doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, doc.TemplateVersionId, "[]", null, null,
            "{\"out\":1}", new byte[32], At, 1));

        var documentRun = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, At, doc.DocumentId);
        var projectRun = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, At);
        db.CalculationRuns.AddRange(documentRun, projectRun);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO calc.CalculationIssue (DocumentId, PeriodKey, TableDefId, RowKey, RowNumber, Kind, CalculationRunId, DetectedAt)
            VALUES ({doc.DocumentId}, {doc.PeriodKey.Value}, {doc.TableDefId}, N'R-1', 1, {CalculationIssue.MissingInputsKind}, {projectRun.Id}, {At})
            """);
    }

    /// <summary>Рядки проєкту в кожній таблиці з посиланням на документ або проєкт — перелік із каталогу бази.</summary>
    private static async Task<Dictionary<string, long>> RowCountsAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();
        var tables = await db.Database.SqlQuery<TableColumn>($"""
            SELECT s.name + N'.' + t.name AS TableName, c.name AS ColumnName
              FROM sys.columns AS c
              JOIN sys.tables AS t ON t.object_id = c.object_id
              JOIN sys.schemas AS s ON s.schema_id = t.schema_id
             WHERE c.name IN (N'DocumentId', N'ProjectId')
               AND s.name IN (N'doc', N'calc', N'wf', N'ext', N'cfg', N'itg', N'rpt')
               AND t.temporal_type <> 1
               AND NOT (s.name = N'itg' AND t.name = N'JobProgress')
            """).ToListAsync();

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables.OrderBy(t => t.TableName, StringComparer.Ordinal))
        {
            var predicate = table.ColumnName == "ProjectId"
                ? $"x.ProjectId = {projectId}"
                : $"x.DocumentId IN (SELECT d.Id FROM doc.Document AS d WHERE d.ProjectId = {projectId})";
#pragma warning disable EF1002 // ⚠ Ім'я таблиці — з sys.tables, не з вводу.
            var count = await db.Database.SqlQueryRaw<long>($"SELECT COUNT_BIG(*) AS Value FROM {table.TableName} AS x WHERE {predicate}").SingleAsync();
#pragma warning restore EF1002
            counts[table.TableName] = counts.GetValueOrDefault(table.TableName) + count;
        }

        counts["doc.TableRow"] = await db.Database.SqlQuery<long>($"""
            SELECT COUNT_BIG(*) AS Value FROM doc.TableRow AS r
              JOIN doc.TableInstance AS i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
              JOIN doc.Document AS d ON d.Id = i.DocumentId WHERE d.ProjectId = {projectId}
            """).SingleAsync();
        counts["doc.CellValue"] = await db.Database.SqlQuery<long>($"""
            SELECT COUNT_BIG(*) AS Value FROM doc.CellValue AS c
              JOIN doc.TableRow AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
              JOIN doc.TableInstance AS i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
              JOIN doc.Document AS d ON d.Id = i.DocumentId WHERE d.ProjectId = {projectId}
            """).SingleAsync();
        counts["doc.Document"] = await db.Documents.LongCountAsync(d => d.ProjectId == projectId);
        counts["doc.Project"] = await db.Projects.LongCountAsync(p => p.Id == projectId);
        return counts;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class TableColumn
    {
        public string TableName { get; set; } = null!;
        public string ColumnName { get; set; } = null!;
    }
}
