// src/Ecr.Api/Security/LoginRateLimiting.cs

using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
using Ecr.Application.Common;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Api.Security;

/// <summary>
/// Обмеження частоти на анонімні ДОРОГІ шляхи (<c>S-10</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>POST /api/v1/login/local</c> анонімний і коштує 210 000
/// ітерацій PBKDF2 — причому й для НЕІСНУЮЧОГО імені: там навмисний
/// <c>Decoy</c>-хеш, щоб відповідь не розрізняла «немає такого» і «не той
/// пароль». Тобто вартість запиту не залежить від того, чи вгадав нападник хоч
/// щось. Блокування облікового запису (<c>ФВ-6.4a</c>) тут не рятує: воно
/// захищає ОБЛІКОВКУ, а процесор сервера з'їдають спроби під іменами, яких
/// немає, і жодне блокування на них не спрацьовує.
///
/// ⚠ Обмежувач — глобальний (<c>GlobalLimiter</c>), а не атрибут
/// <c>[EnableRateLimiting]</c> на дії. Причина не стильова: атрибут працює лише
/// після <c>UseRouting</c>, тобто дорогий шлях спершу пройшов би добір
/// маршруту, прив'язку моделі й фільтри — а сенс саме в тому, щоб скинути
/// зайве навантаження якомога раніше. Заразом межа лишається в одному файлі
/// поруч із поясненням, а не розповзається атрибутами по контролерах.
///
/// ⚠ Вікно фіксоване (одна хвилина) і черги немає (<c>QueueLimit = 0</c>):
/// черга під атакою — це пам'ять, яку нападник наповнює безкоштовно.
/// </remarks>
public static class LoginRateLimiting
{
    /// <summary>Скільки спроб входу з однієї адреси дозволено за хвилину.</summary>
    public const int DefaultLoginPermitPerMinute = 10;

    /// <summary>Префікс шляхів входу — єдине, що обмежується.</summary>
    /// <remarks>
    /// ⚠ Обидва входи, не лише локальний. <c>login/windows</c> вимагає
    /// Negotiate, тобто анонімним не є, але так само ходить у базу на кожен
    /// виклик; спільний префікс тримає правило одним, а не двома, що розійдуться.
    /// </remarks>
    public const string LoginPathPrefix = "/api/v1/login";

    /// <summary>Ключ конфігурації: межа спроб за хвилину.</summary>
    private const string PermitKey = "Security:RateLimit:LoginPermitPerMinute";

    /// <summary>Ключ конфігурації: чи довіряти <c>X-Forwarded-For</c>.</summary>
    private const string TrustForwardedForKey = "Security:RateLimit:TrustForwardedFor";

    /// <summary>Заголовок зворотного проксі з адресою початкового клієнта.</summary>
    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Розділ, у який складаються НЕобмежувані запити.</summary>
    private const string UnlimitedPartition = "unlimited";

    /// <summary>Ключ розділу, коли адреси клієнта немає (наприклад, у тестовому хості).</summary>
    private const string UnknownClient = "unknown";

    /// <summary>
    /// Код відмови.
    /// </summary>
    /// <remarks>
    /// ⛔ Чесно: потрібного коду в каталозі <c>02-contracts.md</c> §7 НЕМАЄ —
    /// сімейства <c>ECR-AUTH-0429</c> не існує, а завести його тут неможливо,
    /// бо <c>ContractIntegrityTests</c> звіряє коди з таблицею контракту в
    /// обидва боки, а сама таблиця й рядки каталогу (<c>09-seed.sql</c>)
    /// лежать поза межами цієї роботи. Тому взято найближчий НАЯВНИЙ:
    /// <c>ECR-AUTH-0423</c> — те саме сімейство (клієнт маршрутизує відмову за
    /// сімейством) і той самий клас стану «автентифікацію відхилено не через
    /// пароль, спробуйте пізніше». Розбіжність реальна й названа: заблоковано
    /// не обліковку, а адресу. Заводити <c>ECR-AUTH-0429</c> — окрема робота
    /// (таблиця §7 + рядок сіду + цей рядок).
    /// </remarks>
    public const string RejectionCode = ErrorCodes.AccountLocked;

