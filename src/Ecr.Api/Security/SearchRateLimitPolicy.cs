// src/Ecr.Api/Security/SearchRateLimitPolicy.cs

using System.Security.Claims;
using System.Threading.RateLimiting;
using Ecr.Api.Auth;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>
/// Межа частоти <c>GET /api/v1/search</c> на КОРИСТУВАЧА (BE-19).
/// </summary>
/// <remarks>
/// Палітра шле запит на кожне натискання клавіші, а кожен запит — це
/// <c>LIKE</c> по трьох таблицях. Межа ріже одного користувача, що затиснув
/// клавішу чи крутить скрипт, і не чіпає сусідів: розділ — ідентифікатор
/// користувача із заявки, не адреса (за NAT адреса одна на весь майданчик).
/// </remarks>
public sealed class SearchRateLimitPolicy(IConfiguration configuration) : IRateLimiterPolicy<string>
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "search";

    /// <summary>Запитів на вікно на користувача, якщо конфігурація мовчить.</summary>
    /// <remarks>
    /// 30 за 10 с = 3/с тривало: швидкий набір із дебаунсом клієнта дає 1–2/с,
    /// тож людина межі не бачить, а скрипт обмежено до трьох запитів за секунду.
    /// </remarks>
    public const int DefaultPermit = 30;

    /// <summary>Довжина вікна в секундах, якщо конфігурація мовчить.</summary>
    public const int DefaultWindowSeconds = 10;

    private const string DetailKey = "err.ECR-REQ-0429.tooManySearches";

    private const string DetailFallback = "Too many searches in a short time. Wait a moment and try again.";

    /// <summary>Розділ для запиту без ідентифікатора — один на всіх, тобто «закрито», а не «без межі».</summary>
    private const string AnonymousPartition = "anonymous";

    private readonly int _permit = configuration.GetValue("Security:RateLimit:SearchPermit", DefaultPermit);

    private readonly TimeSpan _window = TimeSpan.FromSeconds(
        configuration.GetValue("Security:RateLimit:SearchWindowSeconds", DefaultWindowSeconds));

    /// <inheritdoc />
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
        static (rejection, ct) => new ValueTask(LoginRateLimiting.RejectAsync(
            rejection, ErrorCodes.TooManyRequests, DetailKey, DetailFallback, ct));

    /// <inheritdoc />
    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var userId = httpContext.User.FindFirstValue(AuthenticationSetup.UserIdClaim) ?? AnonymousPartition;

        return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = _permit,
            Window = _window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
