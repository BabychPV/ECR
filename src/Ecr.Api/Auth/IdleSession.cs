using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Api.Auth;

/// <summary>
/// Вихід після бездіяльності, який тримає СЕРВЕР (<c>D-471</c>).
/// </summary>
/// <remarks>
/// ⛔ Чому не досить клієнта. Ковзний строк cookie (<c>Auth:SlidingHours</c>, 8 год) продовжується БУДЬ-ЯКИМ
/// запитом, а відкрита вкладка опитує сервер безперервно (зведення задач, «My tasks», прогрес перерахунку,
/// сповіщення). Тож забута вкладка чи викрадена cookie, яку лише «підтримують» опитуванням, жила до абсолютної
/// межі 12 год (S21). Тепер у cookie лежить момент останньої дії ЛЮДИНИ (<see cref="LastActivityClaim"/>), і
/// кожен автентифікований запит звіряє його з чинною межею (<see cref="IIdleTimeoutPolicy"/>).
///
/// ⚠ Що вважається дією людини: лише запити, що змінюють стан (POST/PUT/PATCH/DELETE), без заголовка
/// <see cref="BackgroundHeader"/>. GET/HEAD — НІКОЛИ, хоч би хто їх слав: опитування в клієнті — це GET, і
/// вкладка, що лише опитує, не продовжує сеанс навіть тоді, коли якийсь хук забув позначити себе фоновим.
/// Клацання й клавіші, що не йдуть на сервер, клієнт повідомляє явно — <c>POST /api/v1/session/activity</c>.
///
/// ⚠ Стан — у самій cookie, а не в таблиці сесій: серверного переліку сесій немає (S21), міграції не треба, і
/// два вузли за балансувальником (D-32) бачать той самий момент. Cookie перевидається лише на дії людини і
/// не частіше за <see cref="RenewAfter"/> (окрім явного сигналу активності) — не на кожен запит.
/// </remarks>
public static partial class IdleSession
{
    /// <summary>Claim із моментом останньої дії людини (Unix-секунди, UTC).</summary>
    public const string LastActivityClaim = "ecr:act";

    /// <summary>Заголовок, яким клієнт позначає фоновий запит, що змінює стан (не дія людини).</summary>
    public const string BackgroundHeader = "X-Ecr-Background";

    /// <summary>Шлях явного сигналу активності.</summary>
    public const string ActivityPath = "/api/v1/session/activity";

    /// <summary>Ключ відмови «сеанс завершено через бездіяльність».</summary>
    public const string IdleTimeoutMessageKey = "err.ECR-AUTH-0401.idleTimeout";

    /// <summary>Тип події журналу безпеки «вихід».</summary>
    public const string SignedOutEventType = "SignedOut";

    /// <summary>Як часто перевидавати cookie на звичайних діях людини.</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromSeconds(60);

    /// <summary>Позначка запиту: сеанс щойно відкинуто через бездіяльність (значення — межа в хвилинах).</summary>
    internal const string ExpiredItem = "ecr:idleExpiredMinutes";

    /// <summary>Чи запит — дія людини (а не опитування чи фонова дія).</summary>
    /// <param name="request">Запит.</param>
    public static bool IsUserActivity(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return !HttpMethods.IsGet(request.Method)
               && !HttpMethods.IsHead(request.Method)
               && !HttpMethods.IsOptions(request.Method)
               && !HttpMethods.IsTrace(request.Method)
               && !request.Headers.ContainsKey(BackgroundHeader);
    }

    /// <summary>Ставить момент дії на вході (першому підписі cookie).</summary>
    /// <param name="identity">Ідентичність, що підписується.</param>
    /// <param name="now">Поточний момент.</param>
    internal static void StampOnSignIn(ClaimsIdentity identity, DateTimeOffset now)
    {
        if (!identity.HasClaim(c => c.Type == LastActivityClaim))
        {
            identity.AddClaim(new Claim(LastActivityClaim, ToUnix(now)));
        }
    }

