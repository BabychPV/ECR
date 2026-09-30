// src/Ecr.Api/Startup/HttpsTransport.cs

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.HttpsPolicy;

namespace Ecr.Api.Startup;

/// <summary>
/// Що застосунок знає про свій транспорт: чи завантажено сертифікат HTTPS і
/// коли він спливає. Читає перевірка <c>transport</c> (<c>/health/ready</c>).
/// </summary>
/// <param name="HttpsCertificateLoaded">Сертифікат HTTPS завантажено зі сховища й передано Kestrel.</param>
/// <param name="CertificateNotAfter">Кінець строку дії сертифіката (UTC); <c>null</c>, якщо сертифіката немає.</param>
public sealed record TransportState(bool HttpsCertificateLoaded, DateTimeOffset? CertificateNotAfter)
{
    /// <summary>Сертифіката немає (HTTP, або TLS завершується перед Kestrel).</summary>
    public static readonly TransportState NoCertificate = new(false, null);
}

/// <summary>
/// Прив'язка HTTPS до Kestrel за відбитком сертифіката з <c>LocalMachine\My</c>
/// (<c>D14-08</c>, <c>R-01</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Інсталятор піднімав лише <c>http://+:port</c>, а
/// <c>Auth:RequireHttps = true</c> (типово) робить cookie сеансу <c>Secure</c>:
/// з будь-якої іншої машини, ніж сервер, вхід «вдавався», а наступний запит
/// давав <c>401</c> — браузер не відсилає <c>Secure</c>-cookie по HTTP.
///
/// ⚠ Чому код завантаження, а не <c>Kestrel:Endpoints:Https:Certificate</c>.
/// Стандартна конфігурація Kestrel знає для сертифіката з файла <c>Path</c> +
/// <c>Password</c>, а зі сховища — <c>Subject</c> + <c>Store</c> + <c>Location</c>;
/// <b>відбитка там немає</b>. <c>Subject</c> — підрядок імені, тобто неоднозначний,
/// коли в сховищі кілька сертифікатів одного імені (старий і оновлений — рядовий
/// випадок при продовженні). Відбиток однозначний і вже є способом вибору
/// сертифіката в цьому продукті (<c>Auth:DataProtection:CertificateThumbprint</c>,
/// <c>deploy-ecr.ps1</c>, майстер), тож тут та сама ручка з тими самими
/// правилами, а не другий спосіб. Файл PFX із паролем — гірше: ще один секрет
/// на диску (D-11).
///
/// ⚠ Сертифікат виставляється як <c>ConfigureHttpsDefaults</c>: він діє для
/// кожної <c>https://</c>-адреси, яку хост отримав з <c>ASPNETCORE_URLS</c>
/// (це єдине, що пише <c>deploy-ecr.ps1</c>) — окремої секції
/// <c>Kestrel:Endpoints</c> не потрібно, а отже й другого місця, де порт міг би
/// розійтися з <c>ASPNETCORE_URLS</c>.
///
/// ⚠ Відбиток заданий, а сертифіката немає (або він без закритого ключа) —
/// відмова старту з причиною, а не тихий відкат до HTTP: той самий принцип, що
/// й для сертифіката Data Protection.
///
/// ⚠ Прострочений сертифікат старт НЕ зупиняє (служба, що не піднялася вночі
/// через строк, гірша за сторінку з попередженням браузера) — але про це
/// говорить <c>transport</c> на <c>/health/ready</c> і журнал старту.
/// </remarks>
public static partial class HttpsTransport
{
    /// <summary>Відбиток сертифіката HTTPS у <c>LocalMachine\My</c>; порожньо — Kestrel без сертифіката.</summary>
    public const string CertificateThumbprintKey = "Transport:Https:CertificateThumbprint";

    /// <summary>
    /// Порт HTTPS для перенаправлення з HTTP; нуль — без перенаправлення
    /// (слухає лише HTTPS, або HTTP-порту немає взагалі).
    /// </summary>
    public const string PortKey = "Transport:Https:Port";

    /// <summary>За скільки днів до кінця строку <c>transport</c> жовтіє.</summary>
    public const int ExpiryWarningDays = 30;

    /// <summary>Довжина відбитка SHA-1 у шістнадцяткових символах.</summary>
    private const int ThumbprintLength = 40;

    /// <summary>Відбиток без пробілів і нерозривних пробілів, великими літерами; порожній рядок — не задано.</summary>
    /// <param name="raw">Значення конфігурації або текст із вікна сертифіката Windows.</param>
    public static string Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? string.Empty
            : new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Результат вибору сертифіката: або сертифікат, або причина відмови.</summary>
    /// <param name="Certificate">Сертифікат із закритим ключем.</param>
    /// <param name="Problem">Причина відмови; <c>null</c>, якщо сертифікат придатний.</param>
    public sealed record Selection(X509Certificate2? Certificate, string? Problem);

