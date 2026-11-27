// tests/Ecr.Infrastructure.Tests/Persistence/RequiredInputsDerivedCopyMonthTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.RequiredInputs;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ D-578 (тестова прогалина, борги 27.11 п. 2): обов'язковий вхід, ВИВЕДЕНИЙ з полів, які читає скрипт методології, на
/// поданні й «Перевірити» — для КОПІЇ аркуша і для МІСЯЧНОГО екземпляра річної розкладки. Справжня база: план вимог зі
/// справжнього <see cref="MethodologyStore"/> (прочитані поля версії), знімок — <see cref="MetadataCache"/>, зріз —
/// <see cref="MonthRoutedCellStore"/> над <see cref="NormalizedCellStore"/> (як у застосунку).
/// </summary>
/// <remarks>
/// <para>
/// Копія: вимога діє на рядки ТІЄЇ копії, яку подають (<c>SubmissionPayloadSource.ForSheetCopy</c>), — порожнє поле копії
/// блокує подання копії й не блокує оригінал; «Перевірити» документа — по оригіналах (копія перевіряється своїм поданням).
/// </para>
/// <para>
/// Місяць річної розкладки: не-місячне поле вводу, яке читає скрипт, живе в екземплярі-власнику (якір року); місячний
/// екземпляр бачить його в ЛОГІЧНОМУ зрізі — заповнене в якорі не блокує подання травня, порожнє — блокує.
/// </para>
/// МУТАЦІЇ (локально): (1) у <c>MethodologyRequiredInputs.ResolveAsync</c> не додавати виведених (<c>derived</c> → порожньо) —
/// червоні всі «блокує»; (2) у <c>SubmitSheetHandler</c> брати всі екземпляри замість <c>ForSheetCopy</c> — червоний «копія»
/// (подання оригіналу блокує порожнє поле копії); (3) у тесті зрізу травня підставити <see cref="NormalizedCellStore"/> без
/// маршрутизації — червоний «місяць» (заповнене в якорі поле травня — «порожнє»).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage3)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "D-578")]
public sealed class RequiredInputsDerivedCopyMonthTests(SqlServerFixture sql) : IDisposable
{
    private const int UserId = 1;

    private const string RequiredInputRule = "ECR-CALC-0437";

    private readonly MemoryCache memory = new(new MemoryCacheOptions());

    public void Dispose() => memory.Dispose();

