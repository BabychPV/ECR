using System.Diagnostics.Metrics;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ФВ-0.2: коли діапазон формули ОПУБЛІКОВАНОЇ версії з графом доводиться розкривати за поточним порядком
/// (у збереженому графі немає пробігу для посилання), це видно — попередження в журналі й лічильник
/// <c>ecr.recalc.range_fallback</c>, а не тиха підміна числа.
/// </summary>
/// <remarks>
/// ⚠ Чернетка (граф порожній) — норма, а не збій: попередження немає. Попередження — рівно для версії, у
/// якої граф є, а пробіг цього посилання в ньому загубився.
/// <para>
/// Мутація: прибрати виклик <c>NoteRangeFallback</c> у <c>SliceEvaluationContext.Read</c> — перший тест
/// червоний (немає ні запису, ні приросту лічильника).
/// </para>
/// </remarks>
public sealed class RangeFallbackWarningTests
{
    private const string Expression = "SUM([Main].[7001001:7001003].[Jan]) + [Main].[7001004].[Jan]";

    private static readonly PeriodContext Period =
        new(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CalendarMode.Actual, 2026, 1);

    private static readonly UnitCatalogSnapshot NoUnits = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>());

    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Граф_без_пробігу_діапазону_пише_попередження_і_лічильник()
    {
        var f = Arrange();

        // Граф версії лишився, але ребра діапазону (SortOrder 0..2) у ньому немає — лише одиничне посилання.
        var partial = f.Published.Where(d => d.SortOrder == 3).ToList();
        Assert.Single(partial);

        var (result, log, counter) = Evaluate(f, partial);

        // Запасний шлях рахує за поточним порядком — число те саме, що й було б без графа.
        Assert.Equal(60m + 999m, result);
        Assert.Equal(1, counter);

        var entry = Assert.Single(log);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("7001001", entry.Message, StringComparison.Ordinal);
        Assert.Contains("7001003", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Повний_граф_не_дає_ні_попередження_ні_лічильника()
    {
        var f = Arrange();

        var (result, log, counter) = Evaluate(f, f.Published);

        Assert.Equal(60m + 999m, result);
        Assert.Equal(0, counter);
        Assert.Empty(log);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Чернетка_без_графа_не_дає_попередження()
    {
        var f = Arrange();

        var (result, log, counter) = Evaluate(f, []);

        Assert.Equal(60m + 999m, result);
        Assert.Equal(0, counter);
        Assert.Empty(log);
    }

    private sealed record Fixture(
        TableDef Table,
        FormulaDef Formula,
        TemplateVersionSnapshot Snapshot,
        IReadOnlyList<FormulaDependency> Published,
        Dictionary<CellKey, ExpressionValue> Values);

    private Fixture Arrange()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var jan = builder.Column(table, "Jan");
        var total = builder.Column(table, "Total");
        foreach (var (key, ordinal) in new[] { ("7001001", 1), ("7001002", 2), ("7001003", 3), ("7001004", 4) })
        {
            builder.Row(table, key, ordinal);
        }

        var formula = builder.Formula(table, Expression, column: total);

        var published = PublishChecks.Dependencies(builder.Version(), _engine);
        var snapshot = builder.Build();

        var values = new Dictionary<CellKey, ExpressionValue>();
        foreach (var (key, amount) in new[] { ("7001001", 10m), ("7001002", 20m), ("7001003", 30m), ("7001004", 999m) })
        {
            values[new CellKey(0, table.Id, key, jan.Id)] = ExpressionValue.Number(amount);
        }

        return new Fixture(table, formula, snapshot, published, values);
    }

    private (decimal Result, List<LogEntry> Log, long Counter) Evaluate(
        Fixture f, IReadOnlyList<FormulaDependency> graph)
    {
        long counter = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RecalculationMetrics.MeterName
                && instrument.Name == RecalculationMetrics.RangeFallback)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref counter, measurement));
        listener.Start();

        var logger = new ListLogger();
        var context = new SliceEvaluationContext(
            f.Snapshot, f.Values, new Dictionary<string, ExpressionValue>(), Period, NoUnits,
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            formulaDependencies: graph,
            logger: logger)
        {
            CurrentTableDefId = f.Table.Id,
        };

        var parsed = _engine.Parse(f.Formula.Expression, ExpressionDialect.Template);
        Assert.True(parsed.IsSuccess);
        context.UseFormula(f.Formula, parsed.Expression!.Root);

        var value = _engine.Evaluate(parsed.Expression, context).Value;
        Assert.False(value.IsError);

        var result = Convert.ToDecimal(value.Value, System.Globalization.CultureInfo.InvariantCulture);
        listener.Dispose();

        return (result, logger.Entries, Interlocked.Read(ref counter));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
