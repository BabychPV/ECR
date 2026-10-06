using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// T6-01 наскрізно: Check, доданий через <c>SaveTableRelationHandler</c> ПІСЛЯ прогріву кешу метаданих,
/// діє на <c>SubmitSheetHandler</c> одразу (Block - 422, Warn - підтвердження), а після видалення - знову не заважає.
/// </summary>
/// <remarks>
/// ⚠ Справжні <c>MetadataCache</c> (спільний <c>MemoryCache</c>), <c>SaveTableRelationHandler</c>,
/// <c>DeleteTableRelationHandler</c>, <c>SubmitSheetHandler</c> і база; значення комірок - підставний <see cref="ICellStore"/>.
/// Мутація: у <c>TableRelationHandlers.cs</c> <c>InvalidateAsync</c> замінити на <c>_ = metadataCache;</c> - Submit проходить, тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitRelationCacheTests(SqlServerFixture sql) : IDisposable
{
    private const int UserId = 1;
    private const string Match = """{"by":"RowKey"}""";

    private static int _counter;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Check_Block_доданий_після_прогріву_кешу_блокує_подання_а_після_видалення_не_заважає()
    {
        var scene = await ArrangeAsync(left: 777.5m, right: 55m);
        await WarmCacheAsync(scene);

        await SaveCheckAsync(scene, "Block");
        var error = Assert.IsType<BusinessRuleException>(await SubmitAsync(scene, acknowledge: false));
        Assert.Equal(ErrorCodes.SubmitBlocked, error.ErrorCode);
        Assert.Equal("err.ECR-SUB-4221.validationBlocked", error.Details?["messageKey"]);
        Assert.Contains(scene.RelationCode, JsonSerializer.Serialize(error.Details?["messages"]));
        Assert.False(await IsSubmittedAsync(scene), "Заблоковане подання лишило зріз.");

        // Видалення зв'язку у версії з документами - Breaking (ФВ-7.4), тож вимикаємо його (isActive=false).
        await SaveCheckAsync(scene, "Block", isActive: false);

        Assert.Null(await SubmitAsync(scene, acknowledge: false));
        Assert.True(await IsSubmittedAsync(scene), "Після вимкнення Check подання мало пройти.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Check_Warn_доданий_після_прогріву_кешу_просить_підтвердження()
    {
        var scene = await ArrangeAsync(left: 777.5m, right: 55m);
        await WarmCacheAsync(scene);

        await SaveCheckAsync(scene, "Warn");

        var error = Assert.IsType<BusinessRuleException>(await SubmitAsync(scene, acknowledge: false));
        Assert.Equal("err.ECR-SUB-4221.warningsNeedConfirmation", error.Details?["messageKey"]);
        Assert.False(await IsSubmittedAsync(scene));

        Assert.Null(await SubmitAsync(scene, acknowledge: true));
        Assert.True(await IsSubmittedAsync(scene));
    }

    /// <summary>Прогрів: знімок структури без зв'язків лягає в кеш (як після Validate до додавання Check).</summary>
    private async Task WarmCacheAsync(Scene scene)
    {
        await using var db = Context();
        var snapshot = await new MetadataCache(_memory, db).GetAsync(scene.Doc.TemplateVersionId, CancellationToken.None);
        Assert.False(snapshot.HasActiveRollupOrCheck, "Контроль: до правки зв'язків немає.");
    }

    private async Task SaveCheckAsync(Scene scene, string severity, bool isActive = true)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Permission("Template.View").Build());

        await using var db = Context();
        var handler = new SaveTableRelationHandler(
            new Repository<TemplateVersion, int>(db), new Repository<TableRelationDef, int>(db),
            new TemplateVersionStore(db), new ChangeClassifier(), new MetadataCache(_memory, db),
            new AuditWriter(db), new UnitOfWork(db), new FixedClock(Now), access, user);

        await handler.HandleAsync(
            scene.Doc.TemplateVersionId, scene.RelationCode,
            new SaveTableRelationCommand(
                scene.Doc.TableDefId, scene.TableBId, TableRelationKind.Check, Match,
                $$"""{"left":"{{scene.LeftCode}}","right":"{{scene.RightCode}}","tolerance":"0","severity":"{{severity}}"}""",
                0, isActive),
            CancellationToken.None);
    }

    /// <summary>Подає аркуш; повертає відмову бізнес-правила або <c>null</c>.</summary>
    private async Task<Exception?> SubmitAsync(Scene scene, bool acknowledge)
    {
        await using var db = Context();
        var metadata = new MetadataCache(_memory, db);
        var handler = BuildSubmit(db, scene, metadata);
        try
        {
            await handler.HandleAsync(
                scene.Doc.DocumentId, scene.Doc.SheetDefId, scene.Doc.PeriodKey.Value, acknowledge, CancellationToken.None);
            return null;
        }
        catch (BusinessRuleException ex)
        {
            return ex;
        }
    }

    private async Task<bool> IsSubmittedAsync(Scene scene)
    {
        await using var db = Context();
        return await db.SubmissionSnapshots.AnyAsync(s => s.DocumentId == scene.Doc.DocumentId);
    }

    private EcrDbContext Context() => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Документ із таблицею-приймачем B того самого аркуша; значення комірок задаються підставним сховищем.</summary>
    private async Task<Scene> ArrangeAsync(decimal left, decimal right)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1);
        await using var db = builder.CreateContext();

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
        db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceB, doc.DocumentId, tableB.Id, Now));
        db.TableRows.Add(new TableRow(doc.PeriodKey, rowB, instanceB, RowKey.Create($"KR_{tag}"), 1, Now));
        await db.SaveChangesAsync();

        var leftColumnId = doc.ColumnDefIds[1];
        var leftCode = (await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == leftColumnId)).Code;

        var cells = Substitute.For<ICellStore>();
        cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
             {
                 [doc.TableInstanceId] =
                 [
                     new CellRecord(
                         new CellAddress(doc.PeriodKey, doc.RowIds[0], leftColumnId), doc.TableDefId,
                         new CellValueData { ValueNumeric = left }),
                 ],
                 [instanceB] =
                 [
                     new CellRecord(
                         new CellAddress(doc.PeriodKey, rowB, columnB.Id), tableB.Id,
                         new CellValueData { ValueNumeric = right }),
                 ],
             });

        return new Scene(doc, tableB.Id, leftCode, columnB.Code, cells, $"CHK{tag}");
    }

    private SubmitSheetHandler BuildSubmit(EcrDbContext db, Scene scene, MetadataCache metadata)
    {
        var doc = scene.Doc;
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

        var reader = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build();
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

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        return new SubmitSheetHandler(
            scene.Cells, new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), user, clock, new SheetEditGate(db),
            Recalculation(db, doc, metadata, clock),
            methodologies,
            new TemplateVersionStore(db),
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

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private sealed record Scene(TestDocument Doc, int TableBId, string LeftCode, string RightCode, ICellStore Cells, string RelationCode);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