    [Fact]
    public async Task Копія_аркуша_виведений_вхід_блокує_подання_своєї_копії_а_не_оригіналу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202603, columnCount: 3, rowCount: 1);
        var (input, derived) = (doc.ColumnDefIds[1], doc.ColumnDefIds[2]);
        await MethodologyReadingAsync(doc, derived);

        var copyId = await AddSheetCopyAsync(builder, doc);
        var copy = await AddCopyInstanceAsync(builder, doc, copyId);

        // Оригінал: рядок розпочато, виведене поле заповнене; копія: розпочато, виведене поле ПОРОЖНЄ.
        await WriteAsync(doc.TableInstanceId, doc.TableDefId, doc.PeriodKey, doc.RowIds[0], (input, 5m), (derived, 7m));
        await WriteAsync(copy.InstanceId, doc.TableDefId, doc.PeriodKey, copy.RowId, (input, 6m));

        var blocked = await SubmitBlockedAsync(doc, doc.PeriodKey, copyId);
        var only = Assert.Single(blocked);
        Assert.Equal(RequiredInputRule, only.RuleCode);
        Assert.Equal(copy.RowKey, only.RowKey);
        Assert.Equal(await CodeOfAsync(derived), only.ColumnCode);

        // Оригінал (поле заповнене) — без жодної вимоги входу: порожнє поле копії його не блокує.
        Assert.DoesNotContain(await SubmitBlockedAsync(doc, doc.PeriodKey, sheetCopyId: null), m => m.RuleCode == RequiredInputRule);

        // «Перевірити» документа — по оригіналах: порожнє поле копії сюди не потрапляє.
        Assert.DoesNotContain(await ValidateAsync(doc, doc.PeriodKey, routed: true), m => m.RuleCode == RequiredInputRule);

        // Контроль: порожнє поле ОРИГІНАЛУ «Перевірити» показує, і подання оригіналу воно блокує.
        await WriteAsync(doc.TableInstanceId, doc.TableDefId, doc.PeriodKey, doc.RowIds[0], (derived, null));
        Assert.Contains(await ValidateAsync(doc, doc.PeriodKey, routed: true), m => m.RuleCode == RequiredInputRule);
        Assert.Contains(await SubmitBlockedAsync(doc, doc.PeriodKey, sheetCopyId: null), m => m.RuleCode == RequiredInputRule);
    }

    [Fact]
    public async Task Місячний_екземпляр_річної_розкладки_бачить_виведене_поле_якоря_логічним_зрізом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: 202601, columnCount: 3, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        await using (var db = builder.CreateContext())
        {
            await db.TemplateVersions.Where(v => v.Id == doc.TemplateVersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Layout, TemplateLayout.YearWithMonthColumns));
        }

        var may = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202605));
        var derived = doc.ColumnDefIds[2];
        await MethodologyReadingAsync(doc, derived);

        // Не-місячне поле вводу — власник якір (січень): заповнене там, травень бачить його логічним зрізом.
        await WriteAsync(doc.TableInstanceId, doc.TableDefId, doc.PeriodKey, doc.RowIds[0], (derived, 9m));
        Assert.DoesNotContain(await SubmitBlockedAsync(doc, may.PeriodKey, sheetCopyId: null), m => m.RuleCode == RequiredInputRule);
        Assert.DoesNotContain(await ValidateAsync(doc, may.PeriodKey, routed: true), m => m.RuleCode == RequiredInputRule);

        // Порожнє в якорі — травень не подається, «Перевірити» травня показує вхід.
        await WriteAsync(doc.TableInstanceId, doc.TableDefId, doc.PeriodKey, doc.RowIds[0], (derived, null));
        var blocked = await SubmitBlockedAsync(doc, may.PeriodKey, sheetCopyId: null);
        var only = Assert.Single(blocked, m => m.RuleCode == RequiredInputRule);
        Assert.Equal(may.RowKeys[0], only.RowKey);
        Assert.Equal(await CodeOfAsync(derived), only.ColumnCode);
        Assert.Contains(await ValidateAsync(doc, may.PeriodKey, routed: true), m => m.RuleCode == RequiredInputRule);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Blocked(string? RuleCode, string? RowKey, string? ColumnCode);

    /// <summary>
    /// Опублікована методологія з правилом відбору «усі рядки», прив'язана до таблиці документа, чий скрипт читає поле
    /// <paramref name="derivedColumn"/> (рядок <c>calc.MethodologyReadField</c>) — вимога Block, виведена (D-578).
    /// </summary>
    private async Task MethodologyReadingAsync(TestDocument doc, int derivedColumn)
    {
        await using var db = Context();
        var code = (await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == derivedColumn)).Code;
        var methodology = new Methodology(
            EcrCode.Create($"DR_{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "derived" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, at);
        version.Publish(2, "d578", new DateOnly(2026, 1, 1), testsPassed: true, at);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("ALL"), "{}", 100));
        db.MethodologyReadFields.Add(new MethodologyReadField(version.Id, code, otherMonths: false));
        db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, doc.ColumnDefIds[0], methodology.Id, "tons", "{}"));
        await db.SaveChangesAsync();
    }

    private async Task<long> AddSheetCopyAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();
        var sheet = await db.SheetDefs.SingleAsync(x => x.Id == doc.SheetDefId);
        sheet.SetCopyable(true);
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        await db.SaveChangesAsync();
        return (await new DocumentSheetCopyStore(db).AddCopyAsync(doc.DocumentId, doc.SheetDefId, CancellationToken.None)).SheetCopyId;
    }

    /// <summary>Екземпляр таблиці копії з тим самим рядком шаблону, що в оригіналі.</summary>
    private async Task<(long InstanceId, long RowId, string RowKey)> AddCopyInstanceAsync(
        TestDocumentBuilder builder, TestDocument doc, long sheetCopyId)
    {
        await using var db = builder.CreateContext();
        var original = await db.TableRows.AsNoTracking()
            .SingleAsync(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0]);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, doc.TableDefId, now, sheetCopyId));
        db.TableRows.Add(new TableRow(doc.PeriodKey, rowId, instanceId, RowKey.Create(original.RowKeyValue), original.Ordinal, now));
        await db.SaveChangesAsync();
        return (instanceId, rowId, original.RowKeyValue);
    }

    private async Task WriteAsync(
        long instanceId, int tableDefId, PeriodKey period, long rowId, params (int Column, decimal? Value)[] values)
    {
        await using var db = Context();
        await new NormalizedCellStore(db).ApplyAsync(
            new CellChangeSet(
                instanceId,
                [.. values.Select(v => new CellRecord(
                    new CellAddress(period, rowId, v.Column), tableDefId,
                    v.Value is { } number ? new CellValueData { ValueNumeric = number } : new CellValueData()))],
                [], [], UserId, IsLateEdit: false),
            CancellationToken.None);
    }

    private async Task<string> CodeOfAsync(int columnId)
    {
        await using var db = Context();
        return (await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == columnId)).Code;
    }

    /// <summary>Подає аркуш (копію) і повертає повідомлення 422; подання пройшло — порожньо.</summary>
    private async Task<List<Blocked>> SubmitBlockedAsync(TestDocument doc, PeriodKey period, long? sheetCopyId)
    {
        await using var db = Context();
        try
        {
            await BuildSubmit(db, doc, period, sheetCopyId)
                .HandleAsync(doc.DocumentId, doc.SheetDefId, period.Value, acknowledgeWarnings: true, sheetCopyId, CancellationToken.None);
        }
        catch (BusinessRuleException error) when (error.Details?["messages"] is System.Collections.IEnumerable messages)
        {
            return [.. messages.Cast<object>().Select(m =>
            {
                var type = m.GetType();
                return new Blocked(
                    (string?)type.GetProperty("RuleCode")!.GetValue(m),
                    (string?)type.GetProperty("RowKey")!.GetValue(m),
                    (string?)type.GetProperty("ColumnCode")!.GetValue(m));
            })];
        }

        return [];
    }

    private async Task<IReadOnlyList<ValidationMessage>> ValidateAsync(TestDocument doc, PeriodKey period, bool routed)
    {
        await using var db = Context();
        var metadata = Metadata(db);
        var access = Access(doc, metadata);
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var clock = new FixedClock(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc));
        var headers = Headers();
        var results = Substitute.For<IValidationResultStore>();
        return await new ValidateDocumentHandler(
                Cells(db, metadata, routed), new RowStore(db, bulk, clock), metadata, results,
                new ValidationEngine(new RealFormulaEngine()), headers, clock, new UnitOfWork(db), access, User(),
                Substitute.For<IRegistryStore>(), new TemplateVersionStore(db),
                requiredInputs: Scanner(db))
            .HandleAsync(doc.DocumentId, period, CancellationToken.None);
    }

    private SubmitSheetHandler BuildSubmit(EcrDbContext db, TestDocument doc, PeriodKey period, long? sheetCopyId)
    {
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var clock = new FixedClock(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc));
        var metadata = Metadata(db);

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.HasSheetCopyAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);

        var access = Access(doc, metadata);
        access.CanSubmitAsync(Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanSubmitCopyAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        // Свіжість розрахунку — не предмет тесту: прив'язка є (вимоги плануються), прогін актуальний.
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(new List<int> { 1 });
        methodologies.GetCalculationFreshnessAsync(
                         Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                     .Returns(new CalculationFreshness(null, null));

        var cells = Cells(db, metadata, routed: true);
        return new SubmitSheetHandler(
            cells, new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            metadata, access,
            new ValidationEngine(new RealFormulaEngine()),
            Headers(),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db),
            Recalculation(db, doc, period, metadata, clock, cells),
            methodologies,
            new TemplateVersionStore(db),
            new RegistryStore(db),
            new AuditWriter(db),
            requiredInputs: Scanner(db));
    }

    private static RequiredInputsScanner Scanner(EcrDbContext db) => new(new MethodologyStore(db), new PeriodStore(db));

    private MetadataCache Metadata(EcrDbContext db) => new(memory, db);

    private static ICellStore Cells(EcrDbContext db, MetadataCache metadata, bool routed)
    {
        var inner = new NormalizedCellStore(db);
        if (!routed)
        {
            return inner;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();
        return new MonthRoutedCellStore(inner, new MonthColumnRouter(db, configuration), metadata, db);
    }

    private static IAccessDecisionService Access(TestDocument doc, MetadataCache metadata)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = UserId }.Permission("Document.View").Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
              .Returns(async _ => ReadScopes.Everything(await metadata.GetAsync(doc.TemplateVersionId, CancellationToken.None)));
        return access;
    }

    private static IDocumentHeaderStore Headers()
    {
        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());
        return headers;
    }

    private static RecalculationService Recalculation(
        EcrDbContext db, TestDocument doc, PeriodKey period, MetadataCache metadata, IClock clock, ICellStore cells)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, period.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, period.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new RecalculationService(
            cells, new RowStore(db, bulk, clock), periods, metadata, versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), Headers(),
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