    /// <summary>
    /// Вибирає сертифікат за відбитком з вже знайдених у сховищі кандидатів.
    /// </summary>
    /// <param name="rawThumbprint">Значення <see cref="CertificateThumbprintKey"/>.</param>
    /// <param name="find">Пошук у сховищі за нормалізованим відбитком (шов для тесту).</param>
    public static Selection Select(string? rawThumbprint, Func<string, IReadOnlyList<X509Certificate2>> find)
    {
        ArgumentNullException.ThrowIfNull(find);

        var thumbprint = Normalize(rawThumbprint);
        if (thumbprint.Length != ThumbprintLength || !thumbprint.All(Uri.IsHexDigit))
        {
            return new Selection(null, $"{CertificateThumbprintKey}: очікується відбиток із {ThumbprintLength} "
                + "шістнадцяткових символів (SHA-1, як у вікні сертифіката Windows або Cert:\\LocalMachine\\My).");
        }

        IReadOnlyList<X509Certificate2> found;
        try
        {
            found = find(thumbprint);
        }
        catch (Exception unreachable) when (unreachable is CryptographicException
                                                         or PlatformNotSupportedException
                                                         or UnauthorizedAccessException)
        {
            return new Selection(null, $"{CertificateThumbprintKey}: сховище LocalMachine\\My недоступне "
                + $"({unreachable.GetType().Name}). Зроби його доступним обліковому запису служби.");
        }

        if (found.Count == 0)
        {
            return new Selection(null, $"{CertificateThumbprintKey}: сертифіката з таким відбитком немає в "
                + "LocalMachine\\My. Імпортуй PFX (із закритим ключем) у сховище машини й перезапусти службу.");
        }

        var certificate = found[0];
        if (!certificate.HasPrivateKey)
        {
            return new Selection(null, $"{CertificateThumbprintKey}: сертифікат у LocalMachine\\My без закритого ключа — "
                + "TLS ним не підняти. Імпортуй PFX із закритим ключем і дай обліковому запису служби право його читати "
                + "(certlm.msc → Усі завдання → Керування закритими ключами).");
        }

        return new Selection(certificate, null);
    }

    /// <summary>Пошук за відбитком у <c>LocalMachine\My</c> (справжнє сховище).</summary>
    /// <param name="thumbprint">Нормалізований відбиток.</param>
    public static IReadOnlyList<X509Certificate2> FindInLocalMachine(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        // validOnly: false — строк дії оцінюється нижче й не зупиняє старт.
        return store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .ToList();
    }

    /// <summary>
    /// Прив'язує сертифікат HTTPS до Kestrel, якщо відбиток заданий; повертає стан для health.
    /// </summary>
    /// <param name="builder">Будівник застосунку (конфігурація вже містить змінні оточення).</param>
    /// <param name="find">Пошук у сховищі; <c>null</c> — справжній <c>LocalMachine\My</c> (шов для тесту).</param>
    /// <exception cref="InvalidOperationException">Відбиток заданий, а сертифікат непридатний.</exception>
    public static TransportState ConfigureEcrHttps(
        this WebApplicationBuilder builder, Func<string, IReadOnlyList<X509Certificate2>>? find = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var configured = builder.Configuration[CertificateThumbprintKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return TransportState.NoCertificate;
        }

        var selection = Select(configured, find ?? FindInLocalMachine);
        if (selection.Certificate is not { } certificate)
        {
            throw new InvalidOperationException(selection.Problem);
        }

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(
            https => https.ServerCertificate = certificate));

        var port = builder.Configuration.GetValue(PortKey, defaultValue: 0);
        if (port > 0)
        {
            builder.Services.Configure<HttpsRedirectionOptions>(options =>
            {
                options.HttpsPort = port;
                options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            });
        }

        return new TransportState(true, new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero));
    }

    /// <summary>Вмикає перенаправлення HTTP → HTTPS, якщо задано <see cref="PortKey"/>.</summary>
    /// <param name="app">Зібраний застосунок.</param>
    public static void UseEcrHttpsRedirection(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Configuration.GetValue(PortKey, defaultValue: 0) > 0)
        {
            app.UseHttpsRedirection();
        }
    }

    /// <summary>Пише в журнал старту стан транспорту: HTTP без Secure-cookie — Warning, сертифікат — Information/Warning.</summary>
    /// <param name="app">Зібраний застосунок.</param>
    public static void ReportTransport(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Startup");
        var state = app.Services.GetRequiredService<TransportState>();
        var requireHttps = app.Configuration.GetValue("Auth:RequireHttps", defaultValue: true);

        if (!requireHttps && app.Environment.IsProduction())
        {
            LogInsecureCookie(logger);
        }

        if (state.CertificateNotAfter is { } notAfter)
        {
            var days = (int)Math.Floor((notAfter - DateTimeOffset.UtcNow).TotalDays);
            if (days < 0)
            {
                LogCertificateExpired(logger, notAfter.UtcDateTime);
            }
            else if (days < ExpiryWarningDays)
            {
                LogCertificateExpiresSoon(logger, days, notAfter.UtcDateTime);
            }
            else
            {
                LogCertificateLoaded(logger, notAfter.UtcDateTime);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Транспорт: HTTP без HTTPS — cookie сеансу НЕ Secure (Auth:RequireHttps = false). Лише для стенда; "
        + "на майданчику — HTTPS із сертифікатом замовника (deploy-ecr.ps1 -HttpsThumbprint, 11-install-guide, «HTTPS»).")]
    private static partial void LogInsecureCookie(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Транспорт: HTTPS, сертифікат завантажено, діє до {NotAfter:yyyy-MM-dd} UTC.")]
    private static partial void LogCertificateLoaded(ILogger logger, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Транспорт: сертифікат HTTPS спливає через {Days} дн. ({NotAfter:yyyy-MM-dd} UTC). "
        + "Постав оновлений сертифікат у сховище комп'ютера (Cert:/LocalMachine/My) і повтори deploy-ecr.ps1 з новим -HttpsThumbprint.")]
    private static partial void LogCertificateExpiresSoon(ILogger logger, int days, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Транспорт: сертифікат HTTPS ПРОСТРОЧЕНИЙ ({NotAfter:yyyy-MM-dd} UTC): браузери відмовляться відкривати застосунок. "
        + "Постав оновлений сертифікат у сховище комп'ютера (Cert:/LocalMachine/My) і повтори deploy-ecr.ps1 з новим -HttpsThumbprint.")]
    private static partial void LogCertificateExpired(ILogger logger, DateTime notAfter);
}
