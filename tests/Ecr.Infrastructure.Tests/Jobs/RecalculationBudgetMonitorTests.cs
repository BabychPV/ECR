// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationBudgetMonitorTests.cs
using System.Diagnostics.Metrics;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ПРД-13 (НФ-8.6.4): бюджет повного річного перерахунку (600 с) вимірюється й дає сигнал.
/// До цього гістограма <c>ecr.calc.full_year</c> була оголошена в <c>EcrMetrics</c> без жодного
/// викликача, а перевищення бюджету не бачив ніхто — ні на графіку, ні в журналі.
/// </summary>
/// <remarks>
/// Мутації, на яких тести червоніють (прогнано на чистій вершині — типу
/// <c>RecalculationBudgetMonitor</c> там немає, тест не збирається; після введення класу):
/// <list type="bullet">
/// <item>прибрати <c>fullYearHistogram.Record</c> — <see cref="Повний_рік_пише_гістограму_з_тегами_проєкту_і_режиму"/>;</item>
/// <item><c>elapsed &lt;= WarnAfter</c> → <c>elapsed &lt; WarnAfter</c> — <see cref="Рівно_на_порозі_не_сигналить_а_на_секунду_довше_сигналить"/>;</item>
/// <item>не писати конверт у прогрес — <see cref="Перевищення_лишає_Warning_у_журналі_і_конверт_у_прогресі"/>;</item>
/// <item>писати гістограму для поперіодної задачі — <see cref="Поперіодна_задача_не_пише_гістограму_річного_перерахунку"/>.</item>
/// </list>
/// </remarks>
public sealed class RecalculationBudgetMonitorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(600);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Повний_рік_пише_гістограму_з_тегами_проєкту_і_режиму()
    {
        using var monitor = Monitor("Database");
        using var capture = new Capture(project: 91001);
        var progress = new RecordingProgress();

        var over = await monitor.ObserveAsync(
            91001, documentId: 5, fullYear: true, TimeSpan.FromSeconds(154), progress, CancellationToken.None);

        Assert.False(over);
        var measurement = Assert.Single(capture.Measurements);
        Assert.Equal(154d, measurement.Value);
        Assert.Equal(91001, measurement.Project);
        Assert.Equal("Database", measurement.Mode);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Поперіодна_задача_не_пише_гістограму_річного_перерахунку()
    {
        using var monitor = Monitor("Quartz");
        using var capture = new Capture(project: 91002);

        await monitor.ObserveAsync(
            91002, documentId: 5, fullYear: false, TimeSpan.FromSeconds(3), new RecordingProgress(), CancellationToken.None);

        Assert.Empty(capture.Measurements);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Рівно_на_порозі_не_сигналить_а_на_секунду_довше_сигналить()
    {
        using var monitor = Monitor("Quartz");
        var atLimit = new RecordingProgress();
        var pastLimit = new RecordingProgress();

        Assert.False(await monitor.ObserveAsync(
            91003, 5, fullYear: true, Budget, atLimit, CancellationToken.None));
        Assert.True(await monitor.ObserveAsync(
            91003, 5, fullYear: true, Budget + TimeSpan.FromSeconds(1), pastLimit, CancellationToken.None));

        Assert.Empty(atLimit.Reports);
        Assert.Single(pastLimit.Reports);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Перевищення_лишає_Warning_у_журналі_і_конверт_у_прогресі()
    {
        var logger = new ListLogger<RecalculationBudgetMonitor>();
        using var monitor = new RecalculationBudgetMonitor(new RecalculationBudgetOptions(Budget, "Database"), logger);
        var progress = new RecordingProgress();

        await monitor.ObserveAsync(
            91004, documentId: 77, fullYear: true, TimeSpan.FromSeconds(720), progress, CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(RecalculationBudgetMonitor.OverBudgetKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains("720", warning.Message, StringComparison.Ordinal);
        Assert.Contains("600", warning.Message, StringComparison.Ordinal);

        var (percent, message) = Assert.Single(progress.Reports);
        Assert.Equal(100, percent);
        Assert.True(JobProgressMessageCodec.TryDecode(message, out var envelope));
        Assert.Equal(RecalculationBudgetMonitor.OverBudgetKey, envelope.Key);
        Assert.Equal("720", envelope.Params!["seconds"]);
        Assert.Equal("600", envelope.Params["limit"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Під_порогом_немає_ні_Warning_ні_конверта()
    {
        var logger = new ListLogger<RecalculationBudgetMonitor>();
        using var monitor = new RecalculationBudgetMonitor(new RecalculationBudgetOptions(Budget, "Quartz"), logger);
        var progress = new RecordingProgress();

        await monitor.ObserveAsync(
            91005, 77, fullYear: true, TimeSpan.FromSeconds(599), progress, CancellationToken.None);

        Assert.Empty(logger.Entries);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Збій_запису_прогресу_не_валить_завершений_перерахунок()
    {
        var logger = new ListLogger<RecalculationBudgetMonitor>();
        using var monitor = new RecalculationBudgetMonitor(new RecalculationBudgetOptions(Budget, "Quartz"), logger);

        var over = await monitor.ObserveAsync(
            91006, 77, fullYear: true, TimeSpan.FromSeconds(900), new ThrowingProgress(), CancellationToken.None);

        Assert.True(over);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    [InlineData(null, 600)]
    [InlineData("", 600)]
    [InlineData("90", 90)]
    [InlineData("0", 600)]
    [InlineData("-5", 600)]
    [InlineData("10 хв", 600)]
    public void Поріг_береться_з_Calculations_FullYearWarnSeconds_а_недійсне_дає_дефолт_600(string? value, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RecalculationBudgetOptions.WarnSecondsKey] = value })
            .Build();

        Assert.Equal(TimeSpan.FromSeconds(expected), RecalculationBudgetOptions.Read(configuration).WarnAfter);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ПРД-13")]
    [InlineData(null, "Quartz")]
    [InlineData("Database", "Database")]
    public void Режим_тега_береться_з_Jobs_Queue_Mode(string? mode, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DbBackgroundJobScheduler.ModeKey] = mode })
            .Build();

        Assert.Equal(expected, RecalculationBudgetOptions.Read(configuration).Mode);
    }

    private static RecalculationBudgetMonitor Monitor(string mode)
        => new(new RecalculationBudgetOptions(Budget, mode), new ListLogger<RecalculationBudgetMonitor>());

    /// <summary>Слухач Meter «Ecr»: лише вимірювання гістограми повного року одного проєкту.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly object gate = new();
        private readonly List<Measurement> measurements = [];

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
                int? seen = null;
                string? mode = null;
                foreach (var tag in tags)
                {
                    if (tag is { Key: "project", Value: int p })
                    {
                        seen = p;
                    }
                    else if (tag is { Key: "mode", Value: string m })
                    {
                        mode = m;
                    }
                }

                // Глобальний слухач бачить і чужі паралельні тести — лише свій проєкт.
                if (seen == project)
                {
                    lock (gate)
                    {
                        measurements.Add(new Measurement(value, project, mode));
                    }
                }
            });

            listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (gate)
                {
                    return [.. measurements];
                }
            }
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed record Measurement(double Value, int Project, string? Mode);

    private sealed class RecordingProgress : IJobProgress
    {
        public List<(int Percent, string? Message)> Reports { get; } = [];

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Reports.Add((percent, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct)
            => throw new InvalidOperationException("база недоступна");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
