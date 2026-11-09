// tests/Ecr.Infrastructure.Tests/Persistence/SheetNameRecalculationTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>SHEETNAME()</c> на <b>реальному</b> SQL Server: копія аркуша рахує «&lt;Назва&gt; 2», оригінал «&lt;Назва&gt; 1»,
/// а перейменування групи ставить перерахунок формул аркуша (задача <c>IRecalculationJob</c>, той самий шлях, що правка
/// шапки), після якого значення — нова назва.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не комітяться): прибрати постановку з <c>DocumentSheetCopyHandler.RenameGroupAsync</c> —
/// червоніє <see cref="Перейменування_групи_ставить_перерахунок_і_формула_дає_нову_назву"/> (задачі немає); не
/// передавати назви документа в контекст (<c>RecalculationService</c>, <c>sheetNames</c>) — червоніють обидва тести
/// (копія й оригінал дають назву шаблону без номера).
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetNameRecalculationTests(SqlServerFixture sql)
{
    private const int UserId = 1;
    private const int FormulaId = 930_001;
    private static readonly DateTime Now = new(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Копія_рахує_свою_назву_з_номером_а_оригінал_номер_1()
    {
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);

        await world.RecalculateSheetAsync();

        Assert.Equal("Vessel 1", await TextAsync(world.Doc, world.Doc.RowIds[0]));
        Assert.Equal("Vessel 2", await TextAsync(world.Doc, copyRow));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Перейменування_групи_ставить_перерахунок_і_формула_дає_нову_назву()
    {
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);
        await world.RecalculateSheetAsync();
        world.Jobs.ClearReceivedCalls();

        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Contract" }, default));

        // Постановка — та сама задача й ціль, що в правки шапки, звужена до аркуша.
        var call = Assert.Single(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name == "EnqueueCoalescedAsync");
        Assert.Equal(typeof(IRecalculationJob), call.GetMethodInfo().GetGenericArguments()[0]);
        Assert.Equal(RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, world.Doc.PeriodKey), call.GetArguments()[0]);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(call.GetArguments()[1]));
        Assert.Equal(world.Doc.SheetDefId, payload.RootElement.GetProperty("SheetDefId").GetInt32());
        Assert.Equal(world.Doc.PeriodKey.Value, payload.RootElement.GetProperty("PeriodKey").GetInt32());

        // Що зробить задача: повний прогін аркуша (RecalculationJob → RecalculateAllAsync(sheetDefId)).
        await world.RecalculateSheetAsync();

        Assert.Equal("Contract 1", await TextAsync(world.Doc, world.Doc.RowIds[0]));
        Assert.Equal("Contract 2", await TextAsync(world.Doc, copyRow));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Аркуш_без_SHEETNAME_не_ставить_перерахунку_при_перейменуванні()
    {
        var world = await ArrangeAsync(expression: "'fixed'");
        await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        world.Jobs.ClearReceivedCalls();

        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Contract" }, default));

        Assert.DoesNotContain(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name.StartsWith("Enqueue", StringComparison.Ordinal));
    }

    private async Task<World> ArrangeAsync(string expression = "SHEETNAME()")
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202603, columnCount: 1, rowCount: 1, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId);
            period.AdvanceTo(PeriodState.Open, Now);
            await db.SaveChangesAsync();
        }

        var snapshot = Snapshot(doc, expression);
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var access = Substitute.For<IAccessDecisionService>();
        var profile = new AccessBuilder { UserId = UserId }
            .Permission(DocumentSheetCopyHandler.Permission)
            .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write)
            .Build();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile);
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(_ => DocumentReadScope.For(profile, doc.ProjectId, snapshot));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        user.CorrelationId.Returns("sheet-name");

        // Quartz-подібна черга: постановка ПІСЛЯ коміту (EnlistsInCallerTransaction = false).
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);

        return new World(builder, doc, metadata, access, user, jobs);
    }

    /// <summary>Екземпляр копії аркуша за період з тим самим Fixed-рядком (як при першому відкритті копії).</summary>
    private async Task<long> AddCopyInstanceAsync(TestDocument doc, long sheetCopyId)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0] && r.PeriodKeyValue == doc.PeriodKey.Value)
            .Select(r => r.RowKeyValue)
            .SingleAsync();

        db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, doc.TableDefId, Now, sheetCopyId));
        db.TableRows.Add(new TableRow(doc.PeriodKey, rowId, instanceId, RowKey.Create(rowKey), 1, Now));
        await db.SaveChangesAsync();

        return rowId;
    }

    private static TemplateVersionSnapshot Snapshot(TestDocument doc, string expression)
    {
        var tag = doc.SheetCode["SHEET".Length..];

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create(doc.SheetCode), Name("Vessel"), 1);
        SetId(sheet, doc.SheetDefId);
        sheet.SetCopyable(true);

        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TBL{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, doc.TableDefId);

        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C1_{tag}"), Name("Name"), 1, CellDataType.String);
        SetId(column, doc.ColumnDefIds[0]);
        table.AddColumn(column);

        var row = new RowDef(doc.TableDefId, RowKey.Create($"R1_{tag}"), 1, Name("Row 1"), RowKind.Item);
        SetId(row, doc.RowDefIds[0]);
        table.AddRow(row);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        SetId(formula, FormulaId);
        formula.AssignColumn(column.Id);
        formula.SetEvaluationOrder(1);
        table.AddFormula(formula);

        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            doc.TemplateVersionId, 0, [sheet], new Dictionary<int, ColumnDef> { [column.Id] = column },
            new Dictionary<(int, string), RowDef> { [(table.Id, row.RowKeyValue)] = row });
    }

    private async Task<string?> TextAsync(TestDocument doc, long rowId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueString FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[0]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class World(
        TestDocumentBuilder builder, TestDocument doc, IMetadataCache metadata, IAccessDecisionService access,
        ICurrentUser user, IBackgroundJobScheduler jobs)
    {
        public TestDocument Doc { get; } = doc;

        public IBackgroundJobScheduler Jobs { get; } = jobs;

        /// <summary>Новий контекст і обробник на КОЖЕН виклик — як на кожен HTTP-запит.</summary>
        public async Task<T> Run<T>(Func<DocumentSheetCopyHandler, Task<T>> action)
        {
            await using var db = builder.CreateContext();
            return await action(Handler(db)).ConfigureAwait(false);
        }

        public async Task Run(Func<DocumentSheetCopyHandler, Task> action)
        {
            await using var db = builder.CreateContext();
            await action(Handler(db)).ConfigureAwait(false);
        }

        /// <summary>Те, що робить <c>RecalculationJob</c> з payload постановки: повний прогін аркуша за період.</summary>
        public async Task RecalculateSheetAsync()
        {
            await using var db = builder.CreateContext();
            await Service(db).RecalculateAllAsync(Doc.DocumentId, Doc.PeriodKey, CancellationToken.None, Doc.SheetDefId);
        }

        private DocumentSheetCopyHandler Handler(EcrDbContext db)
            => new(
                new DocumentStore(db), new DocumentSheetCopyStore(db), metadata, new SheetEditGate(db), access,
                new UnitOfWork(db), new AuditWriter(db), user, new FixedClock(), Jobs, new PeriodStore(db));

        private RecalculationService Service(EcrDbContext db)
        {
            var clock = new FixedClock();
            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(Doc.DocumentId, Doc.PeriodKey.Value, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
            periods.FindPeriodStateAsync(Doc.DocumentId, Doc.PeriodKey.Value, Arg.Any<CancellationToken>())
                   .Returns((PeriodState?)PeriodState.Open);

            var versions = Substitute.For<ITemplateVersionStore>();
            versions.ListFormulaDependenciesAsync(Doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns([]);

            var units = Substitute.For<IUnitCatalog>();
            units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

            var headers = Substitute.For<IDocumentHeaderStore>();
            headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(new Dictionary<string, ExpressionValue>());

            return new RecalculationService(
                new NormalizedCellStore(db), new RowStore(db, new BulkCellLoader(db.Database.GetConnectionString()!, 1000), clock),
                periods, metadata, versions, new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
                new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db),
                sheetCopies: new DocumentSheetCopyStore(db));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = Now;
    }
}
