// src/Ecr.Api/Security/ScriptAuthoringRateLimitPolicy.cs

using System.Threading.RateLimiting;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// PSS-21: межа частоти ПРОГОНІВ скрипта методології на документі на КОРИСТУВАЧА — перегляд
/// <c>POST …/script/preview</c> і паритет <c>POST …/script/parity</c>; типово 30 за хвилину, понад — 429
/// <c>ECR-REQ-0429</c> / <c>tooManyScriptRuns</c> із <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// ⛔ Перегляд виконує ТЕКСТ ІЗ ЗАПИТУ, а право на нього — <c>Calculation.View</c> (рішення людини за замовчуванням
/// аудиту PSS-21, питання 8: право не змінюється, додаються межі). Один кошик на перегляд і паритет: обидва — прогін
/// скрипта по рядках документа. Межа на користувача, а не на адресу: за корпоративним NAT адреса спільна.
/// Лічильник у пам'яті процесу. Одночасність прогонів на процес тримає <see cref="ScriptRunGate"/>, час і кроки —
/// рушій (бюджети рядка й запиту, <c>RunTimeout</c> обробника перегляду).
/// </remarks>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class ScriptAuthoringRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback, DefaultScriptRunPermit)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "script-run";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:ScriptRunPermitPerMinute";

    /// <summary>Межа за хвилину на користувача, якщо конфігурація мовчить.</summary>
    /// <remarks>Ім'я унікальне навмисно — див. <see cref="DocumentExportRateLimitPolicy.DefaultExportPermit"/>.</remarks>
    public const int DefaultScriptRunPermit = 30;

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyScriptRuns";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many script preview runs in a short time. Wait a moment and try again.";
}

/// <summary>
/// PSS-21: межа частоти ПЕРЕВІРОК тексту скрипта на КОРИСТУВАЧА — <c>POST …/script/validate</c> і
/// <c>POST …/script/format-const</c>; типово 120 за хвилину, понад — 429 <c>ECR-REQ-0429</c> / <c>tooManyScriptChecks</c>.
/// </summary>
/// <remarks>
/// ⚠ Власний кошик, щедріший за <see cref="ScriptAuthoringRateLimitPolicy"/>: перевірка лише компілює (нічого не
/// виконує), а редактор кличе її сам після кожної паузи введення (500 мс) — 30 за хвилину різали б звичайне набирання.
/// 120 — дві перевірки на секунду безперервно; компіляція лінійна й обмежена розміром тексту.
/// </remarks>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class ScriptCheckRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback, DefaultScriptCheckPermit)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "script-check";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:ScriptCheckPermitPerMinute";

    /// <summary>Межа за хвилину на користувача, якщо конфігурація мовчить.</summary>
    public const int DefaultScriptCheckPermit = 120;

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyScriptChecks";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many script checks in a short time. Wait a moment and try again.";
}

/// <summary>
/// PSS-21: межа ОДНОЧАСНИХ прогонів скрипта (перегляд і паритет) на весь процес Api — та сама форма, що
/// <see cref="ImportPreviewGate"/>: межа + черга «хто раніше»; понад чергу — 429 <c>ECR-REQ-0429</c> / <c>scriptRunBusy</c>.
/// </summary>
/// <remarks>
/// ⚠ Межа на процес, а не на користувача: прогін синхронно тримає потік пулу, і десять користувачів по одному прогону
/// виснажують його так само, як один із десятьма. Дозвіл береться в дії, ПІСЛЯ прив'язки тіла.
/// </remarks>
public sealed class ScriptRunGate : IDisposable
{
    /// <summary>Скільки прогонів одночасно, якщо конфігурація мовчить.</summary>
    public const int DefaultConcurrency = 4;

    /// <summary>Скільки прогонів чекає в черзі, якщо конфігурація мовчить.</summary>
    public const int DefaultQueueLimit = 8;

    /// <summary>Ключ конфігурації: одночасних прогонів.</summary>
    public const string ConcurrencyKey = "Security:RateLimit:ScriptRunConcurrency";

    /// <summary>Ключ конфігурації: довжина черги.</summary>
    public const string QueueLimitKey = "Security:RateLimit:ScriptRunQueueLimit";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.scriptRunBusy";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "The server is busy running other script previews. Try again in a minute.";

    private readonly ConcurrencyLimiter _limiter;

    /// <summary>Будує обмежувач з конфігурації.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public ScriptRunGate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(1, configuration.GetValue(ConcurrencyKey, DefaultConcurrency)),
            QueueLimit = Math.Max(0, configuration.GetValue(QueueLimitKey, DefaultQueueLimit)),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>Чекає місця прогону (у черзі, якщо вона не повна); понад чергу — відмова 429.</summary>
    /// <param name="ct">Скасування — звільняє місце в черзі.</param>
    /// <returns>Оренда; звільнити (<c>Dispose</c>) після прогону.</returns>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0429</c>, ключ <c>scriptRunBusy</c>.</exception>
    public async Task<RateLimitLease> EnterAsync(CancellationToken ct)
    {
        var lease = await _limiter.AcquireAsync(1, ct).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.Dispose();

        throw new BusinessRuleException(
            ErrorCodes.TooManyRequests,
            DetailFallback,
            new Dictionary<string, object?> { ["messageKey"] = DetailKey });
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Реєстрація меж авторства скрипта (PSS-21) ОКРЕМО від <see cref="LoginRateLimiting.AddEcrRateLimiting"/>: політики
/// додаються до тих самих <see cref="RateLimiterOptions"/> (<c>Configure</c> складається), ворота — синглтоном.
/// </summary>
public static class ScriptAuthoringLimits
{
    /// <summary>Реєструє політики <see cref="ScriptAuthoringRateLimitPolicy"/>, <see cref="ScriptCheckRateLimitPolicy"/> і ворота <see cref="ScriptRunGate"/>.</summary>
    /// <param name="services">Колекція служб.</param>
    /// <returns>Та сама колекція.</returns>
    public static IServiceCollection AddEcrScriptAuthoringLimits(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ScriptRunGate>();
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy<string, ScriptAuthoringRateLimitPolicy>(ScriptAuthoringRateLimitPolicy.PolicyName);
            options.AddPolicy<string, ScriptCheckRateLimitPolicy>(ScriptCheckRateLimitPolicy.PolicyName);
        });

        return services;
    }
}
