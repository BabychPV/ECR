// src/Ecr.Api/Options/EcrConfigurationValidation.cs

using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Api.Options;

/// <summary>
/// Перевірка конфігурації на старті (<c>U19</c>): недійсне значення зупиняє
/// службу з ім'ям ключа, а не мовчки підміняється дефолтом.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Частина ключів читається так, що друкарська помилка НЕ видна
/// ніде: <c>ReadInt</c> (<c>Database:*</c>, <c>Campaign:AtRiskDays</c>,
/// <c>Integration:CatalogTimeoutSeconds</c>) і <c>Cache:*SlidingMinutes</c>
/// повертають дефолт на <c>"60s"</c>; <c>Auth:StampCacheSeconds</c> — так само;
/// <c>Database:EditionMode = "Enterprse"</c> давав <c>Auto</c>, хоча в проді режим
/// мусить бути заданий явно; будь-що, крім <c>Migrate</c>, у
/// <c>Schema:StartupMode</c> означало <c>Validate</c>. Адміністратор змінював
/// значення — і нічого не відбувалося, без жодного рядка в журналі.
///
/// ⚠ Чому один перелік тут, а не <c>ValidateOnStart()</c> біля кожного читача.
/// Читачі розкидані по чотирьох збірках і більшість із них — не Options, а
/// точкові <c>configuration["…"]</c> на етапі реєстрації. Перетворити їх усі на
/// Options — це правка <c>Ecr.Infrastructure</c> заради форми, а не поведінки.
/// Перелік же звіряється з <c>appsettings.json</c> тестом
/// (<c>EcrConfigurationValidationTests</c>): числовий чи булевий ключ файлу поза
/// переліком червонить збірку.
///
/// ⚠ Порожнє значення — «не задано», а не помилка: так його й трактують читачі
/// (дефолт коду), і так його пише заповнювач інсталятора.
///
/// ⚠ Ключі, які читає <c>GetValue&lt;int&gt;</c> ще на етапі реєстрації
/// (<c>Auth:SlidingHours</c>, <c>Security:RateLimit:*</c>), на недійсному значенні
/// падають раніше за цю перевірку — з повідомленням біндера, де ключ теж названо.
/// У переліку вони лишаються: для них тут перевіряється ще й межа (нуль).
/// </remarks>
public static partial class EcrConfigurationValidation
{
    /// <summary>Цілі ключі та найменше допустиме значення.</summary>
    public static readonly IReadOnlyList<(string Key, int Min)> Integers =
    [
        ("Database:CommandTimeoutSeconds", 0),
        ("Database:BulkBatchSize", 1),
        ("Database:SheetLockTimeoutSeconds", 0),
        ("Cache:MetadataSlidingMinutes", 1),
        ("Cache:AccessProfileSlidingMinutes", 1),
        ("Auth:SlidingHours", 1),
        ("Auth:StampCacheSeconds", 0),
        (Startup.HttpsTransport.PortKey, 0),
        ("Security:RateLimit:LoginPermitPerMinute", 1),
        ("Security:RateLimit:SearchPermit", 1),
        ("Security:RateLimit:SearchWindowSeconds", 1),
        ("Security:RateLimit:CspReportPermitPerMinute", 1),
        ("Jobs:ShutdownTimeoutSeconds", 1),
        ("Audit:ExportMaxRows", 1),
        ("Localization:ImportMaxBytes", 1),
        ("Registries:ImportMaxBytes", 1),
        ("Campaign:AtRiskDays", 0),
        ("Integration:CatalogTimeoutSeconds", 1),
        ("Logging:File:RetainedFiles", 1),
        ("Logging:File:FileSizeLimitMb", 1),

        // ФВ-9.8 (D-205): нуль чи від'ємне — не «без ліміту»; -1 у паралелізмі
        // зняв би межу зовсім.
        ("Calculations:MaxParallelism", 1),
        ("Calculations:MaxInputCellsPerBinding", 1),

        // U17: частіше за 5 с експорт лише навантажує сервер і колектор.
        (Ecr.Api.Observability.TelemetrySetup.ExportIntervalKey, Ecr.Api.Observability.TelemetrySetup.MinExportIntervalSeconds),
    ];

