using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-230: зв'язок виду Check впливає на подання аркуша так само, як правила валідації:
/// <c>Block</c> (→ Error) блокує, <c>Warn</c> потребує підтвердження, <c>Pass</c> і <c>Info</c> не заважають.
/// </summary>
/// <remarks>
/// ⚠ Реальні <c>RowStore</c>, <c>WorkflowStore</c>, база; комірки для Check — через підставний
/// <see cref="ICellStore"/> (предмет тесту — рішення подання, а не запис комірок). Схеми Check — ПРИПУЩЕННЯ.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitRelationCheckTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    private static int _counter;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Check_Block_із_відхиленням_блокує_подання_422_з_переліком()
    {
        var run = await RunAsync("Block", left: 10m, right: 12m);

        var error = Assert.IsType<BusinessRuleException>(run.Error);
        Assert.Equal(ErrorCodes.SubmitBlocked, error.ErrorCode);
        Assert.Equal("err.ECR-SUB-4221.validationBlocked", error.Details?["messageKey"]);
        Assert.Contains("REL-CHK1", JsonSerializer.Serialize(error.Details?["messages"]));
        Assert.False(run.Submitted, "Заблоковане подання лишило зріз.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Check_Warn_не_блокує_а_просить_підтвердження_і_з_підтвердженням_проходить()
    {
        var without = await RunAsync("Warn", left: 10m, right: 12m);
        var error = Assert.IsType<BusinessRuleException>(without.Error);
        Assert.Equal("err.ECR-SUB-4221.warningsNeedConfirmation", error.Details?["messageKey"]);

        var acknowledged = await RunAsync("Warn", left: 10m, right: 12m, acknowledge: true);
        Assert.Null(acknowledged.Error);
        Assert.True(acknowledged.Submitted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Check_Info_не_впливає_на_подання()
    {
        var run = await RunAsync("Info", left: 10m, right: 12m);

        Assert.Null(run.Error);
        Assert.True(run.Submitted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Check_Block_без_відхилення_подання_проходить()
    {
        var run = await RunAsync("Block", left: 10m, right: 10m);

        Assert.Null(run.Error);
        Assert.True(run.Submitted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_активних_Check_зв_язки_не_читаються_взагалі()
    {
        var run = await RunAsync("Block", left: 10m, right: 12m, flag: false);

        Assert.Null(run.Error);
        Assert.True(run.Submitted);
        await run.Versions.DidNotReceive().ListTableRelationsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// T1-01: Check видимого приймача, джерело якого під забороною (колонка чи таблиця), не віддає значень
    /// джерела в тілі 422 — лише знеособлене зауваження; без заборони значення на місці.
    /// </summary>
    /// <remarks>Мутація: у <c>HiddenValidationIssues.CanSee</c> прибрати перевірку джерела — тест червоніє.</remarks>
    [Theory]
    [InlineData(ResourceKind.Column)]
    [InlineData(ResourceKind.Table)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Check_Block_із_забороненим_джерелом_не_розкриває_значень_у_422(ResourceKind deny)
    {
        var run = await RunAsync("Block", left: 777.5m, right: 55m, deny: deny);

        var error = Assert.IsType<BusinessRuleException>(run.Error);
        Assert.Equal("err.ECR-SUB-4221.hiddenIssues", error.Details?["messageKey"]);
        var body = JsonSerializer.Serialize(error.Details) + error.Message;
        Assert.DoesNotContain("777.5", body, StringComparison.Ordinal);
        Assert.DoesNotContain("REL-CHK1", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Check_Block_без_заборони_422_містить_значення_регресія()
    {
        var run = await RunAsync("Block", left: 777.5m, right: 55m);

        var error = Assert.IsType<BusinessRuleException>(run.Error);
        Assert.Contains("777.5", JsonSerializer.Serialize(error.Details), StringComparison.Ordinal);
    }

    private async Task<Outcome> RunAsync(
        string severity, decimal left, decimal right, bool acknowledge = false, bool flag = true, ResourceKind? deny = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1);
        await using var db = builder.CreateContext();

        // Таблиця-приймач B того самого аркуша з одним рядком (зв'язок таблиці із собою заборонений).
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = $"{Interlocked.Increment(ref _counter)}K{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var tableB = new TableDef(
            doc.SheetDefId, EcrCode.Create($"TK{tag}"), Name($"Check {tag}"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        await db.SaveChangesAsync();
        var columnB = new ColumnDef(tableB.Id, EcrCode.Create($"K1_{tag}"), Name("Col"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);
        await db.SaveChangesAsync();
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceB = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowB = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceB, doc.DocumentId, tableB.Id, now));
        db.TableRows.Add(new TableRow(doc.PeriodKey, rowB, instanceB, RowKey.Create($"KR_{tag}"), 1, now));
        await db.SaveChangesAsync();

        var tableId = doc.TableDefId;
        var leftColumn = doc.ColumnDefIds[1];
        var rightColumn = columnB.Id;

        var relation = new TableRelationDef(EcrCode.Create("CHK1"), tableId, tableB.Id, TableRelationKind.Check, "{}");
        relation.Update(tableId, tableB.Id, TableRelationKind.Check, "{}",
            $$"""{"left":"C{{leftColumn}}","right":"C{{rightColumn}}","tolerance":"0","severity":"{{severity}}"}""", 0, true);
        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListTableRelationsAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>())
                .Returns(new List<TableRelationDef> { relation });
        versions.ListFormulaDependenciesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var cells = Substitute.For<ICellStore>();
        var period = doc.PeriodKey;
        var slices = new Dictionary<long, IReadOnlyList<CellRecord>>
        {
            [doc.TableInstanceId] =
            [
                new CellRecord(new CellAddress(period, doc.RowIds[0], leftColumn), tableId, new CellValueData { ValueNumeric = left }),
            ],
            [instanceB] =
            [
                new CellRecord(new CellAddress(period, rowB, rightColumn), tableB.Id, new CellValueData { ValueNumeric = right }),
            ],
        };
        cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(slices);

        var metadata = Metadata(doc, flag, tableB.Id, columnB.Id);
        var handler = BuildSubmit(db, doc, metadata, versions, cells, deny);

        Exception? error = null;
        try
        {
            await handler.HandleAsync(doc.DocumentId, doc.SheetDefId, period.Value, acknowledge, CancellationToken.None);
        }
        catch (BusinessRuleException ex)
        {
            error = ex;
        }

        var submitted = await db.SubmissionSnapshots.AnyAsync(s => s.DocumentId == doc.DocumentId);
        return new Outcome(error, submitted, versions);
    }

    private static SubmitSheetHandler BuildSubmit(
        EcrDbContext db, TestDocument doc, IMetadataCache metadata, ITemplateVersionStore versions, ICellStore cells,
        ResourceKind? deny = null)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CurrentApprovalStepAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns((ApprovalStepView?)null);

        var readerBuilder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (deny is { } kind)
        {
            readerBuilder = readerBuilder.Deny(kind, kind == ResourceKind.Column ? doc.ColumnDefIds[1] : doc.TableDefId);
        }

        var reader = readerBuilder.Build();
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
              .Returns(async _ => DocumentReadScope.For(
                  reader, AccessBuilder.ProjectId, await metadata.GetAsync(doc.TemplateVersionId, CancellationToken.None)));

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(new List<int>());
        methodologies.GetCalculationFreshnessAsync(
                         Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                     .Returns(new CalculationFreshness(null, null));

        return new SubmitSheetHandler(
            cells, new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db),
            Recalculation(db, doc, metadata, clock),
            methodologies,
            versions,
            new RegistryStore(db),
            new AuditWriter(db));
    }

    private static RecalculationService Recalculation(EcrDbContext db, TestDocument doc, IMetadataCache metadata, IClock clock)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods, metadata, versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    /// <summary>Знімок структури: аркуш з однією таблицею; коди колонок — <c>C{id}</c>.</summary>
    private static IMetadataCache Metadata(TestDocument doc, bool hasRelations, int tableBId, int columnBId)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);

        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, doc.TableDefId);

        var columnsById = new Dictionary<int, ColumnDef>();
        var c = 0;
        foreach (var columnId in doc.ColumnDefIds)
        {
            var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{columnId}"), Name("Col"), ++c, CellDataType.Decimal);
            SetId(column, columnId);
            table.AddColumn(column);
            columnsById[column.Id] = column;
        }

        sheet.AddTable(table);

        var tableB = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{tableBId}"), Name("Table B"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(tableB, tableBId);
        var columnB = new ColumnDef(tableBId, EcrCode.Create($"C{columnBId}"), Name("Col"), 1, CellDataType.Decimal);
        SetId(columnB, columnBId);
        tableB.AddColumn(columnB);
        columnsById[columnB.Id] = columnB;
        sheet.AddTable(tableB);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columnsById,
            RowsByKey: new Dictionary<(int, string), RowDef>())
        {
            HasActiveRollupOrCheck = hasRelations,
        };

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    private sealed record Outcome(Exception? Error, bool Submitted, ITemplateVersionStore Versions);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
