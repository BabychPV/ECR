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
    /// <summary>Налаштовує схеми автентифікації.</summary>
    public static IServiceCollection AddEcrAuthentication(this IServiceCollection services, IConfiguration configuration)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)\n" +
            "  .AddCookie(o => { o.Cookie.Name = Auth:CookieName; o.Cookie.HttpOnly = true;\n" +
            "      o.Cookie.SameSite = SameSiteMode.Strict;\n" +
            "      o.Cookie.SecurePolicy = Auth:RequireHttps ? Always : SameAsRequest;\n" +
            "      o.ExpireTimeSpan = TimeSpan.FromHours(Auth:SlidingHours);\n" +
            "      o.SlidingExpiration = true;\n" +
            "      o.Events.OnRedirectToLogin = 401 замість редиректу — це API, не MVC; })\n" +
            "  .AddNegotiate();\n" +
            "Обидва endpoint'и входу підписують ТУ САМУ cookie з тими самими claims — " +
            "нижче рівня входу авторизація не знає, як користувач увійшов (ФВ-6.2).");
}
