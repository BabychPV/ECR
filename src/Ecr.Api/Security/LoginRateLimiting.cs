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
using Microsoft.AspNetCore.RateLimiting;

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
/// ⚠ Обмежувач входу — глобальний (<c>GlobalLimiter</c>) за префіксом шляху:
/// межа лишається в одному файлі поруч із поясненням. Middleware стоїть ПІСЛЯ
/// автентифікації (цього вимагає межа пошуку за користувачем,
/// <see cref="SearchRateLimitPolicy"/>); для анонімного входу без cookie це
/// лише кілька перевірок у пам'яті, тож PBKDF2 однаково не починається.
///
/// ⚠ Вікно фіксоване (одна хвилина) і черги немає (<c>QueueLimit = 0</c>):
/// черга під атакою — це пам'ять, яку нападник наповнює безкоштовно.
/// </remarks>
public static class LoginRateLimiting
{
    /// <summary>Скільки спроб входу з однієї адреси дозволено за хвилину.</summary>
    /// <remarks>
    /// ⛔ Було 10, і це ламало ЗАКОННЕ використання. За корпоративним NAT усі
    /// користувачі майданчика приходять з однієї зовнішньої адреси: зміна на
    /// заводі — 30 операторів заходять протягом кількох хвилин, — і з
    /// одинадцятого починаються відмови. Знайшов це не аудит, а набір
    /// <c>tools/e2e-stand.ps1</c>: він логіниться 23 рази і падав на
    /// одинадцятому. Набір не атакує — він стискає в часі звичайну поведінку,
    /// і рівно це робить прохідна на початку зміни.
    ///
    /// ⚠ Межа НЕ захищає обліковий запис — це робить окремий і незайманий
    /// механізм блокування (<c>ФВ-6.4a</c>). Її єдина мета — не дати
    /// АНОНІМНОМУ джерелу вичерпати процесор: кожна спроба коштує 210 000
    /// ітерацій PBKDF2-HMAC-SHA256 навіть під іменем, якого не існує.
    /// Тому число обирається не «на око, щоб пролізло», а з ціни хвилини:
    ///
    /// <list type="bullet">
    /// <item><b>Замір</b> (<c>Rfc2898DeriveBytes.Pbkdf2</c>, 210 000,
    /// SHA-256, 32 байти; .NET 10, 12 ядер, машина під навантаженням):
    /// <b>96.6 мс</b> на один хеш, тобто ≈0.1 с одного ядра на спробу.</item>
    /// <item><b>Було, 10/хв</b> — 0.97 с CPU/хв = <b>1.6 %</b> одного ядра з
    /// адреси. Дешево, але ціною відмови в обслуговуванні своїм же.</item>
    /// <item><b>Стало, 60/хв</b> — 5.8 с CPU/хв = <b>9.7 %</b> одного ядра з
    /// адреси. Десять різних адрес на стелі = 58 с CPU/хв ≈ одне ядро з
    /// дванадцяти: межа лишається межею.</item>
    /// <item><b>Чому не 120</b> — 19.3 % ядра з адреси, тобто п'ять адрес
    /// з'їдають ціле ядро, і стеля перестає бути стелею.</item>
    /// <item><b>Чому 60, а не рівно під потребу (30–40)</b> — щоб не
    /// повертатися до цього числа втретє: прохідна дає 30 входів, плюс
    /// помилки набору пароля, плюс повторні входи після виходу. 60 покриває
    /// це з запасом і коштує менше десятої частини ядра.</item>
    /// </list>
    ///
    /// ⚠ Межа за адресою в принципі не спиняє нападника з багатьма адресами —
    /// вона обмежує, скільки може з'їсти ОДНЕ джерело. Підняття з 10 до 60
    /// множить стелю одного джерела на шість, а не знімає її.
    ///
    /// ⚠ Значення перевизначається ключем
    /// <c>Security:RateLimit:LoginPermitPerMinute</c> (<c>appsettings.json</c>);
    /// тут — дефолт, який їде в продукт. Піднімати межу лише в конфігурації
    /// стенда було б гірше за дефект: стенд перестав би ганяти ту
    /// конфігурацію, що в проді.
    /// </remarks>
    public const int DefaultLoginPermitPerMinute = 60;

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
    /// ⛔ Було <c>ECR-AUTH-0423</c> («обліковку заблоковано») — бо родини
    /// <c>ECR-AUTH-0429</c> тоді не існувало, а завести її означало зачепити
    /// чотири файли й два сторожі (<c>ContractIntegrityTests</c> звіряє коди з
    /// таблицею §7 <c>02-contracts.md</c> в обидва боки,
    /// <c>ErrorTitleCatalogTests</c> — заголовок і <c>messageKey</c> із
    /// <c>09-seed.sql</c>). Позичений код БРЕХАВ: заголовок із каталогу казав
    /// «The account is locked.», хоча межу вичерпала адреса, а обліковку ніхто
    /// не блокував — і не міг, бо межа ріже до того, як стане відомо, чи існує
    /// назване ім'я. Ціна не косметична: дзвінок у підтримку через блокування,
    /// якого немає, і адміністратор, що «розблоковує» незаблоковане.
    ///
    /// ⚠ Тепер родина заведена як належить — див.
    /// <see cref="ErrorCodes.TooManyLoginAttempts"/>: 423 — про ОБЛІКОВКУ
    /// (минає розблокуванням), 429 — про АДРЕСУ (минає сама, строк — у
    /// <c>Retry-After</c>). Дія користувача різна, тому й коди різні.
    /// </remarks>
    public const string RejectionCode = ErrorCodes.TooManyLoginAttempts;

