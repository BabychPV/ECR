// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobSheetScopeResultsTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// RC14 (приймальна №10, P2-3/P2-4): перерахунок ОБЛАСТІ (аркуш) не торкається результатів методологій
/// ІНШОГО аркуша і не відхиляється через ПОДАНИЙ сусідній аркуш.
/// </summary>
/// <remarks>
/// ⛔ P2-4: прогін області ставав актуальним для ВСЬОГО документа й періоду
/// (<c>SwitchCurrentRunAsync</c>), а писав лише прив'язки свого аркуша — результати сусіднього аркуша
/// лишалися в старому, вже неактуальному прогоні і зникали з <c>ReadCurrentAsync</c>.
/// ⛔ P2-3: гейт <c>RefusedPeriodsAsync</c> рахував подані аркуші ВСЬОГО документа для області одного аркуша.
/// Мутація (локально): прибрати переніс результатів у <c>RecalculationJob</c> / фільтр за
/// <c>SheetDefId</c> у <c>RefusedPeriodsAsync</c> — червоніють відповідні тести.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobSheetScopeResultsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Перерахунок_аркуша_не_прибирає_результати_методологій_іншого_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);

        // Повний прогін документа: обидва аркуші мають результати зі штампом 1000.
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        // Перерахунок ОБЛАСТІ першого аркуша зі штампом 2000.
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: A оновлено, B лишився з попереднього прогону. До фіксу тут було лише `A|2001`.
        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));

        // Лише один актуальний прогін документа за період, без подвоєних рядків.
        await using var db = arranged.Builder.CreateContext();
        Assert.Equal(1, await db.CalculationRuns.AsNoTracking().CountAsync(r =>
            r.DocumentId == arranged.Document.DocumentId
            && r.PeriodKey == arranged.Document.PeriodKey.Value
            && r.Status == CalculationRun.CurrentStatus));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Повторні_перерахунки_області_не_множать_і_не_гублять_результати_сусіднього_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);

        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 3_000m);

        Assert.Equal(["A|3001", "B|1001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Перерахунок_усього_документа_лишається_повним()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);

        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m);

        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_чернеткового_аркуша_рахується_попри_поданий_сусідній_і_не_чіпає_його_результатів()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        // Другий аркуш ПОДАНО після першого повного прогону.
        await SubmitSecondSheetAsync(arranged);

        // До фіксу тут летів ECR-CALC-4221 sheetsSubmitted (глухий кут: Submit вимагає перерахунку).
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);

        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_самого_поданого_аркуша_і_весь_документ_із_поданим_аркушем_відхиляються_як_раніше()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: true);

        foreach (var sheetDefId in new int?[] { arranged.SecondSheetId, null })
        {
            var thrown = await Record.ExceptionAsync(() =>
                RunJobAsync(arranged, SheetRequest(arranged, sheetDefId), label: 2_000m));

            var error = Assert.IsType<BusinessRuleException>(thrown);
            Assert.Equal("ECR-CALC-4221", error.ErrorCode);
            Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
        }

        // Нічого не записано: результатів немає взагалі.
        Assert.Empty(await ShapeAsync(arranged));
    }

    private static RecalculationRequest SheetRequest(Arranged arranged, int? sheetDefId)
        => new(
            ProjectId: arranged.Document.ProjectId,
            DocumentId: arranged.Document.DocumentId,
            PeriodKey: arranged.Document.PeriodKey.Value,
            TriggeredByUserId: 7,
            SheetDefId: sheetDefId);

    /// <summary>Форма актуальних результатів: <c>вихід|значення</c>, упорядковано.</summary>
    private static async Task<List<string>> ShapeAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));
        var rows = await store.ReadCurrentAsync(
            arranged.Document.DocumentId, arranged.Document.PeriodKey.Value, default);

        return [.. rows.Select(r => $"{r.OutputCode.Replace("tons", string.Empty, StringComparison.Ordinal)}|{r.Value:0.####}")
            .Order(StringComparer.Ordinal)];
    }

    private async Task<Arranged> ArrangeAsync(bool submitSecondSheet)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);

        var sheet2 = new SheetDef(
            document.TemplateVersionId, EcrCode.Create("SHEETRC14"), Name("Sheet 2"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync();

        var table2 = new TableDef(
            sheet2.Id, EcrCode.Create("TBLRC14"), Name("Table 2"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync();

        var column2 = new ColumnDef(table2.Id, EcrCode.Create("C1_RC14"), Name("Col 1"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column2);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2Id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(
            document.PeriodKey, instance2Id, document.DocumentId, table2.Id, Now));
        await db.SaveChangesAsync();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var versions = new Dictionary<int, (int VersionId, string Output)>();
        foreach (var (key, tableDefId, columnDefId) in new[]
                 {
                     ("A", document.TableDefId, document.ColumnDefIds[1]),
                     ("B", table2.Id, column2.Id),
                 })
        {
            var methodology = new Methodology(EcrCode.Create($"RC14{key}_{tag}"), Name("m"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var output = "tons" + key;
            db.CalculationBindings.Add(new CalculationBinding(tableDefId, columnDefId, methodology.Id, output, "{}"));

            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            versions[methodology.Id] = (version.Id, output);
        }

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        var arranged = new Arranged(builder, document, sheet2.Id, versions, unitId);
        if (submitSecondSheet)
        {
            await SubmitSecondSheetAsync(arranged);
        }

        return arranged;
    }

    private static async Task SubmitSecondSheetAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();
        var state = new ApprovalState(
            arranged.Document.DocumentId, arranged.SecondSheetId, arranged.Document.PeriodKey.Value);
        state.Submit(userId: 5, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static async Task RunJobAsync(Arranged arranged, RecalculationRequest request, decimal label)
    {
        await using var db = arranged.Builder.CreateContext();
        var clock = new TestClock(Now);

        var job = new RecalculationJob(
            db,
            new WritingRunner(arranged, label),
            RunHandler(db, clock),
            FormulaService(),
            clock);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);
    }

    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    private sealed record Arranged(
        TestDocumentBuilder Builder,
        TestDocument Document,
        int SecondSheetId,
        Dictionary<int, (int VersionId, string Output)> Versions,
        int UnitId);

    /// <summary>
    /// Фейк оркестратора: на кожну прив'язку пише ОДИН вихід її методології (<c>tonsA</c>/<c>tonsB</c>)
    /// зі штампом прогону через справжнє сховище, власним контекстом.
    /// </summary>
    private sealed class WritingRunner(Arranged arranged, decimal label) : ICalculationRunner
    {
        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            if (bindings.Count == 0)
            {
                return new ModuleProfile();
            }

            await using var db = arranged.Builder.CreateContext();
            var store = new CalculationResultStore(db, new TestClock(Now));

            var outputs = bindings
                .DistinctBy(b => b.MethodologyId)
                .Select(b =>
                {
                    var (versionId, output) = arranged.Versions[b.MethodologyId];
                    return new CalculationOutput(documentId, "R-1",
                    [
                        new CalculationOutputValue(versionId, null, output, label + periodKey.Sequence, arranged.UnitId),
                    ], []);
                })
                .ToList();

            await store.WriteResultsAsync(calculationRunId, outputs, ct);
            await db.SaveChangesAsync(ct);

            return new ModuleProfile();
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
