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
/// S6 (ФВ-6.6): тіло 422 подання не розкриває рядків і часу змін таблиць,
/// прихованих від того, хто подає; блокування при цьому те саме.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було: відмова <c>orphanedRows</c> віддавала <c>rowIds</c> і їхню
/// кількість по ВСЬОМУ документу, а <c>staleMethodologyResults</c> —
/// <c>inputsChangedAt</c>, час правки, яка могла лежати в прихованій таблиці
/// замикання. Обидва — дані з-під заборони.
/// </para>
/// <para>
/// ⚠ Реальні <c>RowStore</c> і база (ознака <c>IsOrphaned</c> і відповідність
/// «рядок → таблиця» — з <c>doc.TableRow</c>/<c>doc.TableInstance</c>); межі
/// читання будує бойовий <see cref="DocumentReadScope.For"/> із забороною на
/// таблицю B того самого аркуша.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitHiddenDetailsTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    private static readonly DateTime CalculatedAt = new(2026, 1, 20, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime InputsChangedAt = new(2026, 1, 21, 9, 30, 0, DateTimeKind.Utc);

    private static int _counter;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Сироти_прихованої_таблиці_не_потрапляють_у_rowIds()
    {
        var doc = await ArrangeAsync();
        await MarkOrphanedAsync(doc, doc.VisibleRowIds[0], doc.HiddenRowIds[0], doc.HiddenRowIds[1]);

        var error = await SubmitBlockedAsync(doc, hideTableB: true);

        Assert.Equal("err.ECR-SUB-4221.orphanedRows", error.Details?["messageKey"]);
        Assert.Equal([doc.VisibleRowIds[0]], RowIds(error));
        Assert.Equal("1", error.Details?["rowCount"]);
        Assert.DoesNotContain(doc.HiddenRowIds[0].ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Лише_приховані_сироти_блокують_знеособлено()
    {
        var doc = await ArrangeAsync();
        await MarkOrphanedAsync(doc, doc.HiddenRowIds[0], doc.HiddenRowIds[1]);

        var error = await SubmitBlockedAsync(doc, hideTableB: true);

        Assert.Equal("err.ECR-SUB-4221.hiddenIssues", error.Details?["messageKey"]);
        Assert.False(error.Details!.ContainsKey("rowIds"), "rowIds прихованих рядків у тілі 422.");
        Assert.False(error.Details!.ContainsKey("rowCount"), "Кількість прихованих рядків у тілі 422.");
        Assert.DoesNotContain("2", error.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Без_заборон_сироти_всіх_таблиць_у_відповіді()
    {
        var doc = await ArrangeAsync();
        await MarkOrphanedAsync(doc, doc.VisibleRowIds[0], doc.HiddenRowIds[0]);

        var error = await SubmitBlockedAsync(doc, hideTableB: false);

        Assert.Equal("err.ECR-SUB-4221.orphanedRows", error.Details?["messageKey"]);
        Assert.Equal(
            new[] { doc.VisibleRowIds[0], doc.HiddenRowIds[0] }.Order(),
            RowIds(error).Order());
        Assert.Equal("2", error.Details?["rowCount"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Застарілість_без_часу_змін_коли_таблиця_замикання_прихована()
    {
        var doc = await ArrangeAsync();

        var error = await SubmitBlockedAsync(doc, hideTableB: true, stale: true);

        Assert.Equal("err.ECR-SUB-4221.staleMethodologyResults", error.Details?["messageKey"]);
        Assert.Null(error.Details?["inputsChangedAt"]);
        Assert.Equal(CalculatedAt, error.Details?["calculatedAt"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Застарілість_з_часом_змін_без_заборон()
    {
        var doc = await ArrangeAsync();

        var error = await SubmitBlockedAsync(doc, hideTableB: false, stale: true);

        Assert.Equal("err.ECR-SUB-4221.staleMethodologyResults", error.Details?["messageKey"]);
        Assert.Equal(InputsChangedAt, error.Details?["inputsChangedAt"]);
        Assert.Equal(CalculatedAt, error.Details?["calculatedAt"]);
    }

    private static List<long> RowIds(BusinessRuleException error)
        => [.. Assert.IsAssignableFrom<IEnumerable<long>>(error.Details?["rowIds"])];

    private async Task<BusinessRuleException> SubmitBlockedAsync(Scenario doc, bool hideTableB, bool stale = false)
    {
        await using var db = CreateContext();
        var handler = BuildSubmit(db, doc, hideTableB, stale);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            handler.HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None));

        // ⛔ Блокування те саме — змінюється лише вміст тіла.
        Assert.Equal(ErrorCodes.SubmitBlocked, error.ErrorCode);
        Assert.False(
            await db.SubmissionSnapshots.AnyAsync(s => s.DocumentId == doc.DocumentId),
            "Заблоковане подання лишило зріз.");
        return error;
    }

    private async Task MarkOrphanedAsync(Scenario doc, params long[] rowIds)
    {
        await using var db = CreateContext();
        var now = new DateTime(2026, 1, 16, 10, 0, 0, DateTimeKind.Utc);
        var rows = await db.TableRows
            .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && rowIds.Contains(r.Id))
            .ToListAsync();
        Assert.Equal(rowIds.Length, rows.Count);
        foreach (var row in rows)
        {
            row.SetOrphaned(true, now);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Документ із двома таблицями одного аркуша: A (із будівника) і B.</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 2);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        await using var db = builder.CreateContext();
        var tag = $"{Interlocked.Increment(ref _counter)}H{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var table = new TableDef(
            doc.SheetDefId, EcrCode.Create($"TH{tag}"), Name($"Hidden {tag}"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create($"H1_{tag}"), Name("Col"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, table.Id, now));
        var hiddenRows = new List<long>();
        for (var r = 0; r < 2; r++)
        {
            db.TableRows.Add(new TableRow(doc.PeriodKey, firstRowId + r, instanceId, RowKey.Create($"HR{r}_{tag}"), r + 1, now));
            hiddenRows.Add(firstRowId + r);
        }

        await db.SaveChangesAsync();

        return new Scenario(doc, table.Id, column.Id, doc.RowIds, hiddenRows);
    }

    private static SubmitSheetHandler BuildSubmit(EcrDbContext db, Scenario doc, bool hideTableB, bool stale)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.Document.ProjectId);

        var metadata = Metadata(doc);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        var reader = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (hideTableB)
        {
            reader = reader.Deny(ResourceKind.Table, doc.HiddenTableDefId);
        }

        var readerProfile = reader.Build();
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
              .Returns(async _ => DocumentReadScope.For(
                  readerProfile, AccessBuilder.ProjectId,
                  await metadata.GetAsync(doc.Document.TemplateVersionId, CancellationToken.None)));

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

        // ⚠ Методологія прив'язана до таблиці A; таблиця B того самого аркуша
        // входить у замикання свіжості (усі таблиці аркуша).
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(stale ? new List<int> { 1 } : new List<int>());
        methodologies.GetCalculationFreshnessAsync(
                         doc.DocumentId, doc.PeriodKey.Value, Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                     .Returns(new CalculationFreshness(CalculatedAt, InputsChangedAt));

        return new SubmitSheetHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db),
            Recalculation(db, doc, metadata, clock),
            methodologies,
            new TemplateVersionStore(db),
            new RegistryStore(db),
            new AuditWriter(db));
    }

    private static RecalculationService Recalculation(EcrDbContext db, Scenario doc, IMetadataCache metadata, IClock clock)
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

    /// <summary>Знімок структури: аркуш із таблицями A і B (без обов'язкових колонок і правил).</summary>
    private static IMetadataCache Metadata(Scenario doc)
    {
        var sheet = new SheetDef(doc.Document.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);

        var columnsById = new Dictionary<int, ColumnDef>();

        void AddTable(int tableDefId, int ordinal, IEnumerable<int> columnIds)
        {
            var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{tableDefId}"), Name("Table"), ordinal,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            SetId(table, tableDefId);
            var c = 0;
            foreach (var columnId in columnIds)
            {
                var column = new ColumnDef(tableDefId, EcrCode.Create($"C{columnId}"), Name("Col"), ++c, CellDataType.Decimal);
                SetId(column, columnId);
                table.AddColumn(column);
                columnsById[column.Id] = column;
            }

            sheet.AddTable(table);
        }

        AddTable(doc.Document.TableDefId, 1, doc.Document.ColumnDefIds);
        AddTable(doc.HiddenTableDefId, 2, [doc.HiddenColumnDefId]);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.Document.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columnsById,
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.Document.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
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

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed record Scenario(
        TestDocument Document,
        int HiddenTableDefId,
        int HiddenColumnDefId,
        IReadOnlyList<long> VisibleRowIds,
        IReadOnlyList<long> HiddenRowIds)
    {
        public long DocumentId => Document.DocumentId;

        public int SheetDefId => Document.SheetDefId;

        public PeriodKey PeriodKey => Document.PeriodKey;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
