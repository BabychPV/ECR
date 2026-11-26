// src/Ecr.Api/Security/SourceTestRateLimitPolicy.cs

using System.Security.Claims;
using System.Threading.RateLimiting;
using Ecr.Api.Auth;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// Межа частоти перевірок з'єднання з тіла форми на КОРИСТУВАЧА: <c>POST /api/v1/data-sources/test-connection</c>
/// (P-1126-SRCCRED §13.1 d, AC-11d).
/// </summary>
/// <remarks>
/// ⛔ Перевірка з тіла приймає довільну адресу й пароль: без межі це підбір пароля чужого облікового запису і сканер
/// внутрішньої мережі. Розділ — користувач із заявки, як у <see cref="SmtpTestRateLimitPolicy"/>; пауза цілі після
/// відмов входу поспіль — окремо, у <c>SourceTargetLockout</c> (Q-15).
/// </remarks>
public sealed class SourceTestRateLimitPolicy(IConfiguration configuration) : IRateLimiterPolicy<string>
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "source-test";

    /// <summary>Перевірок на хвилину на користувача, якщо конфігурація мовчить.</summary>
    public const int DefaultPermitPerMinute = 5;

    /// <summary>Ключ конфігурації: межа на користувача за хвилину.</summary>
    public const string PermitKey = "Security:RateLimit:SourceTestPermitPerMinute";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManySourceTests";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many connection tests. Wait a minute and try again.";

    private readonly int _permit = configuration.GetValue(PermitKey, DefaultPermitPerMinute);

    /// <inheritdoc />
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        static (rejection, ct) => new ValueTask(LoginRateLimiting.RejectAsync(
            rejection, ErrorCodes.TooManyRequests, DetailKey, DetailFallback, ct));

    /// <inheritdoc />
    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var userId = httpContext.User.FindFirstValue(AuthenticationSetup.UserIdClaim);
        var key = string.IsNullOrEmpty(userId)
            ? "anonymous:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "user:" + userId;

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = _permit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
