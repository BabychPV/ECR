// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobPartialTableDiagnosticsTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
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
/// Live 11.11 (doc 96, рядки LTD-A26/A35): перерахунок аркуша рахує спільну методологію S і на таблиці ІНШОГО аркуша,
/// де друга методологія X лише переноситься з попереднього прогону. Рядок, закритий X, прохід бачить «без правила» —
/// хибна діагностика NoMatchingRule. Таблиця, яку прохід бачить не всіма своїми методологіями, діагностики не дає.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>profile.ExcludeTables(partialTables)</c> у <c>RecalculationJob</c> — перший тест
/// червоний (count 2 замість 1). Другий тримає межу: повний перерахунок документа (нічого не переноситься) лишає
/// діагностику обох таблиць.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobPartialTableDiagnosticsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Перерахунок_аркуша_не_дає_рядків_без_правила_у_таблиці_з_перенесеною_методологією()
    {
        var arranged = await ArrangeAsync();
        var progress = new RecordingProgress();

        await RunAsync(arranged, progress, sheetDefId: arranged.Document.SheetDefId);

        var envelope = NoMatchingRule(progress);
        Assert.Equal("1", envelope.Params!["count"]);
        Assert.Equal("1", envelope.Params["rows"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Перерахунок_документа_лишає_діагностику_обох_таблиць()
    {
        var arranged = await ArrangeAsync();
        var progress = new RecordingProgress();

        await RunAsync(arranged, progress, sheetDefId: null);

        Assert.Equal("2", NoMatchingRule(progress).Params!["count"]);
    }

    private static JobProgressMessageEnvelope NoMatchingRule(RecordingProgress progress)
    {
        var (_, message) = progress.Reports.Last(
            r => r.Message?.Contains("jobs.recalcNoMatchingRule", StringComparison.Ordinal) == true);
        Assert.True(JobProgressMessageCodec.TryDecode(message, out var envelope));
        return envelope;
    }

    private static async Task RunAsync(Arranged arranged, RecordingProgress progress, int? sheetDefId)
    {
        var clock = new TestClock(Now);
        await using var db = arranged.Builder.CreateContext();

        // Оркестратор бачить рядок без правила в КОЖНІЙ таблиці, яку йому дали (так рахує UnmatchedRowsOf, коли
        // в таблиці відпрацювала лише частина прив'язок).
        var runner = new UnmatchedPerTableRunner();
        var job = new RecalculationJob(db, runner, RunHandler(), FormulaService(), clock, jobs: null, budget: null);

        await job.ExecuteAsync(
            new RecalculationRequest(
                arranged.Document.ProjectId, arranged.Document.DocumentId, arranged.Document.PeriodKey.Value,
                TriggeredByUserId: 7, SheetDefId: sheetDefId),
            progress,
            CancellationToken.None);

        // Межа тесту: спільна методологія справді рахувалась на обох таблицях — інакше перевіряли б не те.
        Assert.Contains(arranged.Document.TableInstanceId, runner.Instances);
        Assert.Contains(arranged.SecondInstanceId, runner.Instances);
    }

    private async Task<Arranged> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create("SHEETPT2"), Name("Sheet 2"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync();

        var table2 = new TableDef(
            sheet2.Id, EcrCode.Create("TBLPT2"), Name("Table 2"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync();

        var column2 = new ColumnDef(table2.Id, EcrCode.Create("C1_PT2"), Name("Col 1"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column2);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2Id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(document.PeriodKey, instance2Id, document.DocumentId, table2.Id, Now));
        await db.SaveChangesAsync();

        // S — спільна для обох аркушів (як C11_11_02 на S09 і S09A); X — лише на другому аркуші (як C11_11_04).
        var tag = Guid.NewGuid().ToString("N")[..8];
        var shared = new Methodology(EcrCode.Create($"PTS_{tag}"), Name("s"));
        var secondOnly = new Methodology(EcrCode.Create($"PTX_{tag}"), Name("x"));
        db.Methodologies.AddRange(shared, secondOnly);
        await db.SaveChangesAsync();

        db.CalculationBindings.AddRange(
            new CalculationBinding(document.TableDefId, document.ColumnDefIds[1], shared.Id, "tonsS", "{}"),
            new CalculationBinding(table2.Id, column2.Id, shared.Id, "tonsS", "{}"),
            new CalculationBinding(table2.Id, column2.Id, secondOnly.Id, "tonsX", "{}"));
        await db.SaveChangesAsync();

        return new Arranged(builder, document, instance2Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

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

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow),
            Substitute.For<IAuditWriter>());

    private sealed record Arranged(TestDocumentBuilder Builder, TestDocument Document, long SecondInstanceId);

    /// <summary>Оркестратор: рядок 1 без правила в кожному екземплярі таблиці з прив'язок; запам'ятовує екземпляри.</summary>
    private sealed class UnmatchedPerTableRunner : ICalculationRunner
    {
        public HashSet<long> Instances { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            var profile = new ModuleProfile();
            foreach (var instance in bindings.Select(b => b.TableInstanceId).Distinct())
            {
                Instances.Add(instance);
                profile.RecordUnmatched([new UnmatchedRow(instance, 1, "R001", documentId)]);
            }

            profile.RecordEvaluatedTables(Instances);
            return Task.FromResult(profile);
        }
    }

    private sealed class RecordingProgress : IJobProgress
    {
        public List<(int Percent, string? Message)> Reports { get; } = [];

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Reports.Add((percent, message));
            return Task.CompletedTask;
        }
    }
}
