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
        ("Security:RateLimit:LoginPermitPerMinute", 1),
        ("Security:RateLimit:SearchPermit", 1),
        ("Security:RateLimit:SearchWindowSeconds", 1),
        ("Jobs:ShutdownTimeoutSeconds", 1),
        ("Audit:ExportMaxRows", 1),
        ("Localization:ImportMaxBytes", 1),
        ("Registries:ImportMaxBytes", 1),
        ("Campaign:AtRiskDays", 0),
        ("Integration:CatalogTimeoutSeconds", 1),
        ("Logging:File:RetainedFiles", 1),
        ("Logging:File:FileSizeLimitMb", 1),
    ];

    /// <summary>Булеві ключі: лише <c>true</c> або <c>false</c>.</summary>
    public static readonly IReadOnlyList<string> Booleans =
    [
        "Auth:RequireHttps",
        "Auth:EnableNegotiate",
        "Security:RateLimit:TrustForwardedFor",
        "Jobs:NightlyRecalculation:Enabled",
        "Logging:File:Json",
    ];

    /// <summary>Ключі з переліком допустимих значень (без урахування регістру).</summary>
    public static readonly IReadOnlyList<(string Key, string[] Allowed)> Choices =
    [
        ("Schema:StartupMode", ["Validate", "Migrate"]),
        ("Database:EditionMode", Enum.GetNames<SqlEditionMode>()),
    ];

    /// <summary>Ключ адреси OTLP-колектора.</summary>
    public const string OtlpEndpointKey = "Telemetry:OtlpEndpoint";

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

        return problems;
    }

    /// <summary>Попередження, які старт не зупиняють.</summary>
    /// <param name="configuration">Зведена конфігурація застосунку.</param>
    /// <remarks>
    /// ⛔ <c>U17</c> (<c>S-12</c>): експортера OTLP у цій версії НЕМАЄ — він потребує
    /// пакета <c>OpenTelemetry.Extensions.Hosting</c>, тобто окремого рішення про
    /// залежність. Доти непорожній <c>Telemetry:OtlpEndpoint</c> — це налаштування,
    /// яке нічого не робить, і адміністратор, що задав колектор, має дізнатися про
    /// це з журналу старту, а не з порожнього дашборда. Метрики <c>Meter "Ecr"</c>
    /// пишуться й доступні через <c>dotnet-counters</c>.
    /// </remarks>
    public static IReadOnlyList<string> Warnings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Value(configuration, OtlpEndpointKey) is { } endpoint
            ? [$"{OtlpEndpointKey} = «{endpoint}», але експорту OTLP у цій версії немає — значення ігнорується. "
               + "Метрики Meter «Ecr» доступні лише через dotnet-counters на сервері (runbook §3)."]
            : [];
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