    /// <summary>
    /// Подробиця відмови.
    /// </summary>
    /// <remarks>
    /// ⛔ Чесно і тут: <c>messageKey</c> у цій відповіді НЕМАЄ, і це не
    /// недогляд. Власний ключ подробиці (<c>err.ECR-AUTH-0429.tooManyAttempts</c>
    /// або хоча б <c>err.ECR-AUTH-0423.tooManyAttempts</c>) мусить бути
    /// заведений у <c>09-seed.sql</c> — інакше сторож
    /// <c>ErrorTitleCatalogTests.Кожен_messageKey_із_коду_заведено_в_сіді</c>
    /// червоніє, і правильно робить: ключ, якого немає в каталозі, не
    /// локалізується ніде й ніколи, а виглядає так, ніби локалізований. Сід
    /// лежить поза межами цієї роботи, тож замість вигаданого ключа тут іде
    /// готове речення. Заводиться це одним рядком сіду разом із кодом
    /// <c>ECR-AUTH-0429</c>.
    ///
    /// ⚠ Англійською, а не українською, як більшість запасних речень у цьому
    /// коді: ті призначені підмінятися каталогом, а це доїде до користувача як
    /// є. Мова збірки — <c>en</c> (<c>ФВ-14.9</c>), української серед мов
    /// продукту немає.
    /// </remarks>
    private const string RejectionDetail =
        "Too many sign-in attempts from this address. Try again later.";

    /// <summary>Тип вмісту відповіді про відмову.</summary>
    private const string ProblemJson = "application/problem+json";

