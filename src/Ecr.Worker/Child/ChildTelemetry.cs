// src/Ecr.Worker/Child/ChildTelemetry.cs

using System.Diagnostics.Metrics;
using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Ecr.Worker.Child;

/// <summary>
/// Метрики дочірнього воркера (<c>Ecr.Worker --child</c>): той самий Meter <c>Ecr</c> і той самий
/// OTLP-експорт, що в Api, за тими самими ключами <c>Telemetry:*</c>.
/// </summary>
/// <remarks>
/// ⛔ Чому це потрібно. Перерахунок (D-216) виконує ДОЧІРНІЙ процес, а метрики, які емітять
/// <c>JobWorker</c> (<c>ecr.job.failed</c>, <c>ecr.job.start_latency</c>), кеші
/// (<c>ecr.cache.hit/miss</c>) і <c>ecr.access.profile.build</c>, живуть у процесі задачі. Без
/// MeterProvider у дочірньому вони не доходили нікуди — а в Api їх не було, бо задача там не
/// виконувалась.
///
/// ⚠ Конфігурація — <c>worker.settings.json</c> поруч з exe або <c>ECR_Telemetry__*</c>
/// оточення служби (дочірній успадковує оточення наглядача); <c>appsettings.json</c> Api дочірній
/// НЕ читає. Вимкнено (<c>Telemetry:Enabled</c> не <c>true</c>) — нуль реєстрацій.
///
/// ⚠ Дочірній короткоживучий: метрики експортуються періодично, тож до завершення процесу
/// треба скинути буфер — <see cref="Flush"/> на виході (Dispose хоста теж скидає, але явний
/// виклик тримає таймаут під контролем). Убитий Job Object-ом (ліміт пам'яті) процес буфер
/// втрачає — це межа, а не дефект.
/// </remarks>
internal static class ChildTelemetry
{
    /// <summary>Ім'я сервісу за замовчуванням (Api — <c>ecr-api</c>).</summary>
    public const string DefaultServiceName = "ecr-worker";

    private const int DefaultExportIntervalSeconds = 15;
    private const int MinExportIntervalSeconds = 1;

    /// <summary>Реєструє метрики дочірнього; без <c>Telemetry:Enabled = true</c> — лише адаптер старту.</summary>
    public static IServiceCollection AddChildTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Адаптер — завжди: інструмент без слухача майже безкоштовний (як в Api).
        services.AddSingleton<IJobStartMetrics, ChildJobStartMetrics>();

        if (!bool.TryParse(configuration["Telemetry:Enabled"], out var enabled) || !enabled)
        {
            return services;
        }

        var endpoint = configuration["Telemetry:OtlpEndpoint"];
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
        {
            // Дочірній не падає через метрики: задача важливіша за експорт. Api недійсну адресу
            // зупиняє на старті (EcrConfigurationValidation), тож тут це тиха деградація.
            return services;
        }

        var protocol = Enum.TryParse<OtlpExportProtocol>(
            configuration["Telemetry:OtlpProtocol"]?.Trim(), ignoreCase: true, out var chosen)
            && Enum.IsDefined(chosen)
            ? chosen
            : OtlpExportProtocol.Grpc;
        if (protocol == OtlpExportProtocol.HttpProtobuf && uri.AbsolutePath == "/")
        {
            uri = new Uri(uri, "v1/metrics");
        }

        var interval = int.TryParse(
            configuration["Telemetry:ExportIntervalSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(parsed, MinExportIntervalSeconds)
            : DefaultExportIntervalSeconds;

        services.Configure<OtlpExporterOptions>(o =>
        {
            o.Protocol = protocol;
            o.Endpoint = uri;
        });
        services.Configure<MetricReaderOptions>(r =>
            r.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval * 1000);
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(DefaultServiceName))
            .WithMetrics(m => m.AddMeter(Ecr.Infrastructure.Observability.InfrastructureMetrics.MeterName).AddOtlpExporter());
        return services;
    }

    /// <summary>Скидає буфер метрик у колектор перед завершенням процесу.</summary>
    /// <returns><c>false</c>, якщо провайдера немає (експорт вимкнено) або скидання не вкладається в таймаут.</returns>
    public static bool Flush(IServiceProvider provider, int timeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.GetService<MeterProvider>()?.ForceFlush(timeoutMilliseconds) ?? false;
    }

    /// <summary>Затримка старту задачі — той самий інструмент, що <c>EcrMetrics</c> в Api.</summary>
    internal sealed class ChildJobStartMetrics : IJobStartMetrics
    {
        /// <summary>Назва інструмента — та сама, що в Api (<c>EcrMetrics.JobStartLatency</c>).</summary>
        public const string InstrumentName = "ecr.job.start_latency";

        private static readonly Meter Meter = new(Ecr.Infrastructure.Observability.InfrastructureMetrics.MeterName);
        private static readonly Histogram<double> Latency =
            Meter.CreateHistogram<double>(InstrumentName, "ms", "Затримка від постановки задачі в чергу до її старту");

        /// <inheritdoc />
        public void RecordStartLatency(double milliseconds, string jobCode, string? lane = null)
        {
            if (lane is null)
            {
                Latency.Record(milliseconds, new KeyValuePair<string, object?>("job", jobCode));
                return;
            }

            Latency.Record(
                milliseconds,
                new KeyValuePair<string, object?>("job", jobCode),
                new KeyValuePair<string, object?>("lane", lane));
        }
    }
}
