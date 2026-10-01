// tests/Ecr.Api.Tests/JobStartLatencyMetricTests.cs
using System.Diagnostics.Metrics;
using Ecr.Api.Observability;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>ecr.job.start_latency</c> (<c>ФВ-12.2</c>) справді пише значення з тегами
/// <c>job</c> і (для черги в базі) <c>lane</c>; для Quartz тега <c>lane</c> немає.
/// </summary>
public sealed class JobStartLatencyMetricTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Затримка_старту_пишеться_з_тегами_job_і_lane()
    {
        var code = $"Ecr.Test.Latency.{Guid.NewGuid():N}";
        var seen = Measure(metrics =>
        {
            new JobStartMetricsAdapter(metrics).RecordStartLatency(1234d, code, "recalc");
        }, code);

        var one = Assert.Single(seen);
        Assert.Equal(1234d, one.Value);
        Assert.Equal("recalc", one.Tags["lane"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Без_лейна_тег_lane_омінається_а_не_підставляється()
    {
        var code = $"Ecr.Test.Latency.{Guid.NewGuid():N}";
        var seen = Measure(metrics => new JobStartMetricsAdapter(metrics).RecordStartLatency(50d, code), code);

        var one = Assert.Single(seen);
        Assert.False(one.Tags.ContainsKey("lane"));
        Assert.Equal(code, one.Tags["job"]);
    }

    private static List<(double Value, Dictionary<string, object?> Tags)> Measure(
        Action<EcrMetrics> act, string jobCode)
    {
        var seen = new List<(double, Dictionary<string, object?>)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == EcrMetrics.JobStartLatency)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            if (Equals(map.GetValueOrDefault("job"), jobCode))
            {
                lock (seen)
                {
                    seen.Add((value, map));
                }
            }
        });
        listener.Start();

        using var factory = new MeterFactory();
        act(new EcrMetrics(factory));
        return seen;
    }

    private sealed class MeterFactory : IMeterFactory
    {
        private readonly List<Meter> meters = [];

        public Meter Create(MeterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var meter = new Meter(options.Name, options.Version);
            meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in meters)
            {
                meter.Dispose();
            }
        }
    }
}