    /// <summary>Булеві ключі: лише <c>true</c> або <c>false</c>.</summary>
    public static readonly IReadOnlyList<string> Booleans =
    [
        "Auth:RequireHttps",
        "Auth:EnableNegotiate",
        "Security:RateLimit:TrustForwardedFor",
        "Security:Csp:ReportOnly",
        "Security:Csp:Enforce",
        "Jobs:NightlyRecalculation:Enabled",
        "Logging:File:Json",
        Ecr.Api.Observability.TelemetrySetup.EnabledKey,
    ];

    /// <summary>Ключі з переліком допустимих значень (без урахування регістру).</summary>
    public static readonly IReadOnlyList<(string Key, string[] Allowed)> Choices =
    [
        ("Schema:StartupMode", ["Validate", "Migrate"]),
        ("Database:EditionMode", Enum.GetNames<SqlEditionMode>()),

        // MI-02 (F1c): «Databse» інакше мовчки лишав би Quartz.
        (Infrastructure.Jobs.DbBackgroundJobScheduler.ModeKey, Enum.GetNames<Infrastructure.Jobs.JobQueueMode>()),
        (Infrastructure.Jobs.JobLaneMap.ExecutorKey, Enum.GetNames<Infrastructure.Jobs.RecalculationExecutor>()),
        (Ecr.Api.Observability.TelemetrySetup.ProtocolKey, Enum.GetNames<OpenTelemetry.Exporter.OtlpExportProtocol>()),
    ];

    /// <summary>Ключ адреси OTLP-колектора.</summary>
    public const string OtlpEndpointKey = "Telemetry:OtlpEndpoint";

    /// <summary>Ключ адреси приймача звітів CSP.</summary>
    public const string CspReportUriKey = "Security:Csp:ReportUri";

    /// <summary>Ключ примусового режиму CSP.</summary>
    public const string CspEnforceKey = "Security:Csp:Enforce";

    /// <summary>Недійсні значення — по рядку на ключ; порожньо, якщо все гаразд.</summary>
    /// <param name="configuration">Зведена конфігурація застосунку.</param>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var problems = new List<string>();

