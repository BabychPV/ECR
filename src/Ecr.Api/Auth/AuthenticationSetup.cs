using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Ecr.Api.Health;
using Ecr.Api.Startup;
using Ecr.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

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
public static partial class AuthenticationSetup
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

    /// <summary>Claim із відкритим сеансом симуляції «очима користувача» (ФВ-6.16a, V-06).</summary>
    /// <remarks>
    /// ⚠ У cookie, а не запитом «чи є відкритий сеанс» на КОЖЕН запит: сеанс
    /// рідкісний, і платити за нього мають лише ті запити, що його несуть.
    /// Ставиться на початку сеансу, знімається завершенням; вихід закриває сеанс.
    /// </remarks>
    public const string SimulationSessionClaim = "ecr:sim";

    /// <summary>Claim із моментом ВХОДУ (Unix-секунди, UTC) — для абсолютної межі сесії (S21).</summary>
    /// <remarks>
    /// ⚠ Не <c>AuthenticationProperties.IssuedUtc</c>: за ковзного строку той
    /// оновлюється на кожному продовженні cookie і входу вже не пам'ятає.
    /// Claim ставиться один раз на вході (<c>OnSigningIn</c>) і переноситься
    /// перевиданнями cookie (штамп, симуляція), бо ті копіюють усі заявки.
    /// </remarks>
    public const string AuthTimeClaim = "ecr:authtime";

    /// <summary>
    /// Абсолютна межа сесії від входу, незалежно від активності (S21).
    /// </summary>
    /// <remarks>
    /// ⛔ Ковзний строк (<c>Auth:SlidingHours</c>) сам по собі продовжує
    /// сесію безкінечно, доки нею користуються: викрадена cookie, яку
    /// «підтримують» запитами, не вмирала ніколи. Дванадцять годин — робоча
    /// зміна з запасом: людина входить раз на день, а сесія довша за добу вже
    /// не є «тією самою людиною за тим самим столом». Константа, а не
    /// конфігурація, навмисно: межа безпеки не має тихо вимикатися ключем.
    /// </remarks>
    public static readonly TimeSpan AbsoluteSessionLifetime = TimeSpan.FromHours(12);

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
    public static IServiceCollection AddEcrAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<string, X509Certificate2?>? certificateLookup = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddEcrDataProtection(services, configuration, certificateLookup ?? FindInLocalMachine);

        // Дефолт = значення з appsettings.json: у продукт завжди їхало `ecr.auth`,
        // зміна імені розлогінила б усіх при оновленні.
        var cookieName = configuration["Auth:CookieName"] ?? "ecr.auth";
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

                // S21: момент входу — у cookie, абсолютна межа — на кожному запиті.
                options.Events.OnSigningIn = StampAuthTime;
                options.Events.OnValidatePrincipal = RejectIfPastAbsoluteLifetimeAsync;
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

    /// <summary>Ставить момент входу, якщо його ще немає (перший вхід, не перевидання).</summary>
    /// <param name="context">Контекст підпису cookie.</param>
    private static Task StampAuthTime(CookieSigningInContext context)
    {
        if (context.Principal?.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == AuthTimeClaim))
        {
            var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
            identity.AddClaim(new Claim(
                AuthTimeClaim, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        return Task.CompletedTask;
    }

    /// <summary>Відкидає cookie, чий вхід старший за <see cref="AbsoluteSessionLifetime"/>.</summary>
    /// <param name="context">Контекст перевірки cookie.</param>
    /// <remarks>
    /// ⚠ Cookie без моменту входу теж відкидається: інакше cookie, видана до
    /// появи межі, жила б за ковзним строком безкінечно. Ціна одноразова —
    /// після розгортання кожен увійде заново.
    /// </remarks>
    private static async Task RejectIfPastAbsoluteLifetimeAsync(CookieValidatePrincipalContext context)
    {
        var raw = context.Principal?.FindFirst(AuthTimeClaim)?.Value;
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && now - DateTimeOffset.FromUnixTimeSeconds(seconds) < AbsoluteSessionLifetime)
        {
            return;
        }

        // Принципал знятий — запит іде далі анонімним і на [Authorize] отримує
        // 401 (`OnRedirectToLogin`); мертва cookie стирається тією ж відповіддю.
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
                     .ConfigureAwait(false);
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
    /// ⛔ S11 (аудит безпеки): у Production незахищене кільце — відмова старту
    /// (<see cref="UnprotectedInProductionMessage"/>), бо відкритий ключ у
    /// таблиці чи бекапі дає підробку cookie <c>ecr.auth</c> з <c>ecr:uid</c>
    /// будь-кого. Захист — лише сертифікат: <c>ProtectKeysWithDpapi</c> (ключ
    /// машини) на двох вузлах за балансувальником (D-32) дав би кожному вузлу
    /// ключі, яких інший не розшифрує. Development і тести — як були.
    ///
    /// ⚠ Відбиток заданий, а сертифіката немає — це відмова старту, а не
    /// відкат до незахищеного режиму. Мовчазний відкат дав би систему, яка
    /// вважає себе захищеною, і адміністратор дізнався б про це не з health, а
    /// з аудиту.
    /// </remarks>
    private static void AddEcrDataProtection(
        IServiceCollection services, IConfiguration configuration, Func<string, X509Certificate2?> lookup)
    {
        var builder = services.AddDataProtection()
            .SetApplicationName("Ecr")
            .PersistKeysToDbContext<EcrDbContext>();

        // ⛔ S11: у Production незахищене кільце — відмова старту, а не рядок у
        // health. Перевіряється ФАКТ (`XmlEncryptor` зібраних опцій), а не
        // наявність ключа конфігурації — тим самим принципом, що й
        // `DataProtectionKeyProtection`: «що застосунок зробив».
        var allowUnprotected = configuration.GetValue(AllowUnprotectedKeysKey, defaultValue: false);
        services.AddOptions<KeyManagementOptions>()
            .Validate<IHostEnvironment, ILoggerFactory>(
                (options, environment, loggers) => CheckKeyProtection(environment, options, allowUnprotected, loggers),
                UnprotectedInProductionMessage)
            .ValidateOnStart();

        var thumbprint = configuration[CertificateThumbprintKey];
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            services.AddSingleton(DataProtectionKeyProtection.Unprotected);
            return;
        }

        var current = FindCertificate(thumbprint, lookup);
        builder.ProtectKeysWithCertificate(current);

        // D-267: після заміни сертифіката старі ключі кільця лишаються зашифрованими
        // СТАРИМ сертифікатом. Без `UnprotectKeysWithAnyCertificate` вони нечитабельні
        // безповоротно (сеанси, секрети каналів), і відмова тиха. Ненайдений попередній
        // відбиток — Warning, не відмова старту: поточний сертифікат уже обов'язковий.
        var readable = new List<string> { HttpsTransport.Normalize(thumbprint) };
        var missingPrevious = new List<string>();
        var previousCertificates = new List<X509Certificate2>();
        foreach (var previous in ParsePreviousThumbprints(configuration))
        {
            var certificate = lookup(previous);
            if (certificate is null)
            {
                missingPrevious.Add(previous);
                continue;
            }

            previousCertificates.Add(certificate);
            readable.Add(previous);
        }

        if (previousCertificates.Count > 0)
        {
            builder.UnprotectKeysWithAnyCertificate([.. previousCertificates]);
        }

        if (missingPrevious.Count > 0)
        {
            services.AddOptions<KeyManagementOptions>()
                .Validate<ILoggerFactory>(
                    (_, loggers) =>
                    {
                        LogMissingPrevious(
                            loggers.CreateLogger("Ecr.Startup"), PreviousCertificateThumbprintsKey,
                            string.Join(", ", missingPrevious));
                        return true;
                    });
        }

        services.AddSingleton(DataProtectionKeyProtection.ProtectedBy(thumbprint, readable));
    }

    /// <summary>
    /// Відбитки сертифікатів, якими раніше шифрувалось кільце ключів Data Protection
    /// (масив або список через <c>;</c>/<c>,</c>). Порожньо — поведінка без змін (D-267).
    /// </summary>
    public const string PreviousCertificateThumbprintsKey = "Auth:DataProtection:PreviousCertificateThumbprints";

    /// <summary>Нормалізовані відбитки попередніх сертифікатів із конфігурації.</summary>
    public static IReadOnlyList<string> ParsePreviousThumbprints(IConfiguration configuration)
    {
        var section = configuration.GetSection(PreviousCertificateThumbprintsKey);
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            raw.AddRange(section.Value.Split(';', ','));
        }

        raw.AddRange(section.GetChildren().Select(c => c.Value ?? string.Empty));

        return raw.Select(HttpsTransport.Normalize)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Key}: сертифіката(ів) {Thumbprints} немає у сховищі сертифікатів — ключі кільця, зашифровані ними, залишаться нечитабельними.")]
    private static partial void LogMissingPrevious(ILogger logger, string key, string thumbprints);

    /// <summary>Пошук сертифіката за відбитком у <c>LocalMachine\My</c>; <c>null</c> — немає.</summary>
    internal static X509Certificate2? FindInLocalMachine(string thumbprint)
    {
        var normalized = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray());
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>
    /// Явна згода на незахищене кільце в Production — лише для одноразових
    /// стендів (`tools/smoke.ps1`, `tools/e2e-stand.ps1`, `tools/setup-dev-db.ps1`).
    /// </summary>
    /// <remarks>
    /// ⚠ Не тиха лазівка для майданчика: із цим ключем у Production старт
    /// пише Critical із причиною, а перевірка <c>db</c> — <c>Degraded</c> із
    /// тією самою причиною. <c>deploy-ecr.ps1</c> цей ключ не ставить ніколи —
    /// це тримає сторож <c>DeployScriptNeverAllowsUnprotectedKeysTests</c>.
    /// </remarks>
    public const string AllowUnprotectedKeysKey = "Auth:DataProtection:AllowUnprotectedKeys";

    /// <summary>Текст відмови старту в Production без захисту ключів (S11).</summary>
    public const string UnprotectedInProductionMessage =
        "Production: ключі кільця DataProtection у sec.DataProtectionKey не захищені — служба не стартує. "
        + "Хто читає базу або її бекап, той підробляє cookie сеансу будь-якого користувача. "
        + "Задай " + CertificateThumbprintKey + " (змінна ECR_Auth__DataProtection__CertificateThumbprint) — "
        + "відбиток сертифіката з закритим ключем у LocalMachine\\My, ОДНОГО для всіх вузлів "
        + "(DPAPI машини не підходить: вузлів за балансувальником два й більше, D-32). "
        + "Лише для одноразового стенда: " + AllowUnprotectedKeysKey + " = true (тоді старт пише Critical, "
        + "а /health/db — Degraded).";

    /// <summary>Текст Critical на старті, коли Production іде без захисту за явною згодою (S11).</summary>
    public const string UnprotectedByConsentMessage =
        "Production: ключі кільця DataProtection у sec.DataProtectionKey НЕ захищені — старт дозволено лише "
        + "явною згодою " + AllowUnprotectedKeysKey + " = true (одноразовий стенд). Хто читає базу або її бекап, "
        + "той підробляє cookie сеансу будь-якого користувача. На майданчику: прибери згоду й задай "
        + CertificateThumbprintKey + ".";

    /// <summary>
    /// Перевірка вимоги захисту ключів на старті (S11): <c>false</c> — відмова
    /// старту; згода в Production — Critical у журнал і старт.
    /// </summary>
    /// <param name="environment">Середовище хоста.</param>
    /// <param name="options">Зібрані опції керування ключами.</param>
    /// <param name="allowUnprotected">Значення <see cref="AllowUnprotectedKeysKey"/>.</param>
    /// <param name="loggers">Фабрика журналів.</param>
    internal static bool CheckKeyProtection(
        IHostEnvironment environment, KeyManagementOptions options, bool allowUnprotected, ILoggerFactory loggers)
    {
        if (!environment.IsProduction() || options.XmlEncryptor is not null)
        {
            return true;
        }

        if (!allowUnprotected)
        {
            return false;
        }

        var logger = loggers.CreateLogger("Ecr.Startup");
        LogUnprotectedByConsent(logger, UnprotectedByConsentMessage);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Message}")]
    private static partial void LogUnprotectedByConsent(ILogger logger, string message);

    /// <summary>Сертифікат за відбитком у <c>LocalMachine\My</c> (`D14-08`).</summary>
    private static X509Certificate2 FindCertificate(string thumbprint, Func<string, X509Certificate2?> lookup)
    {
        // Пробіли й нерозривні пробіли: відбиток зазвичай копіюють із вікна
        // сертифіката Windows, де він надрукований групами по два символи.
        var normalized = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray());

        X509Certificate2? found;

        // ⛔ Недосяжне сховище — той самий наслідок, що й відсутній сертифікат:
        // налаштований захист застосувати НЕМОЖЛИВО. Тому й відмова та сама, а
        // не сирий `CryptographicException` із конвеєра старту.
        //
        // ⚠ Знайдено гейтом, не міркуванням: на Linux-раннері CI
        // `LocalMachine\My` не відкривається взагалі, і тест «заданий, але
        // відсутній відбиток валить старт» падав — не тому, що продукт не
        // валив старт, а тому, що валив його ІНШИМ винятком. Локально на
        // Windows цього не видно.
        try
        {
            found = lookup(normalized);
        }
        catch (Exception unreachable) when (unreachable is System.Security.Cryptography.CryptographicException
                                                         or PlatformNotSupportedException
                                                         or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{CertificateThumbprintKey} = '{thumbprint}': сховище LocalMachine\\My недоступне "
                + $"({unreachable.GetType().Name}: {unreachable.Message}). Або зроби його доступним "
                + "обліковому запису служби, або (лише поза Production) прибери ключ — тоді ключі кільця "
                + "лежатимуть у sec.DataProtectionKey відкрито, і /health/db про це скаже.",
                unreachable);
        }

        if (found is null)
        {
            throw new InvalidOperationException(
                $"{CertificateThumbprintKey} = '{thumbprint}': сертифіката з таким відбитком немає в "
                + "LocalMachine\\My. Або постав сертифікат, або (лише поза Production) прибери ключ — тоді "
                + "ключі кільця лежатимуть у sec.DataProtectionKey відкрито, і /health/db про це скаже.");
        }

        return found;
    }
}
