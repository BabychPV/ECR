using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;

namespace Ecr.Api.Auth;

/// <summary>
/// Два провайдери автентифікації, **одна** cookie застосунку (ФВ-6.1).
/// </summary>
/// <remarks>
/// ⚠ Пастка розгортання, яка коштує дня на першому деплої (ТЗ §13.5 п.7):
/// в IIS має бути **увімкнено анонімний доступ** і **вимкнено Windows
/// Authentication на рівні сайту**. Negotiate застосовується **тільки** на
/// <c>/api/v1/login/windows</c> через атрибут. Інакше локальні користувачі
/// не дійдуть навіть до форми входу — браузер попросить доменні облікові дані
/// на будь-якому запиті.
/// </remarks>
public static class AuthenticationSetup
{
    /// <summary>Claim із <c>SecurityStamp</c>: перевіряється на кожен запит.</summary>
    public const string SecurityStampClaim = "ecr:stamp";

    /// <summary>Claim з ідентифікатором користувача в нашій базі.</summary>
    public const string UserIdClaim = "ecr:uid";

    /// <summary>Налаштовує схеми автентифікації.</summary>
    public static IServiceCollection AddEcrAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var cookieName = configuration["Auth:CookieName"] ?? "ecr.session";
        var requireHttps = configuration.GetValue("Auth:RequireHttps", defaultValue: true);
        var slidingHours = configuration.GetValue("Auth:SlidingHours", defaultValue: 8);

        var builder = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = cookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = requireHttps
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(slidingHours);
                options.SlidingExpiration = true;

                // ⚠ Це API, а не MVC: редирект на сторінку входу перетворив би
                // 401 на 302 з HTML, і клієнт побачив би «успіх» замість відмови.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // ⚠ Negotiate реєструється УМОВНО, і це не зручність для тестів.
        // NegotiateHandler реалізує IAuthenticationRequestHandler, тобто його
        // HandleRequestAsync виконується на КОЖЕН запит, а не лише на
        // /api/v1/login/windows. Він вимагає IConnectionItemsFeature, якого
        // немає ні в TestServer, ні в reverse-proxy без Kestrel/HTTP.sys, — і
        // тоді 500 отримує будь-який запит, включно з /health/live (`Q-054`).
        //
        // За замовчуванням увімкнено: у проді за IIS він потрібен.
        if (configuration.GetValue("Auth:EnableNegotiate", defaultValue: true))
        {
            builder.AddNegotiate();
        }

        services.AddAuthorization();
        return services;
    }
}
