// src/Ecr.Api/Security/SmtpTestQuotaMiddleware.cs

using System.Threading.RateLimiting;
using Ecr.Application.Notifications;
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>Системна квота проб транспорту (30/год на всіх, <c>Security:RateLimit:SmtpTestSystemPermitPerHour</c>).</summary>
public sealed class SmtpTestSystemQuota(IConfiguration configuration) : IDisposable
{
    private readonly FixedWindowRateLimiter _limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = configuration.GetValue(
            SmtpTestRateLimitPolicy.SystemPermitKey, SmtpTestRateLimitPolicy.DefaultSystemPermitPerHour),
        Window = TimeSpan.FromHours(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    /// <summary>Бере одну пробу з квоти.</summary>
    public RateLimitLease TryAcquire() => _limiter.AttemptAcquire();

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Системна межа проб ПІСЛЯ політики користувача й перевірки права (рекомендація безпекового рев'ю D-263).
/// </summary>
/// <remarks>
/// ⛔ Чому не глобальний обмежувач: той рахує й ВІДХИЛЕНІ запити, тож адмін A, що довбе ендпоінт, з'їв би
/// системну квоту за всіх. Цей проміжний шар стоїть ПІСЛЯ <c>UseRateLimiter</c> (відхилені політикою
/// користувача сюди не доходять), а право перевіряється ПЕРЕД списанням. Ендпоінт визначається за МЕТАДАНИМИ
/// (<c>[EnableRateLimiting("smtp-test")]</c>), а не за підрядком шляху: завершальна коса риска й варіанти
/// маршруту не обходять квоту, а схожі шляхи без атрибута її не отримують.
/// </remarks>
public sealed class SmtpTestQuotaMiddleware(RequestDelegate next, SmtpTestSystemQuota quota)
{
    /// <summary>Виконує запит; проба над квотою — 429.</summary>
    public async Task InvokeAsync(HttpContext context, IAccessDecisionService access, ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        if (policy != SmtpTestRateLimitPolicy.PolicyName)
        {
            await next(context).ConfigureAwait(false);

            return;
        }

        await PermissionCheck.RequireAsync(
            access, currentUser, ListNotificationChannelsHandler.Permission, context.RequestAborted)
            .ConfigureAwait(false);

        using var lease = quota.TryAcquire();

        if (!lease.IsAcquired)
        {
            await LoginRateLimiting.RejectAsync(
                new OnRejectedContext { HttpContext = context, Lease = lease },
                ErrorCodes.TooManyRequests, SmtpTestRateLimitPolicy.DetailKey, SmtpTestRateLimitPolicy.DetailFallback,
                context.RequestAborted).ConfigureAwait(false);

            return;
        }

        await next(context).ConfigureAwait(false);
    }
}