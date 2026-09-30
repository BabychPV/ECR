// src/Ecr.Api/Security/SecurityHeadersMiddleware.cs

namespace Ecr.Api.Security;

/// <summary>
/// Заголовки безпеки на <b>кожну</b> відповідь (<c>S-22</c>): і на JSON API, і
/// на <c>problem+json</c>, і на статику SPA.
/// </summary>
/// <remarks>
/// ⛔ Предмет. У конвеєрі не було ні <c>UseHsts</c>, ні
/// <c>X-Content-Type-Options</c>, ні <c>X-Frame-Options</c>, ні
/// <c>Referrer-Policy</c>, ні CSP. Той самий Kestrel віддає і API, і сторінку
/// застосунку, тому це не «заголовки для API» — це заголовки робочого місця
/// користувача: без <c>nosniff</c> завантажений користувачем файл може бути
/// виконаний браузером як скрипт з НАШОГО походження, без
/// <c>frame-ancestors</c>/<c>X-Frame-Options</c> сторінку можна вкласти в чужий
/// фрейм і клікати за користувача.
///
/// ⛔ Заголовки ставляться через <see cref="HttpResponse.OnStarting(Func{Task})"/>,
/// а не присвоєнням до виклику <c>next</c>. Причина конкретна і вже сплачена
/// одного разу в <c>CorrelationIdMiddleware</c>:
/// <c>ExceptionHandlingMiddleware.WriteAsync</c> робить
/// <c>Response.Clear()</c> — він стирає ВСІ заголовки, зокрема й наші. Написані
/// «наперед», вони зникали б рівно з <c>problem+json</c>, тобто з тих
/// відповідей, де їх найлегше не помітити. <c>OnStarting</c> виконується в
/// момент, коли відповідь уже сформована й починає йти в мережу, тобто ПІСЛЯ
/// будь-якого <c>Clear()</c>.
///
/// ⚠ HSTS — лише коли запит справді прийшов HTTPS. Інсталятор сьогодні піднімає
/// <c>http://+:port</c> (<c>R-01</c>, рішення <c>D14-08</c> тільки вводить вибір
/// транспорту), а <c>Strict-Transport-Security</c>, відданий поверх HTTP,
/// браузер зобов'язаний ІГНОРУВАТИ — але той самий заголовок, випадково відданий
/// з HTTPS-стенда для того ж імені хоста, замикає домен на HTTPS на рік уперед,
/// і повернути його назад можна лише з боку клієнта. Тому умова — на запиті, а
/// не на конфігурації: майданчик, який перейшов на HTTPS (<c>D14-08</c>,
/// варіант «а»), отримує HSTS автоматично і без окремої ручки, а майданчик на
/// HTTP не отримує його ніколи.
///
/// ⚠ <c>includeSubDomains</c>/<c>preload</c> свідомо НЕМАЄ: ECR — один вузол у
/// домені замовника, і поширювати вимогу HTTPS на сусідні імена того ж домену
/// ми не маємо права.
///
/// ⛔ CSP розділена на дві половини, і це головне рішення цього файлу.
/// <b>Примусова</b> частина містить лише директиви, які фізично не можуть
/// зламати цей SPA: <c>frame-ancestors</c> (застосунок ніде не вкладається у
/// фрейм), <c>base-uri</c> (у <c>index.html</c> немає <c>&lt;base&gt;</c>),
/// <c>object-src</c> (плагінів немає) і <c>form-action</c> (жодна форма не
/// постить на чуже походження — клієнт ходить <c>fetch</c>'ем на свій же
/// сервер). <b>Звітна</b> (<c>Report-Only</c>) частина додає
/// <c>default-src</c>/<c>script-src</c>/<c>style-src</c> — саме ті директиви,
/// що ламають застосунок МОВЧКИ: у клієнті є 16 місць із <c>style={{…}}</c>
/// (це <c>style-src-attr 'unsafe-inline'</c>), динамічно створені
/// <c>&lt;style&gt;</c> і одне <c>blob:</c>-завантаження вивантаженого файлу.
/// Довести тестом, що клієнт живий під примусовою версією цих директив, тут
/// неможливо (потрібен браузерний прогін <c>e2e-stand.ps1</c>), а «здається,
/// працює» — не доказ. Тому вони їдуть звітними: порушення видно в консолі
/// браузера, і жодна сторінка від них не гасне.
///
/// ⛔ <c>S14</c>: звітна політика тепер ВИМІРНА. До <c>report-uri</c> порушення
/// бачив лише той, хто відкрив консоль браузера, тобто ніхто. Звіти йдуть на
/// <c>POST /api/v1/csp-report</c> (<c>CspReportController</c>). Ручки —
/// <see cref="CspSettings"/> (<c>Security:Csp:*</c>); <c>Enforce</c> лише
/// задел, типово вимкнений.
///
/// ⚠ <c>report-to</c>/<c>Reporting-Endpoints</c> — лише на HTTPS-запиті, за тим
/// самим міркуванням, що й HSTS. Reporting API працює тільки в захищеному
/// контексті, а Chrome, побачивши <c>report-to</c>, ІГНОРУЄ <c>report-uri</c>:
/// на сьогоднішньому <c>http://</c> (<c>R-01</c>) обидва канали мовчали б.
/// Тому поверх HTTP їде лише <c>report-uri</c>, поверх HTTPS — обидва.
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>Примусова CSP: лише директиви, що не можуть зламати SPA.</summary>
    /// <remarks>Байт-у-байт незмінна: <c>S14</c> її не чіпає.</remarks>
    public const string ContentSecurityPolicy =
        "base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'";

    /// <summary>
    /// Звітна CSP — повна, сувора: те, що ще не доведене браузерним прогоном.
    /// </summary>
    /// <remarks>
    /// ⚠ Узгоджена зі збіркою клієнта (Vite): жодних вбудованих скриптів
    /// (<c>index.html</c> має один <c>&lt;script type="module" src&gt;</c>), шрифти
    /// self-hosted (<c>@fontsource</c>), <c>connect-src</c> — той самий сервер.
    /// <c>style-src 'unsafe-inline'</c> лишається: Mantine пише <c>&lt;style&gt;</c> і
    /// <c>style=""</c> під час виконання.
    /// </remarks>
    public const string ReportOnlyContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; font-src 'self'; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    /// <summary>Ім'я групи звітів у <c>Reporting-Endpoints</c> і в <c>report-to</c>.</summary>
    public const string ReportingGroup = "csp-endpoint";

    /// <summary>Заголовок Reporting API.</summary>
    public const string ReportingEndpointsHeader = "Reporting-Endpoints";

    /// <summary>HSTS на рік, без <c>includeSubDomains</c> і <c>preload</c>.</summary>
    public const string StrictTransportSecurity = "max-age=31536000";

    /// <summary>Заголовок політики реферера.</summary>
    public const string ReferrerPolicyHeader = "Referrer-Policy";

    /// <summary>Значення політики реферера.</summary>
    /// <remarks>
    /// ⚠ <c>no-referrer</c>, а не <c>strict-origin-when-cross-origin</c>: шляхи
    /// ECR містять ідентифікатори документів і періодів, і віддавати їх чужому
    /// сайту навіть походженням немає жодної потреби — зовнішніх посилань у
    /// застосунку немає.
    /// </remarks>
    public const string ReferrerPolicy = "no-referrer";

    private readonly RequestDelegate _next;
    private readonly CspSettings _csp;

    /// <summary>Політика, що їде примусово: типово базова, за <c>Enforce</c> — повна.</summary>
    private readonly PolicyPair _enforced;

    /// <summary>Звітна політика; <c>null</c> — звітного заголовка немає.</summary>
    private readonly PolicyPair? _reportOnly;

    /// <summary>Створює middleware; політики збираються один раз, на старті.</summary>
    /// <param name="next">Наступний обробник конвеєра.</param>
    /// <param name="configuration">Конфігурація застосунку (<c>Security:Csp:*</c>).</param>
    public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
        _csp = CspSettings.From(configuration);

        // ⚠ Значення, що не пройшло перевірку, ігнорується, а не дописується в
        // заголовок: старт із таким значенням і так падає
        // (`EcrConfigurationValidation`), це друга смуга для тестового хоста.
        var reportUri = CspSettings.IsValidReportUri(_csp.ReportUri) ? _csp.ReportUri : string.Empty;

        if (_csp.Enforce)
        {
            _enforced = PolicyPair.For(ReportOnlyContentSecurityPolicy, reportUri);
            _reportOnly = null;
        }
        else
        {
            _enforced = new PolicyPair(ContentSecurityPolicy, ContentSecurityPolicy, null);
            _reportOnly = _csp.ReportOnly
                ? PolicyPair.For(ReportOnlyContentSecurityPolicy, reportUri)
                : null;
        }
    }

    /// <summary>Обробляє запит.</summary>
    /// <param name="context">Контекст запиту.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ⚠ Читається ДО next(): усередині конвеєра ніхто схему не змінює, а
        // читання всередині зворотного виклику прив'язало б нас до стану
        // запиту в момент, коли його вже могли переписати.
        var isHttps = context.Request.IsHttps;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers[ReferrerPolicyHeader] = ReferrerPolicy;
            headers.ContentSecurityPolicy = isHttps ? _enforced.Https : _enforced.Http;

            if (_reportOnly is { } reportOnly)
            {
                headers.ContentSecurityPolicyReportOnly = isHttps ? reportOnly.Https : reportOnly.Http;
            }

            // ⚠ Група для `report-to` оголошується лише разом із самою директивою
            // (HTTPS) і лише коли є куди слати.
            if (isHttps && _csp.ReportUri.Length > 0 && CspSettings.IsValidReportUri(_csp.ReportUri)
                && (_reportOnly is not null || _csp.Enforce))
            {
                headers[ReportingEndpointsHeader] = $"{ReportingGroup}=\"{_csp.ReportUri}\"";
            }

            if (isHttps)
            {
                headers.StrictTransportSecurity = StrictTransportSecurity;
            }

            return Task.CompletedTask;
        });

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>Готовий текст політики для HTTP і HTTPS-запиту.</summary>
    /// <param name="Http">Значення заголовка поверх HTTP (лише <c>report-uri</c>).</param>
    /// <param name="Https">Значення заголовка поверх HTTPS (<c>report-uri</c> і <c>report-to</c>).</param>
    /// <param name="ReportUri">Адреса звітів; <c>null</c> — політика без звітування.</param>
    private sealed record PolicyPair(string Http, string Https, string? ReportUri)
    {
        public static PolicyPair For(string policy, string reportUri)
            => reportUri.Length == 0
                ? new PolicyPair(policy, policy, null)
                : new PolicyPair(
                    $"{policy}; report-uri {reportUri}",
                    $"{policy}; report-uri {reportUri}; report-to {ReportingGroup}",
                    reportUri);
    }
}
