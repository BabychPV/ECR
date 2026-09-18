using System.Security.Cryptography.X509Certificates;
using Ecr.Api.Health;
using Ecr.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;

namespace Ecr.Api.Auth;

/// <summary>
/// Два провайдери автентифікації, **одна** cookie застосунку (ФВ-6.1).
/// </summary>
/// <remarks>
/// ⚠ Пастка розгортання, яка коштує дня на першому деплої (ТЗ §13.5 п.7).
/// Negotiate застосовується **тільки** на <c>/api/v1/login/windows</c> через
/// атрибут — уся решта маршрутів мусить лишатися анонімно досяжною для
/// автентифікації, інакше локальні користувачі не дійдуть навіть до форми
/// входу (браузер попросить доменні облікові дані на будь-якому запиті).
///
/// ⛔ Q-222 (аудит): цей коментар раніше описував модель IIS-фронтування
/// (анонімний доступ увімкнено, Windows Authentication на рівні сайту
/// вимкнено) — застаріло. Застосунок самостійно хоститься на Kestrel як
/// Windows-служба, без IIS/nginx (`docs/build/10-installer.md` §11.2);
/// той самий принцип обмеження Negotiate одним маршрутом лишається, просто
/// немає окремого рівня IIS, де його ще й треба вимкнути вручну.
/// </remarks>
public static class AuthenticationSetup
{
    /// <summary>Claim із <c>SecurityStamp</c>: перевіряється на кожен запит.</summary>
    public const string SecurityStampClaim = "ecr:stamp";

    /// <summary>Claim з ідентифікатором користувача в нашій базі.</summary>
    public const string UserIdClaim = "ecr:uid";

    /// <summary>Claim «пароль виданий разово» (ФВ-6.18).</summary>
    /// <remarks>
    /// ⚠ У cookie, а не запитом до бази на кожен запит. Прапорець знімається
    /// лише зміною пароля, а вона крутить <c>SecurityStamp</c> — тобто стара
    /// cookie з прапорцем перестає бути дійсною тієї ж миті.
    /// </remarks>
    public const string MustChangePasswordClaim = "ecr:mustchg";

    /// <summary>
    /// Відбиток сертифіката, яким шифруються ключі кільця (`MI-01`, `D14-08`).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремий ключ, а не `Kestrel:Endpoints:Https`, і це судження, а не
    /// недогляд: кроку «Транспорт» у майстрі ще немає (`W0.1`), а прив'язатися
    /// до того, чого немає, означало б написати читання неіснуючої форми
    /// конфігурації й назвати це підтримкою. Коли `W0.1` заведе транспорт,
    /// цей рядок отримає значення з того самого відбитка — заміна одного
    /// рядка, не переробка.
    /// </remarks>
    public const string CertificateThumbprintKey = "Auth:DataProtection:CertificateThumbprint";

    /// <summary>Налаштовує схеми автентифікації.</summary>
    public static IServiceCollection AddEcrAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddEcrDataProtection(services, configuration);

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

    /// <summary>
    /// Спільне кільце ключів DataProtection у базі (`MI-01`, `D-32`).
    /// </summary>
    /// <remarks>
    /// ⛔ Три виклики, і кожен закриває свою відмову, а не «налаштовує
    /// бібліотеку»:
    /// <list type="bullet">
    /// <item><c>SetApplicationName</c> — без нього ім'я застосунку береться з
    /// <c>IHostEnvironment.ApplicationName</c> і входить у ланцюжок призначення
    /// ключа. Два інстанси з різним іменем процесу (служба й той самий код під
    /// консоллю) мають РІЗНИЙ ланцюжок — спільна таблиця ключів їх не
    /// порятує.</item>
    /// <item><c>PersistKeysToDbContext</c> — без нього ключі лежать у профілі
    /// облікового запису; під сервісною обліковкою без завантаженого профілю
    /// вони ефемерні, і рестарт служби розлогінює всіх навіть на ОДНОМУ
    /// інстансі.</item>
    /// <item><c>ProtectKeysWithCertificate</c> — лише коли відбиток заданий.
    /// Інакше ключі лежать у таблиці відкрито, і саме це показує
    /// <c>/health/db</c>: тихий незахищений режим — те, чого директива прямо
    /// не хоче.</item>
    /// </list>
    ///
    /// ⚠ Відбиток заданий, а сертифіката немає — це відмова старту, а не
    /// відкат до незахищеного режиму. Мовчазний відкат дав би систему, яка
    /// вважає себе захищеною, і адміністратор дізнався б про це не з health, а
    /// з аудиту.
    /// </remarks>
    private static void AddEcrDataProtection(IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName("Ecr")
            .PersistKeysToDbContext<EcrDbContext>();

        var thumbprint = configuration[CertificateThumbprintKey];
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            services.AddSingleton(DataProtectionKeyProtection.Unprotected);
            return;
        }

        builder.ProtectKeysWithCertificate(FindCertificate(thumbprint));
        services.AddSingleton(DataProtectionKeyProtection.ProtectedBy(thumbprint));
    }

    /// <summary>Сертифікат за відбитком у <c>LocalMachine\My</c> (`D14-08`).</summary>
    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        // Пробіли й нерозривні пробіли: відбиток зазвичай копіюють із вікна
        // сертифіката Windows, де він надрукований групами по два символи.
        var normalized = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray());

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        var found = store.Certificates
            .Find(X509FindType.FindByThumbprint, normalized, validOnly: false);

        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                $"{CertificateThumbprintKey} = '{thumbprint}': сертифіката з таким відбитком немає в "
                + "LocalMachine\\My. Або постав сертифікат, або прибери ключ — тоді ключі кільця "
                + "лежатимуть у sec.DataProtectionKey відкрито, і /health/db про це скаже.");
        }

        return found[0];
    }
}
