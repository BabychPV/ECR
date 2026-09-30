// tests/Ecr.Api.Tests/Startup/TelemetrySetupTests.cs

using Ecr.Api.Observability;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// Експорт метрик OTLP (<c>U17</c>): вимкнено — у контейнері нуль від
/// OpenTelemetry; увімкнено — є <see cref="MeterProvider"/>.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини: вимкнений експорт не має навантажувати сервер. Тому тест
/// «вимкнено» перевіряє не «експортер не шле», а що НІЧОГО не зареєстровано:
/// ні MeterProvider, ні рідера, ні жодного типу з простору <c>OpenTelemetry</c>.
/// </remarks>
public sealed class TelemetrySetupTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("yes")]
    public void Вимкнено_нуль_реєстрацій_OpenTelemetry(string? enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var before = services.Count;

        var values = new List<KeyValuePair<string, string?>>
        {
            // Адреса задана навмисно: вимикач, а не порожня адреса, має
            // прибирати експорт.
            new("Telemetry:OtlpEndpoint", "http://127.0.0.1:4317"),
        };
        if (enabled is not null)
        {
            values.Add(new("Telemetry:Enabled", enabled));
        }

        services.AddEcrTelemetry(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(services, IsOpenTelemetry);

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    public void Увімкнено_з_адресою_MeterProvider_є()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddEcrTelemetry(Config(
            ("Telemetry:Enabled", "true"),
            ("Telemetry:OtlpEndpoint", "http://127.0.0.1:4317"),
            ("Telemetry:ExportIntervalSeconds", "30")));

        Assert.Contains(services, IsOpenTelemetry);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<MeterProvider>());
    }

    /// <remarks>
    /// Опції читаються тим самим шляхом, що й бібліотека в <c>AddOtlpExporter()</c>:
    /// <c>IOptionsFactory&lt;OtlpExporterOptions&gt;.Create(Options.DefaultName)</c>
    /// і <c>IOptionsMonitor&lt;MetricReaderOptions&gt;.Get(Options.DefaultName)</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    [InlineData(null, "http://collector:4317", OtlpExportProtocol.Grpc, "http://collector:4317/")]
    [InlineData("Grpc", "http://collector:4317", OtlpExportProtocol.Grpc, "http://collector:4317/")]
    [InlineData("HttpProtobuf", "http://collector:4318", OtlpExportProtocol.HttpProtobuf, "http://collector:4318/v1/metrics")]
    [InlineData("httpprotobuf", "https://collector:4318/custom/metrics", OtlpExportProtocol.HttpProtobuf, "https://collector:4318/custom/metrics")]
    public void Протокол_адреса_й_інтервал_доходять_до_опцій_експортера(
        string? protocol, string endpoint, OtlpExportProtocol expectedProtocol, string expectedEndpoint)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var values = new List<(string Key, string Value)>
        {
            ("Telemetry:Enabled", "true"),
            ("Telemetry:OtlpEndpoint", endpoint),
            ("Telemetry:ExportIntervalSeconds", "30"),
        };
        if (protocol is not null)
        {
            values.Add(("Telemetry:OtlpProtocol", protocol));
        }

        services.AddEcrTelemetry(Config([.. values]));

        using var provider = services.BuildServiceProvider();
        var exporter = provider.GetRequiredService<IOptionsFactory<OtlpExporterOptions>>().Create(Microsoft.Extensions.Options.Options.DefaultName);
        var reader = provider.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(Microsoft.Extensions.Options.Options.DefaultName);

        Assert.Equal(expectedProtocol, exporter.Protocol);
        Assert.Equal(new Uri(expectedEndpoint), exporter.Endpoint);
        Assert.Equal(30_000, reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    public void Експортуються_всі_Meter_и_ECR()
    {
        Assert.Contains(EcrMetrics.MeterName, TelemetrySetup.MeterNames);
    }

    private static bool IsOpenTelemetry(ServiceDescriptor d)
        => InOpenTelemetry(d.ServiceType)
           || InOpenTelemetry(d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)
           || InOpenTelemetry(d.IsKeyedService ? d.KeyedImplementationInstance?.GetType() : d.ImplementationInstance?.GetType())
           || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Any(InOpenTelemetry));

    private static bool InOpenTelemetry(Type? type)
        => type?.Namespace is { } ns && ns.StartsWith("OpenTelemetry", StringComparison.Ordinal);

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}

/// <summary>
/// Справжній <c>Program.cs</c>: вимикач читається там, де реєструються служби.
/// </summary>
[Collection("SqlServer")]
public sealed class TelemetryProgramTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "U17")]
    public void Застосунок_за_замовчуванням_без_MeterProvider_а_увімкнений_з_ним()
    {
        using var app = new EcrApiFactory(sql);
        Assert.Null(app.Services.GetService<MeterProvider>());

        // ⚠ Порт 1: колектора немає, з'єднання відхиляється одразу — тест не
        // чекає таймауту експорту на звільненні.
        using var enabled = app.WithWebHostBuilder(b => b
            .UseSetting("Telemetry:Enabled", "true")
            .UseSetting("Telemetry:OtlpEndpoint", "http://127.0.0.1:1"));
        Assert.NotNull(enabled.Services.GetService<MeterProvider>());
    }
}
