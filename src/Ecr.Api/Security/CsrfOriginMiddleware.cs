using Ecr.Application.Errors;

namespace Ecr.Api.Security;

/// <summary>
/// Відмова в небезпечному запиті, який браузер надіслав з ЧУЖОГО сайту (<c>L1-04</c>,
/// вимога antiforgery з <c>06-tz-architecture.md</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Доти захист від підробки запиту тримався лише на cookie
/// <c>SameSite=Strict</c>. Вона не рятує від СУСІДНЬОГО хоста того самого сайту
/// (<c>evil.corp.local</c> → <c>ecr.corp.local</c>): для браузера це
/// «same-site», cookie їде, і форма-приманка з <c>multipart</c>/<c>POST</c> без
/// тіла доходить до обробника від імені користувача. Те саме — Windows-вхід
/// (Negotiate), чиї облікові дані браузер теж підставляє сам.
///
/// ⚠ Правило (перша наявна ознака вирішує):
/// <list type="number">
/// <item><c>Sec-Fetch-Site</c> є — пропускаються лише <c>same-origin</c> і
/// <c>none</c> (дія самого користувача). <c>same-site</c> відхиляється
/// СВІДОМО: саме це і є атака сусіднього хоста. Веб-клієнт ходить в API зі
/// свого ж origin: SPA роздає сам застосунок (<c>UseStaticFiles</c>), за
/// проксі — той самий зовнішній origin, у dev і Playwright — проксі Vite.
/// ⚠ Проксі Vite (<c>changeOrigin</c>) переписує <c>Host</c> на порт API, а
/// <c>Origin</c> лишає портом Vite; проходить запит саме тут, бо браузер
/// шле <c>same-origin</c> (замір Chromium: <c>Origin: http://localhost:5085</c>,
/// <c>Host: localhost:5084</c>, <c>Sec-Fetch-Site: same-origin</c>).</item>
/// <item>Заголовка немає (старий браузер) — <c>Origin</c>, якщо він є, має
/// вказувати на той самий хост і порт, що й <c>Host</c> запиту або перший
/// <c>X-Forwarded-Host</c> (проксі, що переписує <c>Host</c>); <c>null</c> —
/// відмова. Схема не порівнюється: за проксі, що знімає TLS, вона
/// розходиться законно.</item>
/// <item>Немає жодного — пропускається. Так ходять не браузери (скрипти,
/// служби, тести, <c>curl</c>), а форму з чужого сайту без цих заголовків
/// сучасний браузер не надсилає.</item>
/// </list>
///
/// ⚠ Нового конфігу немає навмисно: довіреним є лише власний origin, тож
/// переліку дозволених хостів, який треба було б тримати в синхроні з
/// розгортанням, не існує. Перевіряються лише <c>POST</c>/<c>PUT</c>/<c>PATCH</c>/
/// <c>DELETE</c>; <c>GET</c>/<c>HEAD</c>/<c>OPTIONS</c> (отже й <c>/health/*</c>) — ні. Виняток — приймач звітів CSP:
/// браузер шле їх сам, і стану вони не змінюють.
///
/// ⚠ Код відмови наявний (<c>ECR-AUTH-0403</c>), нова лише причина
/// (<c>err.ECR-AUTH-0403.csrfOrigin</c>). Стоїть одразу ПІСЛЯ автентифікації:
/// cookie лише прочитано, нічого не змінено, а журнал відмов (ФВ-5.24) уже
/// знає, від чийого імені була спроба.
/// </remarks>
public sealed class CsrfOriginMiddleware(RequestDelegate next)
{
    /// <summary>Ключ причини відмови в каталозі рядків.</summary>
    public const string MessageKey = "err.ECR-AUTH-0403.csrfOrigin";

    /// <summary>Шляхи, які не перевіряються.</summary>
    private static readonly string[] Exempt = [CspSettings.DefaultCspReportUri];

    /// <summary>Перевіряє запит.</summary>
    /// <param name="context">Контекст запиту.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        if (IsStateChanging(request.Method)
            && !Exempt.Contains(request.Path.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            && IsCrossSite(request))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403",
                "Запит надіслано з іншого сайту; його відхилено.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = MessageKey,
                });
        }

        return next(context);
    }

    /// <summary><c>true</c> — браузер позначив запит як надісланий з чужого сайту.</summary>
    /// <param name="request">Запит.</param>
    internal static bool IsCrossSite(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
        {
            return !string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(site, "none", StringComparison.OrdinalIgnoreCase);
        }

        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && !IsSameHost(origin, request.Host)
            && !IsSameHost(origin, ForwardedHost(request));
    }

    /// <summary>Перший <c>X-Forwarded-Host</c> — зовнішній хост, який бачив браузер за проксі.</summary>
    /// <remarks>
    /// ⚠ Застосунок не довіряє <c>X-Forwarded-*</c> ні для чого іншого
    /// (<c>UseForwardedHeaders</c> не вмикається), і тут довіра не потрібна:
    /// заголовок лише ДОДАЄ ще один збіг для свого запиту. Підставити його з
    /// чужого сайту браузер не дасть без CORS-попереднього запиту, а CORS у
    /// застосунку не ввімкнено; не-браузер і так проходить без <c>Origin</c>.
    /// </remarks>
    private static HostString ForwardedHost(HttpRequest request)
    {
        var value = request.Headers["X-Forwarded-Host"].ToString();
        var comma = value.IndexOf(',', StringComparison.Ordinal);
        return new HostString((comma >= 0 ? value[..comma] : value).Trim());
    }

    private static bool IsSameHost(string origin, HostString host)
    {
        if (!host.HasValue
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // `Host` без порту — порт за замовчуванням тієї схеми, якою прийшов браузер.
        return host.Port is { } port ? port == uri.Port : uri.IsDefaultPort;
    }

    private static bool IsStateChanging(string method)
        => HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
