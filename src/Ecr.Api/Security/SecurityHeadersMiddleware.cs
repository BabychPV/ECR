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
/// </remarks>
/// <param name="next">Наступний обробник конвеєра.</param>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>Примусова CSP: лише директиви, що не можуть зламати SPA.</summary>
    public const string ContentSecurityPolicy =
        "base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'";

    /// <summary>Звітна CSP: те, що ще не доведене браузерним прогоном.</summary>
    public const string ReportOnlyContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'";

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
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.ContentSecurityPolicyReportOnly = ReportOnlyContentSecurityPolicy;

            if (isHttps)
            {
                headers.StrictTransportSecurity = StrictTransportSecurity;
            }

            return Task.CompletedTask;
        });

        await next(context).ConfigureAwait(false);
    }
}
