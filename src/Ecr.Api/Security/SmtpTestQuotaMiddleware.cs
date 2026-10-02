// src/Ecr.Api/Security/SmtpTestQuotaMiddleware.cs

using System.Threading.RateLimiting;
using Ecr.Api.Errors;
using Ecr.Application.Notifications;
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Security;

/// <summary>Системна квота проб транспорту (30/год на всіх, <c>Security:RateLimit:SmtpTestSystemPermitPerHour</c>).</summary>
/// <remarks>
/// ⚠ Власний лічильник із фіксованим годинним вікном замість <c>FixedWindowRateLimiter</c>: той не вміє повертати
/// списане, а квота має рахувати лише проби, що ПРОЙШЛИ валідацію (рев'ю ent6 S6) — запит, що завершився 4xx
/// (422/404), повертає токен у тому ж вікні. Лічильник у пам'яті процесу: на N вузлах межа діє на кожному окремо (N ×).
/// </remarks>
public sealed class SmtpTestSystemQuota(IConfiguration configuration, TimeProvider? time = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly int _limit = configuration.GetValue(
        SmtpTestRateLimitPolicy.SystemPermitKey, SmtpTestRateLimitPolicy.DefaultSystemPermitPerHour);
    private long _windowStart;
    private int _used;
    private long _window;

    private static readonly TimeSpan WindowLength = TimeSpan.FromHours(1);

    /// <summary>Бере одну пробу з квоти.</summary>
    public QuotaLease TryAcquire()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();

            if (_window == 0 || _time.GetElapsedTime(_windowStart, now) >= WindowLength)
            {
                _windowStart = now;
                _used = 0;
                _window++;
            }

            if (_used >= _limit)
            {
                var left = WindowLength - _time.GetElapsedTime(_windowStart, now);

                return new QuotaLease(this, 0, left);
            }

            _used++;

            return new QuotaLease(this, _window, TimeSpan.Zero);
        }
    }

    /// <summary>Повертає токен, якщо вікно те саме, в якому його взято.</summary>
    internal void Refund(long window)
    {
        lock (_gate)
        {
            if (window == _window && _used > 0)
            {
                _used--;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>Лізинг квоти: <see cref="Refund"/> повертає токен (запит завершився 4xx).</summary>
    public sealed class QuotaLease(SmtpTestSystemQuota owner, long window, TimeSpan retryAfter) : RateLimitLease
    {
        private int _refunded;

        /// <inheritdoc />
        public override bool IsAcquired => window != 0;

        /// <inheritdoc />
        public override IEnumerable<string> MetadataNames => IsAcquired ? [] : [MetadataName.RetryAfter.Name];

        /// <summary>Повертає токен один раз; для відмови нічого не робить.</summary>
        public void Refund()
        {
            if (IsAcquired && Interlocked.Exchange(ref _refunded, 1) == 0)
            {
                owner.Refund(window);
            }
        }

        /// <inheritdoc />
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (!IsAcquired && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;

                return true;
            }

            metadata = null;

            return false;
        }
    }
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

        // ⛔ Квота рахує лише проби, що пройшли валідацію й пошук (рев'ю ent6 S6): відповідь 4xx (422/404) повертає токен.
        // ⚠ 422/404 тут — ВИНЯТКИ (ProblemDetails пише зовнішній ExceptionHandlingMiddleware), тож статус беремо з тієї ж
        // `Map`. 5xx токен не повертає — проба могла дійти до транспорту.
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && ExceptionHandlingMiddleware.Map(e).Status is >= 400 and < 500)
        {
            lease.Refund();

            throw;
        }

        if (context.Response.StatusCode is >= 400 and < 500)
        {
            lease.Refund();
        }
    }
}