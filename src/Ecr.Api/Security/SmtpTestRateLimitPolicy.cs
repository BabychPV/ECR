// src/Ecr.Api/Security/SmtpTestRateLimitPolicy.cs

using System.Security.Claims;
using System.Threading.RateLimiting;
using Ecr.Api.Auth;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// Межа частоти проб поштового транспорту на КОРИСТУВАЧА: <c>POST /api/v1/notifications/smtp/test</c>
/// і <c>POST /api/v1/notifications/channels/{id}/test</c> (рекомендація безпекового рев'ю D-263).
/// </summary>
/// <remarks>
/// ⛔ Проба шле РЕАЛЬНИЙ лист через ефективний транспорт: без межі адміністратор із
/// викраденим сеансом (або скрипт) перетворює сервер на розсилку з нашої адреси.
/// Розділ — користувач із заявки (за NAT адреса спільна), як у <see cref="SearchRateLimitPolicy"/>.
/// Системна межа (30 на годину на всіх) стоїть окремо — у <c>SmtpTestQuotaMiddleware</c>
/// (проміжний шар після <c>UseRateLimiter</c>: рахує лише прийняті проби; квота в пам'яті процесу),
/// бо ім'єнована політика має один розділ.
/// </remarks>
public sealed class SmtpTestRateLimitPolicy(IConfiguration configuration) : IRateLimiterPolicy<string>
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "smtp-test";

    /// <summary>Проб на хвилину на користувача, якщо конфігурація мовчить.</summary>
    public const int DefaultPermitPerMinute = 5;

    /// <summary>Проб на годину на всю систему, якщо конфігурація мовчить.</summary>
    public const int DefaultSystemPermitPerHour = 30;

    /// <summary>Ключ конфігурації: межа на користувача за хвилину.</summary>
    public const string PermitKey = "Security:RateLimit:SmtpTestPermitPerMinute";

    /// <summary>Ключ конфігурації: системна межа за годину.</summary>
    public const string SystemPermitKey = "Security:RateLimit:SmtpTestSystemPermitPerHour";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManySmtpTests";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many test messages. Wait a moment and try again.";

    private const string AnonymousPartition = "anonymous";

    private readonly int _permit = configuration.GetValue(PermitKey, DefaultPermitPerMinute);

    /// <inheritdoc />
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        static (rejection, ct) => new ValueTask(LoginRateLimiting.RejectAsync(
            rejection, ErrorCodes.TooManyRequests, DetailKey, DetailFallback, ct));

    /// <inheritdoc />
    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // ⚠ Розділ за користувачем — лише для автентифікованих. Анонім (запит без сеансу) не ділить розділ
        // ні з чиїмось userId, ні з іншими анонімами іншої адреси: свій вузький розділ за адресою.
        var userId = httpContext.User.FindFirstValue(AuthenticationSetup.UserIdClaim);
        var key = string.IsNullOrEmpty(userId)
            ? AnonymousPartition + ":" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
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
