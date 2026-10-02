// src/Ecr.Api/Security/DocumentRecalculateQuota.cs

using System.Security.Claims;
using System.Threading.RateLimiting;
using Ecr.Api.Auth;
using Ecr.Api.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Api.Security;

/// <summary>
/// Маркер ендпоінта, на який діє межа частоти перерахунку документа (<see cref="DocumentRecalculateQuotaMiddleware"/>).
/// Ендпоінт визначається за МЕТАДАНИМИ, а не за підрядком шляху — так само, як для проб SMTP.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DocumentRecalculateQuotaAttribute : Attribute;

/// <summary>
/// Лічильник частоти перерахунку на пару «користувач + документ» (рекомендація безпекового рев'ю «Аудит»,
/// 2026-10-02): <c>Security:RateLimit:RecalculatePermitPerMinute</c>, типово 6 на хвилину.
/// </summary>
/// <remarks>
/// ⛔ Після того як перерахунок свого документа відкрито за читанням (<c>Document.View</c>), кожен читач
/// міг ставити в чергу повні перерахунки без обмежень. Межа — для ВСІХ однакова, зокрема й для власників
/// <c>Calculation.Recalculate</c> (простіше й безпечніше; семантика Exclusive/Coalesced не змінюється).
///
/// ⚠ Власний лічильник, а не <c>[EnableRateLimiting]</c>: стандартний обмежувач списує дозвіл ДО обробника
/// і не вміє повертати, тож запити, відхилені правом, пошуком документа чи валідацією (403/404/422),
/// з'їдали б межу. Тут дозвіл повертається (<see cref="Refund(Ticket)"/>), коли відповідь не успішна.
/// Лічильник у пам'яті процесу: перезапуск скидає, на кількох вузлах межа діє на кожному окремо.
/// </remarks>
public sealed class DocumentRecalculateQuota(IConfiguration configuration, TimeProvider? clock = null)
{
    /// <summary>Запитів на хвилину на користувача й документ, якщо конфігурація мовчить.</summary>
    public const int DefaultPermitPerMinute = 6;

    /// <summary>Ключ конфігурації: межа за хвилину на пару «користувач + документ».</summary>
    public const string PermitKey = "Security:RateLimit:RecalculatePermitPerMinute";

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyRecalculations";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many recalculation requests. Wait a moment and try again.";

    /// <summary>Ключ конфігурації: межа за хвилину на КОРИСТУВАЧА (усі документи разом; ent7 P3-2).</summary>
    public const string UserPermitKey = "Security:RateLimit:RecalculateUserPermitPerMinute";

    /// <summary>Запитів на хвилину на користувача (усі документи), якщо конфігурація мовчить.</summary>
    public const int DefaultUserPermitPerMinute = 30;

    private const long WindowMs = 60_000;
    private const int SweepThreshold = 1024;

    private readonly int _permit = configuration.GetValue(PermitKey, DefaultPermitPerMinute);
    private readonly int _userPermit = configuration.GetValue(UserPermitKey, DefaultUserPermitPerMinute);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (long Start, int Count)> _windows = [];
    private readonly object _gate = new();
    private long _lastSweep = long.MinValue / 2;

