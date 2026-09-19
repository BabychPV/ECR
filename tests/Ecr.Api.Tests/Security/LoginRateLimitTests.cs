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

    /// <summary>Межа з <c>appsettings.json</c>; тест бере її з коду, а не вгадує.</summary>
    private const int Permit = LoginRateLimiting.DefaultLoginPermitPerMinute;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Одинадцята_спроба_входу_за_хвилину_з_одного_IP_дає_429_з_problem_json()
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

        Assert.Equal(LoginRateLimiting.RejectionCode, json.GetProperty("errorCode").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());

        // ⚠ Заголовок — із каталогу рядків, тим самим механізмом, що й решта
        // відмов (`ФВ-14.9a`): без цього користувач побачив би першим рядком
        // плашки сам код. Подробиця поки власного ключа каталогу не має — див.
        // `RejectionDetail`; тут перевіряється, що вона принаймні є.
        Assert.NotEqual(LoginRateLimiting.RejectionCode, json.GetProperty("title").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString()),
            $"порожня подробиця: {body}");
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