    /// <summary>Налаштування серіалізації — ті самі, що в обробнику помилок.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Реєструє обмежувач частоти.</summary>
    /// <param name="services">Колекція служб.</param>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <returns>Та сама колекція.</returns>
    public static IServiceCollection AddEcrRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var permitPerMinute = configuration.GetValue(PermitKey, DefaultLoginPermitPerMinute);
        var trustForwardedFor = configuration.GetValue(TrustForwardedForKey, defaultValue: false);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments(LoginPathPrefix, StringComparison.OrdinalIgnoreCase)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        ClientKey(context, trustForwardedFor),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        })
                    : RateLimitPartition.GetNoLimiter(UnlimitedPartition));

            options.OnRejected = static async (rejection, ct) =>
            {
                var context = rejection.HttpContext;
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // ⚠ `Retry-After` — не косметика: без нього клієнт (і будь-який
                // скрипт розгортання) може лише вгадувати, коли повторити, і
                // типово вгадує «зараз», тобто продовжує те саме навантаження.
                if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await WriteProblemAsync(context, ct).ConfigureAwait(false);
            };
        });

        return services;
    }

    /// <summary>
    /// Ключ розділу: хто саме «один клієнт».
    /// </summary>
    /// <remarks>
    /// ⛔ Типово — адреса СОКЕТА, і <c>X-Forwarded-For</c> ігнорується. Заголовок
    /// пише клієнт, а не мережа: якби ключ брався з нього, нападник міняв би
    /// його щозапиту і межа не спрацювала б ЖОДНОГО разу — «захист», який
    /// нічого не тримає, гірший за його відсутність, бо про нього звітують.
    ///
    /// ⚠ Зворотний бік названий прямо: якщо ECR колись стане за зворотним
    /// проксі, адреса сокета стане адресою проксі — одна на всіх, і перший же
    /// користувач вичерпає межу для решти, тобто «захист» перетвориться на
    /// відмову в обслуговуванні своїм же. Саме тому ручка
    /// <c>Security:RateLimit:TrustForwardedFor</c> існує і типово ВИМКНЕНА:
    /// вмикати її можна лише там, де проксі ГАРАНТОВАНО переписує заголовок, а
    /// не дописує до клієнтського. Сьогодні інсталятор піднімає Kestrel
    /// напряму (<c>R-01</c>), тож типовим станом є «проксі немає».
    ///
    /// ⚠ IPv4, відображений в IPv6 (<c>::ffff:10.0.0.1</c>), зводиться до
    /// IPv4: інакше той самий клієнт мав би два розділи залежно від того, як
    /// саме стек прийняв з'єднання, тобто подвоєну межу.
    /// </remarks>
    private static string ClientKey(HttpContext context, bool trustForwardedFor)
    {
        if (trustForwardedFor)
        {
            var forwarded = context.Request.Headers[ForwardedForHeader].ToString();

            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                // Найлівіший запис — початковий клієнт; решта — ланцюг проксі.
                var first = forwarded.Split(',')[0].Trim();

                if (first.Length > 0)
                {
                    return first;
                }
            }
        }

        var address = context.Connection.RemoteIpAddress;

        if (address is null)
        {
            return UnknownClient;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    /// <summary>
    /// Пише <c>problem+json</c> тієї самої форми, що й решта відмов.
    /// </summary>
    /// <remarks>
    /// ⛔ Порожнє тіло на 429 означало б, що клієнт не може ні показати
    /// причину, ні звірити випадок із логом: <c>correlationId</c> — єдине, за
    /// чим це робиться. Тому тут будується той самий
    /// <see cref="EcrProblemDetails"/> і тими самими налаштуваннями
    /// серіалізації, а не «щось схоже» руками.
    ///
    /// ⚠ Чому НЕ через <c>ExceptionHandlingMiddleware</c> (кидком винятку):
    /// його <c>Map</c> не має арма на 429, а цифри коду в цьому проєкті
    /// означають HTTP-статус — кинутий <c>ECR-AUTH-0423</c> доїхав би як 423
    /// «обліковку заблоковано», тобто відповів би неправду про інший стан.
    /// </remarks>
    private static async Task WriteProblemAsync(HttpContext context, CancellationToken ct)
    {
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw)
            ? raw as string ?? string.Empty
            : string.Empty;

        var problem = new EcrProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = await ResolveAsync(
                context, UiStringResolver.ErrorKeyPrefix + RejectionCode, RejectionCode).ConfigureAwait(false),
            Detail = RejectionDetail,
            Type = $"https://ecr.ncoc.kz/errors/{RejectionCode}",
            Instance = context.Request.Path,
            ErrorCode = RejectionCode,
            CorrelationId = correlationId,
        };

        problem.Extensions["errorCode"] = RejectionCode;
        problem.Extensions["correlationId"] = correlationId;

        context.Response.ContentType = ProblemJson;

        await JsonSerializer
            .SerializeAsync(context.Response.Body, problem, SerializerOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Текст каталогу за ключем; будь-який збій — запасне значення.</summary>
    /// <remarks>
    /// ⛔ Ковтається БУДЬ-ЯКА помилка каталогу. Це шлях СКИДАННЯ навантаження:
    /// якщо база під тиском не відповідає, похід за перекладом кине вдруге — і
    /// клієнт замість <c>problem+json</c> отримав би обірване з'єднання рівно
    /// тоді, коли система й так у найгіршому стані. Той самий аргумент, що в
    /// <c>ExceptionHandlingMiddleware.LocalizedTitleAsync</c>.
    /// </remarks>
    private static async Task<string> ResolveAsync(HttpContext context, string key, string fallback)
    {
        try
        {
            var catalog = context.RequestServices.GetService<IUiStringCatalog>();
            var currentUser = context.RequestServices.GetService<ICurrentUser>();

            if (catalog is null || currentUser is null)
            {
                return fallback;
            }

            var strings = await catalog
                .GetAsync(currentUser.Language, context.RequestAborted)
                .ConfigureAwait(false);

            var text = UiStringResolver.Resolve(strings, key);

            // Ключа немає ніде — резолвер повертає сам ключ; показувати його
            // користувачеві гірше, ніж запасне речення.
            return string.Equals(text, key, StringComparison.Ordinal) ? fallback : text;
        }
#pragma warning disable CA1031 // Причина — у ⛔ вище: відмова каталогу не має права зірвати відповідь про відмову.
        catch (Exception)
#pragma warning restore CA1031
        {
            return fallback;
        }
    }
}