        foreach (var (key, min) in Integers)
        {
            if (Value(configuration, key) is not { } text)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                problems.Add(Describe(key, text, "очікується ціле число"));
            }
            else if (number < min)
            {
                problems.Add(Describe(
                    key, text, string.Create(CultureInfo.InvariantCulture, $"очікується ціле число не менше {min}")));
            }
        }

        foreach (var key in Booleans)
        {
            if (Value(configuration, key) is { } text && !bool.TryParse(text, out _))
            {
                problems.Add(Describe(key, text, "очікується true або false"));
            }
        }

        foreach (var (key, allowed) in Choices)
        {
            if (Value(configuration, key) is { } text
                && !allowed.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(Describe(key, text, "очікується одне з: " + string.Join(", ", allowed)));
            }
        }

        // S14: значення дописується в заголовок політики — `;`, пробіл чи керівний
        // символ розщепили б його (див. CspSettings.IsValidReportUri).
        if (Value(configuration, CspReportUriKey) is { } reportUri
            && !Ecr.Api.Security.CspSettings.IsValidReportUri(reportUri))
        {
            problems.Add(Describe(
                CspReportUriKey, reportUri,
                "очікується відносний шлях (/api/v1/csp-report) або абсолютна адреса http(s) без пробілів, ; , і лапок; порожнє значення вимикає звітування"));
        }

        // D-212 PR-7: невідомий пояс AF інакше зупиняв би кожен синк довідника з датами вже вночі,
        // а не старт служби з ім'ям ключа.
        var zoneKey = Application.Integration.RegistrySync.RegistrySyncValidity.TimeZoneKey;
        if (Value(configuration, zoneKey) is { } zone
            && !Application.Integration.RegistrySync.RegistrySyncValidity.TryResolveTimeZone(zone, out _))
        {
            problems.Add(Describe(zoneKey, zone, "очікується ідентифікатор часового поясу Windows чи IANA, напр. FLE Standard Time або Europe/Kyiv"));
        }

        // ⛔ U17: увімкнений експорт без адреси колектора — не «експорт кудись за
        // замовчуванням» (localhost:4317 бібліотеки), а помилка адміністратора.
        if (Ecr.Api.Observability.TelemetrySetup.IsEnabled(configuration))
        {
            var endpoint = Value(configuration, OtlpEndpointKey);
            if (endpoint is null)
            {
                problems.Add(
                    $"{OtlpEndpointKey} = «»: обов'язковий, коли {Ecr.Api.Observability.TelemetrySetup.EnabledKey} = true — "
                    + "адреса OTLP-колектора (gRPC), напр. http://collector:4317; або вимкніть експорт "
                    + $"(змінна оточення ECR_{OtlpEndpointKey.Replace(":", "__", StringComparison.Ordinal)}).");
            }
            else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problems.Add(Describe(OtlpEndpointKey, endpoint, "очікується абсолютна адреса http:// або https://, напр. http://collector:4317"));
            }
        }

        return problems;
    }

    /// <summary>Попередження, які старт не зупиняють.</summary>
    /// <param name="configuration">Зведена конфігурація застосунку.</param>
    /// <remarks>
    /// ⚠ <c>U17</c>: адреса колектора задана, а експорт вимкнено — значення нічого
    /// не робить. Адміністратор, що задав колектор, має дізнатися про це з журналу
    /// старту, а не з порожнього дашборда.
    /// </remarks>
    public static IReadOnlyList<string> Warnings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var warnings = new List<string>();

        if (Value(configuration, OtlpEndpointKey) is { } endpoint
            && !Ecr.Api.Observability.TelemetrySetup.IsEnabled(configuration))
        {
            warnings.Add(
                $"{OtlpEndpointKey} = «{endpoint}», але {Ecr.Api.Observability.TelemetrySetup.EnabledKey} ≠ true — "
                + "експорт метрик OTLP вимкнено, значення ігнорується (runbook §3.4).");
        }

        // ⚠ S14: примусова CSP не проганялася браузерним набором (e2e-stand.ps1) —
        // вмикати її можна лише після прогону і спостереження за ecr.csp.violations.
        if (Value(configuration, CspEnforceKey) is { } enforce
            && bool.TryParse(enforce, out var enforced) && enforced)
        {
            warnings.Add(
                $"{CspEnforceKey} = true: повна Content-Security-Policy застосовується примусово. "
                + "Переконайтеся, що e2e-набір пройшов під нею, а ecr.csp.violations порожній (runbook §3.4).");
        }

        return warnings;
    }

    /// <summary>Перевіряє конфігурацію; на недійсній — пише Critical і зупиняє старт.</summary>
    /// <param name="app">Зібраний застосунок.</param>
    /// <exception cref="InvalidOperationException">Є хоч один недійсний ключ.</exception>
    public static void ValidateEcrConfiguration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Startup");

        foreach (var warning in Warnings(app.Configuration))
        {
            LogConfigurationWarning(logger, warning);
        }

        var problems = Validate(app.Configuration);
        if (problems.Count == 0)
        {
            return;
        }

        var message = "Недійсна конфігурація — служба не стартує:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Select(p => "  " + p))
            + Environment.NewLine
            + "Значення задаються в %ProgramData%\\ECR\\config\\appsettings.Production.json "
            + "або змінними оточення служби ECR_<Секція>__<Ключ>.";

        LogConfigurationInvalid(logger, message);
        throw new InvalidOperationException(message);
    }

    /// <summary>Значення ключа або <c>null</c>, якщо його не задано чи воно порожнє.</summary>
    private static string? Value(IConfiguration configuration, string key)
        => configuration[key] is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string Describe(string key, string value, string expected)
        => $"{key} = «{value}»: {expected} (змінна оточення ECR_{key.Replace(":", "__", StringComparison.Ordinal)}).";

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Message}")]
    private static partial void LogConfigurationInvalid(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Конфігурація: {Warning}")]
    private static partial void LogConfigurationWarning(ILogger logger, string warning);
}