    /// <summary>Скільки ключів зараз у лічильнику (для перевірки, що словник не росте без меж).</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _windows.Count;
            }
        }
    }

    /// <summary>Дозвіл на запит: два списані лічильники — користувача й пари «користувач + документ».</summary>
    /// <param name="UserKey">Ключ лічильника користувача.</param>
    /// <param name="UserWindow">Вікно списання на користувача.</param>
    /// <param name="DocumentKey">Ключ лічильника пари.</param>
    /// <param name="DocumentWindow">Вікно списання на пару.</param>
    public readonly record struct Ticket(string UserKey, long UserWindow, string DocumentKey, long DocumentWindow);

    /// <summary>
    /// Бере дозвіл на запит: і на користувача, і на пару «користувач + документ». Відмова одного повертає списане в іншому.
    /// </summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="documentId">Документ (з маршруту).</param>
    /// <param name="ticket">Що списано (для <see cref="Refund(Ticket)"/>).</param>
    /// <param name="retryAfter">Скільки чекати, якщо відмовлено.</param>
    public bool TryAcquire(string userId, string documentId, out Ticket ticket, out TimeSpan retryAfter)
    {
        var userKey = "user:" + userId;
        var documentKey = userKey + "|doc:" + documentId;
        ticket = default;

        if (!Acquire(userKey, _userPermit, out var userWindow, out retryAfter))
        {
            return false;
        }

        if (!Acquire(documentKey, _permit, out var documentWindow, out retryAfter))
        {
            Refund(userKey, userWindow);

            return false;
        }

        ticket = new Ticket(userKey, userWindow, documentKey, documentWindow);

        return true;
    }

    /// <summary>Повертає обидва списання з <see cref="TryAcquire(string, string, out Ticket, out TimeSpan)"/>.</summary>
    /// <param name="ticket">Дозвіл запиту.</param>
    public void Refund(Ticket ticket)
    {
        Refund(ticket.UserKey, ticket.UserWindow);
        Refund(ticket.DocumentKey, ticket.DocumentWindow);
    }

    /// <summary>Бере один дозвіл; <c>false</c> — межу вичерпано (<paramref name="retryAfter"/> — коли відкриється вікно).</summary>
    /// <param name="key">Пара «користувач + документ».</param>
    /// <param name="window">Початок вікна, у якому списано дозвіл (для <see cref="Refund(string, long)"/>).</param>
    /// <param name="retryAfter">Скільки чекати, якщо відмовлено.</param>
    public bool TryAcquire(string key, out long window, out TimeSpan retryAfter)
        => Acquire(key, _permit, out window, out retryAfter);

    private bool Acquire(string key, int permit, out long window, out TimeSpan retryAfter)
    {
        var now = _clock.GetTimestamp() * 1000 / _clock.TimestampFrequency;

        lock (_gate)
        {
            // ent7 P3-3: прохід — не частіше раза на вікно (інакше кожен запит при >1024 записів лінійний).
            if (_windows.Count > SweepThreshold && now - _lastSweep >= WindowMs)
            {
                _lastSweep = now;
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= WindowMs).Select(w => w.Key).ToList())
                {
                    _windows.Remove(stale);
                }
            }

            if (!_windows.TryGetValue(key, out var entry) || now - entry.Start >= WindowMs)
            {
                entry = (now, 0);
            }

            window = entry.Start;
            if (entry.Count >= permit)
            {
                _windows[key] = entry;
                retryAfter = TimeSpan.FromMilliseconds(Math.Max(1000, entry.Start + WindowMs - now));

                return false;
            }

            _windows[key] = (entry.Start, entry.Count + 1);
            retryAfter = TimeSpan.Zero;

            return true;
        }
    }

    /// <summary>Повертає дозвіл, списаний у вікні <paramref name="window"/> (інше вікно вже закінчилось — нічого).</summary>
    /// <param name="key">Пара «користувач + документ».</param>
    /// <param name="window">Вікно зі <see cref="TryAcquire(string, out long, out TimeSpan)"/>.</param>
    public void Refund(string key, long window)
    {
        lock (_gate)
        {
            if (_windows.TryGetValue(key, out var entry) && entry.Start == window && entry.Count > 0)
            {
                // Нульовий запис не тримаємо: запити на неіснуючі id інакше роздували б словник (ent7 P3-3).
                if (entry.Count == 1)
                {
                    _windows.Remove(key);
                }
                else
                {
                    _windows[key] = (entry.Start, entry.Count - 1);
                }
            }
        }
    }

    /// <summary>Відмовлений «дозвіл», що лише несе <c>Retry-After</c> для спільної відповіді 429.</summary>
    internal sealed class RejectedLease(TimeSpan retryAfter) : RateLimitLease
    {
        /// <inheritdoc />
        public override bool IsAcquired => false;

        /// <inheritdoc />
        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        /// <inheritdoc />
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
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
/// Межа перерахунку документа ПІСЛЯ автентифікації й авторизації: анонім отримує 401 і межі не витрачає;
/// списане повертається, коли запит відхилено (виняток або не-2xx).
/// </summary>
public sealed class DocumentRecalculateQuotaMiddleware(RequestDelegate next, DocumentRecalculateQuota quota)
{
    /// <summary>Лише 4xx — відмова до виконання роботи; 5xx не повертає межу.</summary>
    private static bool IsRefusal(int status) => status is >= StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError;

    /// <summary>Виконує запит; понад межу — 429 <c>ECR-REQ-0429</c> з <c>Retry-After</c>.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = context.User.FindFirstValue(AuthenticationSetup.UserIdClaim);
        if (context.GetEndpoint()?.Metadata.GetMetadata<DocumentRecalculateQuotaAttribute>() is null
            || string.IsNullOrEmpty(userId))
        {
            await next(context).ConfigureAwait(false);

            return;
        }

        var documentId = Convert.ToString(context.Request.RouteValues["id"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        if (!quota.TryAcquire(userId, documentId, out var ticket, out var retryAfter))
        {
            using var lease = new DocumentRecalculateQuota.RejectedLease(retryAfter);
            await LoginRateLimiting.RejectAsync(
                new Microsoft.AspNetCore.RateLimiting.OnRejectedContext { HttpContext = context, Lease = lease },
                ErrorCodes.TooManyRequests, DocumentRecalculateQuota.DetailKey, DocumentRecalculateQuota.DetailFallback,
                context.RequestAborted).ConfigureAwait(false);

            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRefusal(exception is OperationCanceledException ? StatusCodes.Status500InternalServerError
                                              : ExceptionHandlingMiddleware.Map(exception).Status))
        {
            // Виняток відмови (403/404/422…) мапить зовнішній ExceptionHandlingMiddleware — тут лише повертаємо дозвіл.
            // ⛔ 5xx і скасування запиту дозвіл НЕ повертають (ent7 P3-1): робота могла вже статися, як і для проб SMTP.
            quota.Refund(ticket);

            throw;
        }

        if (IsRefusal(context.Response.StatusCode))
        {
            quota.Refund(ticket);
        }
    }
}
