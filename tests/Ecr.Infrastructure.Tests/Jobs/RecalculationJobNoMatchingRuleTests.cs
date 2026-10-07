// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobNoMatchingRuleTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L-4 (Land Demo): рядки без жодного правила видно в повідомленні задачі перерахунку —
/// ключ <c>jobs.recalcNoMatchingRule</c>, кількість і номери рядків (без значень комірок).
/// Задача при цьому завершується успішно: решта рядків порахована.
/// </summary>
/// <remarks>
/// Мутація: прибрати блок звіту про рядки без правила в <c>RecalculationJob.ExecuteAsync</c> —
/// червоний перший тест; прибрати умову «є рядки» — червоний другий.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobNoMatchingRuleTests(SqlServerFixture sql)
{
    private const int MethodologyId = 813;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Рядки_без_правила_потрапляють_у_повідомлення_задачі_з_номерами()
    {
        var (builder, document) = await ArrangeAsync();
        var progress = new RecordingProgress();

        await RunAsync(builder, document, progress, [new UnmatchedRow(500, 2, "R001"), new UnmatchedRow(500, 3, "R002")]);

        var (_, message) = progress.Reports.Last(r => r.Message?.Contains("jobs.recalcNoMatchingRule", StringComparison.Ordinal) == true);
        Assert.True(JobProgressMessageCodec.TryDecode(message, out var envelope));
        Assert.Equal("jobs.recalcNoMatchingRule", envelope.Key);
        Assert.Equal("2", envelope.Params!["count"]);
        Assert.Equal("2, 3", envelope.Params["rows"]);

        // Діагностика не містить значень комірок і ключів рядків.
        Assert.DoesNotContain("R001", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Без_рядків_без_правила_повідомлення_немає()
    {
        var (builder, document) = await ArrangeAsync();
        var progress = new RecordingProgress();

        await RunAsync(builder, document, progress, []);

        Assert.DoesNotContain(
            progress.Reports,
            r => r.Message?.Contains("jobs.recalcNoMatchingRule", StringComparison.Ordinal) == true);
    }

    private static async Task RunAsync(
        TestDocumentBuilder builder, TestDocument document, RecordingProgress progress, IReadOnlyList<UnmatchedRow> unmatched)
    {
        var clock = new TestClock(new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));
        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, new UnmatchedRunner(unmatched), RunHandler(), FormulaService(), clock, jobs: null, budget: null);

        await job.ExecuteAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, document.PeriodKey.Value, TriggeredByUserId: null),
            progress,
            CancellationToken.None);
    }

    private async Task<(TestDocumentBuilder Builder, TestDocument Document)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        db.CalculationBindings.Add(new CalculationBinding(
            document.TableDefId, document.ColumnDefIds[1], MethodologyId, "tons", "{}"));
        await db.SaveChangesAsync();

        return (builder, document);
    }

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
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    /// <summary>Оркестратор, що повертає профіль із заданими рядками без правила.</summary>
    private sealed class UnmatchedRunner(IReadOnlyList<UnmatchedRow> unmatched) : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            var profile = new ModuleProfile();
            profile.RecordUnmatched(unmatched);

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
