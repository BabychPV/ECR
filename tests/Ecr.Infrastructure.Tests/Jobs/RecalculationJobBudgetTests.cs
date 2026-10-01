// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobBudgetTests.cs
using System.Diagnostics.Metrics;
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
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ПРД-13 (НФ-8.6.4): задача перерахунку сама міряє свою тривалість — обидва виконавці
/// (<c>QuartzJobAdapter</c> у Api і <c>JobWorker</c> в Ecr.Worker) кличуть один і той самий
/// <c>RecalculationJob.ExecuteAsync</c>, тож вимір стоїть саме в ньому.
/// </summary>
/// <remarks>
/// ⚠ Час — через <see cref="TestClock"/>, який просуває підставний оркестратор: перевіряється
/// саме різниця годинника задачі, а не реальна тривалість (600 с у тесті не чекаємо).
/// Мутації, на яких тести червоніють: прибрати блок <c>budget.ObserveAsync</c> наприкінці
/// <c>RecalculationJob.ExecuteAsync</c> — червоні всі, крім тесту провалу; перенести його в
/// <c>finally</c> — червоний <see cref="Провалена_задача_не_дає_ні_виміру_ні_сигналу"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobBudgetTests(SqlServerFixture sql)
{
    private const int MethodologyId = 812;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Річна_задача_довша_за_поріг_пише_гістограму_Warning_і_конверт()
    {
        var (builder, document) = await ArrangeAsync();
        var logger = new ListLogger();
        using var capture = new Capture(document.ProjectId);
        var progress = new RecordingProgress();

        await RunAsync(builder, document, periodKey: null, advance: TimeSpan.FromSeconds(700), logger, progress);

        var measurement = Assert.Single(capture.Values);
        Assert.Equal(700d, measurement.Seconds);
        Assert.Equal("Database", measurement.Mode);

        Assert.Single(logger.Warnings);
        var (_, message) = progress.Reports[^1];
        Assert.True(JobProgressMessageCodec.TryDecode(message, out var envelope));
        Assert.Equal(RecalculationBudgetMonitor.OverBudgetKey, envelope.Key);
        Assert.Equal("700", envelope.Params!["seconds"]);
        Assert.Equal("600", envelope.Params["limit"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Річна_задача_у_межах_бюджету_пише_гістограму_але_не_сигналить()
    {
        var (builder, document) = await ArrangeAsync();
        var logger = new ListLogger();
        using var capture = new Capture(document.ProjectId);
        var progress = new RecordingProgress();

        await RunAsync(builder, document, periodKey: null, advance: TimeSpan.FromSeconds(154), logger, progress);

        Assert.Equal(154d, Assert.Single(capture.Values).Seconds);
        Assert.Empty(logger.Warnings);
        Assert.DoesNotContain(progress.Reports, r => r.Message?.Contains(RecalculationBudgetMonitor.OverBudgetKey, StringComparison.Ordinal) == true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Задача_названого_періоду_довша_за_поріг_сигналить_але_не_пише_річну_гістограму()
    {
        var (builder, document) = await ArrangeAsync();
        var logger = new ListLogger();
        using var capture = new Capture(document.ProjectId);
        var progress = new RecordingProgress();

        await RunAsync(builder, document, document.PeriodKey.Value, TimeSpan.FromSeconds(700), logger, progress);

        Assert.Empty(capture.Values);
        Assert.Single(logger.Warnings);
        Assert.Contains(progress.Reports, r => r.Message?.Contains(RecalculationBudgetMonitor.OverBudgetKey, StringComparison.Ordinal) == true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Провалена_задача_не_дає_ні_виміру_ні_сигналу()
    {
        var (builder, document) = await ArrangeAsync();
        var logger = new ListLogger();
        using var capture = new Capture(document.ProjectId);
        var progress = new RecordingProgress();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            builder, document, periodKey: null, advance: TimeSpan.FromSeconds(700), logger, progress, fail: true));

        Assert.Empty(capture.Values);
        Assert.Empty(logger.Warnings);
    }

    private async Task RunAsync(
        TestDocumentBuilder builder, TestDocument document, int? periodKey, TimeSpan advance,
        ListLogger logger, RecordingProgress progress, bool fail = false)
    {
        var clock = new TestClock(new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));
        using var monitor = new RecalculationBudgetMonitor(
            new RecalculationBudgetOptions(TimeSpan.FromSeconds(600), "Database"), logger);

        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, new ClockAdvancingRunner(clock, advance, fail), RunHandler(), FormulaService(), clock, jobs: null, monitor);

        await job.ExecuteAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, periodKey, TriggeredByUserId: null),
            progress,
            CancellationToken.None);
    }

    private async Task<(TestDocumentBuilder Builder, TestDocument Document)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        // Прив'язка потрібна, щоб оркестратор (підставний) справді викликався для періоду.
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

    /// <summary>Оркестратор, що «рахує» стільки, на скільки просуває годинник задачі.</summary>
    private sealed class ClockAdvancingRunner(TestClock clock, TimeSpan advance, bool fail) : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            clock.Advance(advance);

            return fail
                ? throw new InvalidOperationException("оркестратор зламався")
                : Task.FromResult(new ModuleProfile());
        }
    }

    private sealed record Sample(double Seconds, string? Mode);

    /// <summary>Слухач гістограми <c>ecr.calc.full_year</c> лише для одного проєкту (тести йдуть паралельно).</summary>
    private sealed class Capture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly object gate = new();
        private readonly List<Sample> values = [];

        public Capture(int project)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RecalculationBudgetMonitor.MeterName
                    && instrument.Name == RecalculationBudgetMonitor.InstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                var mine = false;
                string? mode = null;
                foreach (var tag in tags)
                {
                    mine |= tag is { Key: "project", Value: int p } && p == project;
                    mode = tag is { Key: "mode", Value: string m } ? m : mode;
                }

                if (mine)
                {
                    lock (gate)
                    {
                        values.Add(new Sample(value, mode));
                    }
                }
            });

            listener.Start();
        }

        public IReadOnlyList<Sample> Values
        {
            get
            {
                lock (gate)
                {
                    return [.. values];
                }
            }
        }

        public void Dispose() => listener.Dispose();
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

    internal sealed class ListLogger : ILogger<RecalculationBudgetMonitor>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
