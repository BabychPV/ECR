// tests/Ecr.Api.Tests/Security/LoginRateLimitTests.cs

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Api.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Обмеження частоти входу (<c>S-10</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>POST /api/v1/login/local</c> анонімний і коштує 210 000
/// ітерацій PBKDF2 навіть для неіснуючого імені (<c>Decoy</c>-хеш — щоб
/// відповідь не розрізняла «немає такого» і «не той пароль»). Блокування
/// облікового запису захищає ОБЛІКОВКУ, а не сервер: під іменами, яких немає,
/// блокувати нічого.
///
/// ⚠ Перевірка йде в ДВА боки, і другий не менш важливий за перший. Межа, що
/// ріже всіх разом, — це не захист, а відмова в обслуговуванні своїм же
/// користувачам, і виглядає вона в журналі точно так само.
/// </remarks>
[Collection("SqlServer")]
public sealed class LoginRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Kashagan-2026-Winter!";
    private const string ProblemJson = "application/problem+json";
    private const string ForwardedFor = "X-Forwarded-For";

    /// <summary>Межа спроб за хвилину; тест бере її з коду, а не вгадує.</summary>
    /// <remarks>
    /// ⚠ Це ЗАПАСНЕ значення обмежувача, а не обов'язково діюче: діє те, що в
    /// <c>appsettings.json</c> під <c>Security:RateLimit:LoginPermitPerMinute</c>.
    /// Те, що ці два числа збігаються, — окреме твердження, і його доводить
    /// <see cref="Межа_в_конфігурації_збігається_з_дефолтом_у_коді"/>. Без
    /// нього цикли «по <c>Permit</c>» нижче міряли б не ту межу, за якою живе
    /// продукт.
    /// </remarks>
    private const int Permit = LoginRateLimiting.DefaultLoginPermitPerMinute;

    /// <summary>
    /// Скільки разів логіниться набір <c>src/Ecr.Web/e2e</c> за один прогін.
    /// </summary>
    /// <remarks>
    /// ⛔ Це не кругле число «про запас», а ЗАМІР: 23 виклики
    /// <c>signIn(page)</c>, один з яких у <c>beforeEach</c>
    /// (<c>expressionEditor.spec.ts</c>). Саме він і впіймав дефект: на межі
    /// 10/хв прогін падав на одинадцятому вході з «The account is locked.»,
    /// хоча жодної обліковки ніхто не блокував. Набір при цьому не атакує — він
    /// стискає в часі звичайну поведінку, ту саму, що прохідна на початку зміни
    /// за корпоративним NAT.
    ///
    /// ⚠ Число тримається окремо від <see cref="Permit"/> НАВМИСНО: тест має
    /// падати, коли межу опустять нижче за реальну потребу, а не мовчки
    /// підлаштовуватися під неї.
    /// </remarks>
    private const int E2eSignInsPerRun = 23;

    /// <summary>Код відмови — ЛІТЕРАЛОМ, а не через константу продукту.</summary>
    /// <remarks>
    /// ⛔ <c>LoginRateLimiting.RejectionCode</c> тут не годиться: тест, який
    /// бере очікуване значення з того самого рядка, що й перевіряє, лишиться
    /// зеленим після будь-якої його підміни — зокрема після повернення до
    /// позиченого <c>ECR-AUTH-0423</c>, через яке вся ця робота й почалася.
    /// </remarks>
    private const string ExpectedCode = "ECR-AUTH-0429";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Спроба_понад_межу_за_хвилину_з_одного_IP_дає_429_і_НЕ_каже_про_блокування_обліковки()
    {
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "203.0.113.10");

        for (var attempt = 1; attempt <= Permit; attempt++)
        {
            var allowed = await LoginAsync(client, "немає-такого-користувача").ConfigureAwait(true);

            // ⚠ Спершу — що межа не спрацювала РАНІШЕ. Обмежувач, який ріже з
            // першого запиту, теж дав би 429 на одинадцятому.
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var rejected = await LoginAsync(client, "немає-такого-користувача").ConfigureAwait(true);

        // ⛔ Мутаційний доказ: прибрати `app.UseRateLimiter()` із `Program.cs`
        // (або підняти `PermitLimit`) — падає цей рядок.
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // ⛔ І це половина вимоги: 429 з ПОРОЖНІМ тілом клієнт не відрізнить від
        // збою мережі, а звірити випадок із журналом не зможе взагалі.
        Assert.Equal(ProblemJson, rejected.Content.Headers.ContentType?.MediaType);

        var body = await rejected.Content.ReadAsStringAsync().ConfigureAwait(true);
        var json = JsonDocument.Parse(body).RootElement;

        // ⛔ Мутаційний доказ: повернути `RejectionCode` на
        // `ErrorCodes.AccountLocked` — падає цей рядок. Клієнт маршрутизує
        // відмову за кодом, і саме код відрізняє «адреса вичерпала хвилинну
        // межу» від «обліковку зачинено політикою невдалих спроб».
        Assert.Equal(ExpectedCode, json.GetProperty("errorCode").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());

        // ⚠ Заголовок — із каталогу рядків, тим самим механізмом, що й решта
        // відмов (`ФВ-14.9a`): без цього користувач побачив би першим рядком
        // плашки сам код.
        var title = json.GetProperty("title").GetString();
        Assert.NotEqual(ExpectedCode, title);

        // ⛔ ГОЛОВНЕ твердження цього тесту, і воно про ПРАВДУ, а не про текст.
        // З позиченим `ECR-AUTH-0423` заголовок приїздив із каталогу як
        // «The account is locked.» — при тому що обмежувач ріже за АДРЕСОЮ і
        // про обліковки не знає нічого: імені, під яким стукали, може не
        // існувати взагалі. Ціна брехні тут не косметична — дзвінок у
        // підтримку через блокування, якого немає, або адміністратор, що
        // «розблоковує» незаблоковане.
        Assert.DoesNotContain("lock", title, StringComparison.OrdinalIgnoreCase);

        var detail = json.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail), $"порожня подробиця: {body}");
        Assert.DoesNotContain("lock", detail, StringComparison.OrdinalIgnoreCase);

        // ⛔ Подробиця приїхала з КАТАЛОГУ, а не із запасного речення в коді:
        // згадка `Retry-After` є лише в рядку сіду
        // (`err.ECR-AUTH-0429.tooManyAttempts`). Без цього рядка резолвер
        // мовчки віддав би запасне речення — тобто ключ виглядав би
        // локалізованим, не будучи ним, і жодною мовою, крім `en`, не
        // перекладався б ніколи.
        Assert.Contains("Retry-After", detail, StringComparison.Ordinal);

        Assert.False(
            string.IsNullOrWhiteSpace(json.GetProperty("correlationId").GetString()),
            $"у тілі немає correlationId: {body}");

        // ⚠ Без `Retry-After` клієнт може лише вгадувати, коли повторити, і
        // типово вгадує «зараз» — тобто продовжує те саме навантаження.
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Користувач_з_іншого_IP_заходить_поки_сусідній_вичерпав_межу()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);

        using var attacker = Client(host, "203.0.113.20");
        using var honest = Client(host, "198.51.100.7");

        // ⚠ Вичерпує межу під ІМЕНЕМ, ЯКОГО НЕМАЄ: інакше тест заблокував би
        // обліковку (`ФВ-6.4a`) і перевіряв би вже не обмежувач частоти.
        for (var attempt = 0; attempt <= Permit; attempt++)
        {
            await LoginAsync(attacker, "немає-такого-користувача").ConfigureAwait(true);
        }

        var blocked = await LoginAsync(attacker, "немає-такого-користувача").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        var login = await honest.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(true);

        // ⛔ Мутаційний доказ: зробити ключ розділу сталим (наприклад, повертати
        // з `ClientKey` один рядок на всіх) — падає цей рядок, і саме він
        // відрізняє захист від відмови в обслуговуванні власним користувачам.
        Assert.True(
            login.IsSuccessStatusCode,
            $"законний вхід з іншої адреси: {login.StatusCode}; {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Межа_в_конфігурації_збігається_з_дефолтом_у_коді()
    {
        // ⛔ ЗНАЙДЕНО МУТАЦІЄЮ, і без цього тесту решта набору брехала б.
        // `AddEcrRateLimiting` читає `configuration.GetValue(PermitKey,
        // DefaultLoginPermitPerMinute)` — тобто константа є лише ЗАПАСНИМ
        // значенням, а діє те, що написано в `appsettings.json`. Доки обидва
        // числа були 10, різниці не було видно; щойно я повернув константу на
        // 10, лишивши конфігурацію на 60, тест `Зміна_за_одним_NAT…` лишився
        // ЗЕЛЕНИМ — бо застосунок константи не питав. Усі тести нижче, що
        // ходять у цикл по `Permit`, від цієї розбіжності стали б
        // безпредметними: вони міряли б одне число, а продукт жив би за іншим.
        //
        // ⚠ Перевіряється ЕФЕКТИВНЕ значення з піднятого застосунку, а не
        // текст `appsettings.json`: саме його бачить обмежувач, і саме воно
        // поїде в прод.
        using var app = new EcrApiFactory(sql);
        using var host = app.WithWebHostBuilder(_ => { });

        // Хост створюється лінькувато — без клієнта конфігурації ще немає.
        using var _unused = host.CreateClient();

        var effective = host.Services
            .GetRequiredService<IConfiguration>()
            .GetValue<int>("Security:RateLimit:LoginPermitPerMinute");

        Assert.Equal(Permit, effective);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_за_одним_NAT_проходить_межу_не_вичерпавши_її()
    {
        // ⛔ Без цього тесту робота полагодила б лише НАПИС. Позичений код
        // брехав, але сама межа теж була дефектом: 10/хв на адресу — це
        // відмова в обслуговуванні за корпоративним NAT, де ВСІ користувачі
        // майданчика приходять з однієї зовнішньої адреси. Прохідна на
        // початку зміни (30 операторів за кілька хвилин) і набір e2e
        // (`E2eSignInsPerRun` входів поспіль) — та сама поведінка, і жодна з
        // них не є атакою.
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);

        // ⚠ Входи УСПІШНІ, а не відбиті: обмежувач рахує запити незалежно від
        // результату, і саме законний трафік він і різав.
        //
        // ⚠ Свій клієнт на кожен вхід — це не гігієна тесту, а сам сценарій:
        // за NAT адреса одна, а браузери РІЗНІ, кожен зі своєю cookie. Спільний
        // клієнт носив би cookie першого входу й перевіряв би вже не те.
        for (var attempt = 1; attempt <= E2eSignInsPerRun; attempt++)
        {
            using var client = Client(host, "198.51.100.42");

            var login = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = name, password = Password }).ConfigureAwait(true);

            // ⛔ Мутаційний доказ: повернути `DefaultLoginPermitPerMinute` на
            // 10 — падає цей рядок на одинадцятій ітерації, рівно там, де
            // падав прогін `tools/e2e-stand.ps1`.
            Assert.True(
                login.StatusCode != HttpStatusCode.TooManyRequests,
                $"вхід №{attempt} з {E2eSignInsPerRun} відбито межею "
                + $"({Permit}/хв): за NAT це відмова в обслуговуванні власним користувачам");

            Assert.True(
                login.IsSuccessStatusCode,
                $"вхід №{attempt}: {login.StatusCode}; {app.ErrorsText}");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Підроблений_X_Forwarded_For_не_обходить_межу()
    {
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "203.0.113.30");

        // Нападник міняє заголовок щозапиту — найдешевший спосіб обійти межу,
        // якщо ключ розділу брати з нього.
        for (var attempt = 1; attempt <= Permit; attempt++)
        {
            using var request = Request("немає-такого-користувача");
            request.Headers.Add(
                ForwardedFor,
                string.Create(CultureInfo.InvariantCulture, $"10.0.0.{attempt}"));

            using var allowed = await client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var last = Request("немає-такого-користувача");
        last.Headers.Add(ForwardedFor, "10.0.0.250");

        using var rejected = await client.SendAsync(last).ConfigureAwait(true);

        // ⛔ Мутаційний доказ: змінити дефолт `Security:RateLimit:TrustForwardedFor`
        // на `true` — падає цей рядок, бо кожен запит потрапляє у власний
        // розділ і межа не спрацьовує ЖОДНОГО разу.
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    /// <summary>Застосунок, у якому адресу сокета задає заголовок тесту.</summary>
    private static WebApplicationFactory<Program> WithRemoteIp(EcrApiFactory app)
        => app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>()));

    /// <summary>Клієнт із фіксованою адресою «сокета».</summary>
    private static HttpClient Client(WebApplicationFactory<Program> host, string remoteIp)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        return client;
    }

    /// <summary>Запит на вхід із завідомо невірними обліковими даними.</summary>
    private static HttpRequestMessage Request(string userName)
        => new(HttpMethod.Post, new Uri("/api/v1/login/local", UriKind.Relative))
        {
            Content = JsonContent.Create(new { userName, password = "не-той-пароль" }),
        };

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName)
        => await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = "не-той-пароль" }).ConfigureAwait(false);

    private async Task<string> ArrangeLocalUserAsync()
    {
        var name = $"rate_{Guid.NewGuid():N}"[..20];

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));

        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }
}
