// tests/Ecr.Worker.Tests/ChildTelemetryTests.cs

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Observability;
using Ecr.TestKit;
using Ecr.Worker.Child;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// Метрики дочірнього воркера: перерахунок виконується в ньому, тож без власного експорту
/// <c>ecr.job.failed</c>, <c>ecr.job.start_latency</c> і кеші зникали (аудит 2026-10-01, P1).
/// </summary>
public sealed class ChildTelemetryTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Вимкнено_нуль_реєстрацій_експорту_але_адаптер_старту_є()
    {
        using var provider = Build(("Telemetry:Enabled", "false"));

        Assert.Null(provider.GetService<MeterProvider>());
        Assert.False(ChildTelemetry.Flush(provider));
        Assert.IsType<ChildTelemetry.ChildJobStartMetrics>(provider.GetRequiredService<IJobStartMetrics>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Адаптер_старту_пише_ecr_job_start_latency_з_тегами_job_і_lane()
    {
        var seen = new List<(double Value, string? Job, string? Lane)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == InfrastructureMetrics.MeterName && i.Name == ChildTelemetry.ChildJobStartMetrics.InstrumentName)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, v, tags, _) =>
        {
            string? job = null, lane = null;
            foreach (var t in tags)
            {
                if (t.Key == "job")
                {
                    job = t.Value as string;
                }

                if (t.Key == "lane")
                {
                    lane = t.Value as string;
                }
            }

            seen.Add((v, job, lane));
        });
        listener.Start();

        using var provider = Build(("Telemetry:Enabled", "false"));
        var metrics = provider.GetRequiredService<IJobStartMetrics>();
        metrics.RecordStartLatency(42, "recalc", "recalc");
        metrics.RecordStartLatency(7, "nightly");

        Assert.Contains(seen, s => s is { Value: 42, Job: "recalc", Lane: "recalc" });
        Assert.Contains(seen, s => s is { Value: 7, Job: "nightly", Lane: null });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Увімкнено_метрики_дочірнього_долітають_до_колектора_після_Flush()
    {
        var port = FreePort();
        using var collector = new HttpListener();
        collector.Prefixes.Add($"http://localhost:{port}/");
        collector.Start();
        var received = Task.Run(async () =>
        {
            var bodies = new List<byte[]>();
            while (collector.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await collector.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }

                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                lock (bodies)
                {
                    bodies.Add(ms.ToArray());
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-protobuf";
                ctx.Response.Close();
                if (ctx.Request.Url!.AbsolutePath != "/v1/metrics")
                {
                    return new[] { Encoding.UTF8.GetBytes("wrong-path:" + ctx.Request.Url.AbsolutePath) };
                }
            }

            lock (bodies)
            {
                return bodies.ToArray();
            }
        });

        using (var provider = Build(
                   ("Telemetry:Enabled", "true"),
                   ("Telemetry:OtlpEndpoint", $"http://localhost:{port}"),
                   ("Telemetry:OtlpProtocol", "HttpProtobuf"),
                   ("Telemetry:ExportIntervalSeconds", "3600")))
        {
            // Провайдер створюється при першому зверненні (у хості — стартовою службою).
            Assert.NotNull(provider.GetRequiredService<MeterProvider>());

            InfrastructureMetrics.RecordJobFailed("recalc", "error");
            ChildTelemetry.Flush(provider, 10_000);
        }

        // Dispose провайдера теж скидає, тож колектор міг отримати кілька POST-ів.
        await Task.Delay(300);
        collector.Stop();
        var bodies = await received.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEmpty(bodies);
        Assert.DoesNotContain(bodies, b => Encoding.UTF8.GetString(b).StartsWith("wrong-path", StringComparison.Ordinal));
        Assert.Contains(bodies, b => Encoding.UTF8.GetString(b).Contains("ecr.job.failed", StringComparison.Ordinal));
        Assert.Contains(bodies, b => Encoding.UTF8.GetString(b).Contains("ecr-worker", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Недійсна_адреса_не_валить_дочірній_експорт_мовчки_вимкнено()
    {
        using var provider = Build(("Telemetry:Enabled", "true"), ("Telemetry:OtlpEndpoint", "not a uri"));

        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Дочірня_композиція_реєструє_метрики()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ecr"] = "Server=.;Database=EcrChildCompositionProbe;Integrated Security=true",
                ["Telemetry:Enabled"] = "true",
                ["Telemetry:OtlpEndpoint"] = "http://localhost:4317",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        ChildComposition.AddChildWorker(services, configuration, new Isolation.WorkerPoolOptions());

        Assert.Contains(services, d => d.ServiceType == typeof(IJobStartMetrics));
        Assert.Contains(services, d => d.ServiceType == typeof(MeterProvider));
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChildTelemetry(configuration);
        return services.BuildServiceProvider();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