    /// <summary>Ключ каталогу для подробиці відмови.</summary>
    /// <remarks>
    /// ⚠ Записаний ПОВНИМ літералом, а не складений із
    /// <c>UiStringResolver.ErrorKeyPrefix</c> і <see cref="RejectionCode"/>.
    /// Причина не стильова: сторож
    /// <c>ErrorTitleCatalogTests.Кожен_messageKey_із_коду_заведено_в_сіді</c>
    /// шукає в джерелах саме рядковий літерал, що починається з префікса
    /// <c>err.</c>, — складений із частин ключ він не побачив би, і рядок,
    /// забутий у сіді, проліз би мовчки.
    ///
    /// ⚠ І назвати цей префікс у коментарі В ЛАПКАХ теж не можна: сторож
    /// дивиться на текст файлу, а не на дерево розбору, тож згадка в
    /// XML-документації читається ним як ще один кидок — і вимагає рядка сіду
    /// під ключ <c>err.…</c>, якого не існує. Перевірено: саме так він і
    /// почервонів на першому ж прогоні цієї правки.
    /// </remarks>
    private const string RejectionDetailKey = "err.ECR-AUTH-0429.tooManyAttempts";

    /// <summary>
    /// Запасна подробиця відмови — коли каталог недоступний.
    /// </summary>
    /// <remarks>
    /// ⚠ Англійською, а не українською, як більшість запасних речень у цьому
    /// коді: ті призначені підмінятися каталогом, а це доїде до користувача як
    /// є, якщо база під тиском не відповість. Мова збірки — <c>en</c>
    /// (<c>ФВ-14.9</c>), української серед мов продукту немає.
    ///
    /// ⛔ Не каже про блокування обліковки ЖОДНИМ словом — це та сама неправда,
    /// що й у позиченому коді, лише другим рядком плашки.
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

            options.OnRejected = static (rejection, ct) =>
                new ValueTask(RejectAsync(rejection, RejectionCode, RejectionDetailKey, RejectionDetail, ct));

            // Межа пошуку (BE-19) — іменована політика: їй потрібен користувач,
            // тобто вона діє лише після автентифікації (див. `Program.cs`).
            options.AddPolicy<string, SearchRateLimitPolicy>(SearchRateLimitPolicy.PolicyName);
        });

        return services;
    }

    /// <summary>
    /// Відповідь 429: <c>Retry-After</c> і <c>problem+json</c> із заданим кодом.
    /// </summary>
    /// <remarks>
    /// ⚠ Спільна для всіх політик: форма відмови одна, різняться лише код і подробиця.
    /// </remarks>
    internal static async Task RejectAsync(
        OnRejectedContext rejection, string code, string detailKey, string detailFallback, CancellationToken ct)
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

        await WriteProblemAsync(context, code, detailKey, detailFallback, ct).ConfigureAwait(false);
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
    /// ⚠ Чому НЕ через <c>ExceptionHandlingMiddleware</c> (кидком винятку).
    /// Причина вже НЕ в тому, що в його <c>Map</c> немає арма на 429 — арм
    /// заведено разом із кодом. Причина в тому, що це шлях СКИДАННЯ
    /// навантаження: обмежувач глобальний і спрацьовує до <c>UseRouting</c>,
    /// а кидок винятку провів би відхилений запит крізь увесь конвеєр — тобто
    /// коштував би рівно того, задля економії чого межа й існує. Арм у
    /// <c>Map</c> лишається страхувальною сіткою для синхронного шляху, який
    /// колись кине цей код сам.
    /// </remarks>
    private static async Task WriteProblemAsync(
        HttpContext context, string code, string detailKey, string detailFallback, CancellationToken ct)
    {
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw)
            ? raw as string ?? string.Empty
            : string.Empty;

        var problem = new EcrProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = await ResolveAsync(
                context, UiStringResolver.ErrorKeyPrefix + code, code).ConfigureAwait(false),
            Detail = await ResolveAsync(context, detailKey, detailFallback).ConfigureAwait(false),
            Type = $"https://ecr.ncoc.kz/errors/{code}",
            Instance = context.Request.Path,
            ErrorCode = code,
            CorrelationId = correlationId,
        };

        problem.Extensions["errorCode"] = code;
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
