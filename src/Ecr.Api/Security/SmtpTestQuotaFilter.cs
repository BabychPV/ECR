// src/Ecr.Api/Security/SmtpTestQuotaFilter.cs

using System.Threading.RateLimiting;
using Ecr.Application.Notifications;
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
/// ⛔ Чому фільтр, а не глобальний обмежувач: той рахує й ВІДХИЛЕНІ запити, тож адмін A, що довбе ендпоінт,
/// з'їв би системну квоту за всіх. Обмежувач частоти відсікає запит ДО фільтра, а право перевіряється тут ПЕРЕД
/// списанням — відхилені користувачем і неправомочні запити квоту не витрачають. Ендпоінти позначені атрибутом
/// <c>[ServiceFilter]</c> — за метаданими, а не за підрядком шляху.
/// </remarks>
public sealed class SmtpTestQuotaFilter(
    SmtpTestSystemQuota quota, IAccessDecisionService access, ICurrentUser currentUser) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await PermissionCheck.RequireAsync(
            access, currentUser, ListNotificationChannelsHandler.Permission, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        using var lease = quota.TryAcquire();

        if (!lease.IsAcquired)
        {
            await LoginRateLimiting.RejectAsync(
                new OnRejectedContext { HttpContext = context.HttpContext, Lease = lease },
                ErrorCodes.TooManyRequests, SmtpTestRateLimitPolicy.DetailKey, SmtpTestRateLimitPolicy.DetailFallback,
                context.HttpContext.RequestAborted).ConfigureAwait(false);
            context.Result = new EmptyResult();

            return;
        }

        await next().ConfigureAwait(false);
    }
}