    /// <summary>
    /// Звіряє бездіяльність; <c>false</c> — сеанс відкинуто (cookie стерто, вихід записано в журнал).
    /// </summary>
    /// <param name="context">Контекст перевірки cookie.</param>
    /// <param name="now">Поточний момент.</param>
    internal static async Task<bool> ValidateAsync(CookieValidatePrincipalContext context, DateTimeOffset now)
    {
        var principal = context.Principal;
        if (principal is null)
        {
            return true;
        }

        var http = context.HttpContext;
        var policy = http.RequestServices.GetRequiredService<IIdleTimeoutPolicy>();
        var minutes = await policy.GetMinutesAsync(http.RequestAborted).ConfigureAwait(false);

        // ⚠ Cookie, видана до появи межі, моменту дії не має — рахуємо від входу.
        var last = ReadUnix(principal, LastActivityClaim) ?? ReadUnix(principal, AuthenticationSetup.AuthTimeClaim) ?? now;

        if (now - last >= TimeSpan.FromMinutes(minutes))
        {
            http.Items[ExpiredItem] = minutes;
            context.RejectPrincipal();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            await AuditIdleSignOutAsync(http, principal, minutes, by: "server").ConfigureAwait(false);
            return false;
        }

        if (IsUserActivity(http.Request)
            && (IsActivitySignal(http.Request) || now - last >= RenewAfter))
        {
            var claims = principal.Claims
                .Where(c => c.Type != LastActivityClaim)
                .Select(c => new Claim(c.Type, c.Value))
                .Append(new Claim(LastActivityClaim, ToUnix(now)));

            context.ReplacePrincipal(new ClaimsPrincipal(
                new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            context.ShouldRenew = true;
        }

        return true;
    }

    /// <summary>
    /// Відмова на захищеному маршруті після відкинутого через бездіяльність сеансу: <c>401 ECR-AUTH-0401</c>
    /// з ключем <see cref="IdleTimeoutMessageKey"/> і межею, замість порожнього 401 «не входив».
    /// </summary>
    /// <param name="http">Запит.</param>
    internal static void ThrowIfExpired(HttpContext http)
    {
        if (http.Items.TryGetValue(ExpiredItem, out var raw) && raw is int minutes)
        {
            throw new Application.Errors.AccessDeniedException(
                ErrorCodes.Unauthorized,
                $"Сеанс завершено: {minutes} хв без дій.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = IdleTimeoutMessageKey,
                    ["idleMinutes"] = minutes,
                });
        }
    }

    /// <summary>Записує вихід через бездіяльність у журнал безпеки — один раз на сеанс.</summary>
    /// <param name="http">Запит.</param>
    /// <param name="principal">Принципал сеансу, що завершується.</param>
    /// <param name="minutes">Чинна межа.</param>
    /// <param name="by">Хто помітив: <c>server</c> (запит після межі) чи <c>client</c> (таймер вкладки).</param>
    /// <remarks>
    /// ⚠ Один сеанс — один запис: після межі в польоті бувають кілька опитувань із тією самою cookie, а всі
    /// вкладки браузера доходять до межі разом. Ключ — користувач і момент входу (ідентичність сеансу).
    /// На двох вузлах (D-32) запис може подвоїтися — прийнятно: журнал каже правду, лише двічі.
    /// </remarks>
    internal static Task AuditIdleSignOutAsync(HttpContext http, ClaimsPrincipal principal, int minutes, string by)
    {
        var authTime = principal.FindFirstValue(AuthenticationSetup.AuthTimeClaim) ?? "-";
        var userId = principal.FindFirstValue(AuthenticationSetup.UserIdClaim);
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        var key = $"idle-signout:{userId}:{authTime}";

        if (cache.TryGetValue(key, out _))
        {
            return Task.CompletedTask;
        }

        cache.Set(key, true, TimeSpan.FromMinutes(15));
        return AuditSignOutAsync(http, principal, new { reason = "idle", by, idleTimeoutMinutes = minutes });
    }

    /// <summary>Записує вихід у журнал безпеки (подія-факт, поза транзакцією).</summary>
    /// <param name="http">Запит.</param>
    /// <param name="principal">Принципал сеансу.</param>
    /// <param name="details">Деталі; обов'язково з <c>reason</c>.</param>
    internal static async Task AuditSignOutAsync(HttpContext http, ClaimsPrincipal principal, object details)
    {
        if (!int.TryParse(
                principal.FindFirstValue(AuthenticationSetup.UserIdClaim), NumberStyles.None,
                CultureInfo.InvariantCulture, out var userId))
        {
            return;
        }

        var services = http.RequestServices;
        try
        {
            var audit = services.GetRequiredService<IAuditWriter>();
            var correlation = services.GetService<ICurrentUser>()?.CorrelationId;
            await audit.WriteIndependentSecurityEventAsync(
                new SecurityEventRecord(
                    services.GetRequiredService<Ecr.Domain.Abstractions.IClock>().UtcNow, SignedOutEventType, TargetUserId: userId, TargetRoleId: null,
                    JsonSerializer.Serialize(details, Json), userId, correlation),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // ⛔ Збій журналу не повертає сеанс до життя: вихід уже відбувся, слід — у лог.
            LogAuditFailed(services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Auth"), failure, userId);
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static bool IsActivitySignal(HttpRequest request)
        => string.Equals(request.Path.Value, ActivityPath, StringComparison.OrdinalIgnoreCase);

    private static string ToUnix(DateTimeOffset at)
        => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadUnix(ClaimsPrincipal principal, string type)
        => long.TryParse(principal.FindFirst(type)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Журнал безпеки: не записано вихід користувача {UserId}.")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, int userId);
}
