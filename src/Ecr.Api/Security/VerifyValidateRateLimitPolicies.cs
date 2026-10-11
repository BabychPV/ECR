// src/Ecr.Api/Security/VerifyValidateRateLimitPolicies.cs

using System.Security.Claims;
using System.Threading.RateLimiting;
using Ecr.Api.Auth;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// Основа меж частоти «важких» POST-дій перевірки на КОРИСТУВАЧА: фіксоване хвилинне вікно без черги,
/// розділ — ідентифікатор користувача із заявки (за NAT адреса спільна), як у <see cref="SearchRateLimitPolicy"/>.
/// </summary>
/// <remarks>
/// L1-05 / P1-08 (аудит 09.10, рішення людини 11.10): 100 запитів на хвилину на користувача; понад межу —
/// 429 <c>ECR-REQ-0429</c> із <c>Retry-After</c> у секундах (спільний шлях <see cref="LoginRateLimiting.RejectAsync"/>).
/// Лічильник у пам'яті процесу: перезапуск скидає, на кількох вузлах межа діє на кожному окремо.
/// Анонім до цих ендпоінтів не доходить (401 до обмежувача); для надійності він ділить один «закритий» розділ.
/// </remarks>
public abstract class UserPerMinuteRateLimitPolicy : IRateLimiterPolicy<string>
{
    /// <summary>Запитів на хвилину на користувача, якщо конфігурація мовчить.</summary>
    public const int DefaultPermitPerMinute = 100;

    private const string AnonymousPartition = "anonymous";

    private readonly int _permit;
    private readonly string _detailKey;
    private readonly string _detailFallback;

    /// <summary>Створює політику.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <param name="permitKey">Ключ конфігурації межі.</param>
    /// <param name="detailKey">Ключ каталогу подробиці відмови.</param>
    /// <param name="detailFallback">Запасна подробиця — коли каталог недоступний.</param>
    protected UserPerMinuteRateLimitPolicy(
        IConfiguration configuration, string permitKey, string detailKey, string detailFallback)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _permit = configuration.GetValue(permitKey, DefaultPermitPerMinute);
        _detailKey = detailKey;
        _detailFallback = detailFallback;
    }

    /// <inheritdoc />
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        (rejection, ct) => new ValueTask(LoginRateLimiting.RejectAsync(
            rejection, ErrorCodes.TooManyRequests, _detailKey, _detailFallback, ct));

    /// <inheritdoc />
    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var userId = httpContext.User.FindFirstValue(AuthenticationSetup.UserIdClaim);
        var key = string.IsNullOrEmpty(userId) ? AnonymousPartition : "user:" + userId;

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = _permit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}

/// <summary>Межа <c>POST /api/v1/reports/snapshots/{id}/verify</c> (L1-05): звірка зрізу читає всі його рядки.</summary>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class SnapshotVerifyRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "snapshot-verify";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:VerifyPermitPerMinute";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyVerifications";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many snapshot verification requests. Wait a moment and try again.";
}

/// <summary>Межа <c>POST /api/v1/documents/{id}/validate</c> (P1-08): кожен прогін читає документ і дописує результат.</summary>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class DocumentValidateRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "document-validate";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:ValidatePermitPerMinute";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyValidations";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many document validation requests. Wait a moment and try again.";
}
