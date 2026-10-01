using System.Diagnostics.Metrics;
using Ecr.Infrastructure.Observability;

namespace Ecr.Infrastructure.Tests;

/// <summary>
/// Реальний <see cref="MeterListener"/> на статичних лічильниках <see cref="InfrastructureMetrics"/>.
/// Вимірювання фільтруються або за кодом задачі, або за <see cref="AsyncLocal{T}"/>-міткою
/// потоку виконання: лічильники спільні на процес, а тести йдуть паралельно.
/// </summary>
internal sealed class InfrastructureMetricsCapture : IDisposable
{
    private static readonly AsyncLocal<object?> Flow = new();

    private readonly MeterListener listener = new();
    private readonly object gate = new();
    private readonly List<(string Instrument, Dictionary<string, object?> Tags)> seen = [];
    private readonly object flowMark = new();
    private readonly string? jobCode;

    /// <param name="jobCode">Якщо задано — беруться лише вимірювання цього коду задачі; інакше — лише з поточного потоку виконання.</param>
    public InfrastructureMetricsCapture(string? jobCode = null)
    {
        this.jobCode = jobCode;
        Flow.Value = flowMark;

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == InfrastructureMetrics.MeterName
                && instrument.Name is InfrastructureMetrics.JobFailed
                    or InfrastructureMetrics.CacheHit or InfrastructureMetrics.CacheMiss
                    or InfrastructureMetrics.AccessProfileBuild or InfrastructureMetrics.JobRunDuration)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            var mine = jobCode is null
                ? ReferenceEquals(Flow.Value, flowMark)
                : Equals(map.GetValueOrDefault("job"), jobCode);
            if (mine && value == 1)
            {
                lock (gate)
                {
                    seen.Add((instrument.Name, map));
                }
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var map = new Dictionary<string, object?> { ["value"] = value };
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            var mine = jobCode is null
                ? ReferenceEquals(Flow.Value, flowMark)
                : Equals(map.GetValueOrDefault("job"), jobCode);
            if (mine)
            {
                lock (gate)
                {
                    seen.Add((instrument.Name, map));
                }
            }
        });
        listener.Start();
    }

    public List<Dictionary<string, object?>> Of(string instrument)
    {
        lock (gate)
        {
            return seen.Where(s => s.Instrument == instrument).Select(s => s.Tags).ToList();
        }
    }

    public void Dispose()
    {
        listener.Dispose();
        _ = jobCode;
    }
}
