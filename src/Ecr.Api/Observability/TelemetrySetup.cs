// src/Ecr.Api/Observability/TelemetrySetup.cs

using System.Globalization;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Ecr.Api.Observability;

/// <summary>
/// Експорт метрик OpenTelemetry по OTLP (<c>U17</c>, <c>S-12</c>) — лише за
/// явним <c>Telemetry:Enabled = true</c>.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини: «якщо збираємо — добре; передбач відключення через
/// конфігурацію, щоб не навантажувати сервер». Тому вимкнено — це НУЛЬ
/// реєстрацій: ні <c>AddOpenTelemetry</c>, ні <c>MeterProvider</c>, ні
/// експортера, ні фонового рідера. Лічильники <c>Meter "Ecr"</c> при цьому
/// пишуться як і раніше (їх видно через <c>dotnet-counters</c>) — без
/// слухача вони майже нічого не коштують.
///
/// ⚠ Адресу тут не перевіряємо й не кидаємо: порожній чи недійсний
/// <c>Telemetry:OtlpEndpoint</c> за увімкненого експорту зупиняє старт у
/// <c>EcrConfigurationValidation</c> — з Critical у журналі, до бази і до
/// запуску рідера. Виняток тут, до <c>Build()</c>, не мав би ще логера.
///
/// ⚠ Інструментування AspNetCore/Http/Runtime навмисно немає: це окремі
/// пакети, їх не заведено. Експортуються лише власні метрики ECR.
/// </remarks>
public static class TelemetrySetup
{
    /// <summary>Ключ вмикача експорту.</summary>
    public const string EnabledKey = "Telemetry:Enabled";

    /// <summary>Ключ інтервалу експорту, секунди.</summary>
    public const string ExportIntervalKey = "Telemetry:ExportIntervalSeconds";

    /// <summary>Ключ протоколу OTLP: <c>Grpc</c> (дефолт) або <c>HttpProtobuf</c>.</summary>
    public const string ProtocolKey = "Telemetry:OtlpProtocol";

    /// <summary>Інтервал експорту за замовчуванням, секунди.</summary>
    public const int DefaultExportIntervalSeconds = 60;

    /// <summary>Найменший допустимий інтервал експорту, секунди.</summary>
    public const int MinExportIntervalSeconds = 5;

    /// <summary>Усі Meter-и ECR, які йдуть в експорт.</summary>
    /// <remarks>
    /// На 2026-09-29 у <c>src</c> один лічильник — <see cref="EcrMetrics.MeterName"/>.
    /// Новий <c>Meter</c> без рядка тут не експортується.
    /// </remarks>
    public static readonly IReadOnlyList<string> MeterNames = [EcrMetrics.MeterName];

    /// <summary>Чи увімкнено експорт: лише рядок <c>true</c> (без урахування регістру).</summary>
    /// <remarks>
    /// Недійсне значення тут — «вимкнено», а зупиняє старт із назвою ключа
    /// <c>EcrConfigurationValidation</c> (перелік <c>Booleans</c>).
    /// </remarks>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return bool.TryParse(configuration["Telemetry:Enabled"], out var enabled) && enabled;
    }

    /// <summary>Реєструє експорт метрик OTLP, якщо його ввімкнено.</summary>
    /// <param name="services">Контейнер.</param>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public static IServiceCollection AddEcrTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsEnabled(configuration))
        {
            return services;
        }

        var endpoint = configuration["Telemetry:OtlpEndpoint"];
        var serviceName = configuration["Telemetry:ServiceName"] ?? "ecr-api";
        var intervalSeconds = int.TryParse(
            configuration["Telemetry:ExportIntervalSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(parsed, MinExportIntervalSeconds)
            : DefaultExportIntervalSeconds;

        var protocol = Enum.TryParse<OtlpExportProtocol>(
            configuration["Telemetry:OtlpProtocol"]?.Trim(), ignoreCase: true, out var chosen)
            && Enum.IsDefined(chosen)
            ? chosen
            : OtlpExportProtocol.Grpc;

        // ⚠ Через Options, а не лямбдою AddOtlpExporter((e, r) => …): експортер
        // сам бере OtlpExporterOptions/MetricReaderOptions з іменем за
        // замовчуванням, тож налаштування видно в контейнері й перевіряється
        // тестом рівно там, звідки його читає бібліотека. Делегат виконується
        // при побудові MeterProvider — на старті хоста, ПІСЛЯ перевірки
        // конфігурації, яка недійсну адресу вже відхилила.
        services.Configure<OtlpExporterOptions>(exporter =>
        {
            exporter.Protocol = protocol;
            exporter.Endpoint = ExporterEndpoint(endpoint!, protocol);
        });
        services.Configure<MetricReaderOptions>(reader =>
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = intervalSeconds * 1000);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                string.IsNullOrWhiteSpace(serviceName) ? "ecr-api" : serviceName.Trim()))
            .WithMetrics(metrics =>
            {
                foreach (var name in MeterNames)
                {
                    metrics.AddMeter(name);
                }

                metrics.AddOtlpExporter();
            });

        return services;
    }

    /// <summary>Адреса експортера з урахуванням протоколу.</summary>
    /// <remarks>
    /// ⚠ Адресу, задану кодом, бібліотека бере як є. Для <c>HttpProtobuf</c> це
    /// означає, що <c>http://collector:4318</c> без шляху слав би метрики в
    /// корінь колектора, а не в <c>/v1/metrics</c>. Тому шлях дописується, коли
    /// адміністратор дав лише хост і порт; заданий шлях не чіпаємо.
    /// </remarks>
    private static Uri ExporterEndpoint(string endpoint, OtlpExportProtocol protocol)
    {
        var uri = new Uri(endpoint.Trim(), UriKind.Absolute);
        return protocol == OtlpExportProtocol.HttpProtobuf && uri.AbsolutePath == "/"
            ? new Uri(uri, "v1/metrics")
            : uri;
    }
}
