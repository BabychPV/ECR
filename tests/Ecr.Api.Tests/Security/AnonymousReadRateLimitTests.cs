using System.Net;
using System.Net.Http.Json;
using Ecr.Api.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PSS-29: анонімні читання під <c>/api/v1/public</c> і <c>/api/v1/ui-strings</c> мають межу 120/хв на адресу —
/// КРІМ канонічних читань екрана входу, які обслуговуються з пам'яті; автентифікованих межа не стосується.
/// </summary>
/// <remarks>
/// ⛔ e2e RC #988: межа рахувала й канонічні читання (стан + каталог + зріз кожної мови — 3–5 запитів на відкриття
/// екрана входу), і всі, хто ділить адресу (e2e-воркери, офіс за NAT, проксі без довіреного <c>X-Forwarded-For</c>),
/// після ~25 відкриттів за хвилину бачили «Interface texts could not be loaded — check the network».
///
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: повернути межу на канонічні читання (прибрати <c>!IsMemoryServedRead</c>) — червоніє
/// <see cref="Канонічні_читання_екрана_входу_понад_межу_з_однієї_адреси_не_відхиляються"/>; прибрати гілку
/// <c>IsAnonymousRead</c> з глобального обмежувача — червоніє тест 121-го НЕканонічного запиту; прибрати умову
/// <c>IsAuthenticated != true</c> — червоніє тест автентифікованого користувача; зробити ключ розділу сталим —
/// червоніє перевірка іншої адреси.
/// </remarks>
[Collection("SqlServer")]
public sealed class AnonymousReadRateLimitTests(SqlServerFixture sql)
{
    private const int Permit = LoginRateLimiting.DefaultAnonymousReadPermitPerMinute;
    private const string Password = "Anon-Read-Limit-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public void Межа_в_конфігурації_збігається_з_дефолтом_у_коді()
    {
        using var app = new EcrApiFactory(sql);
        using var host = app.WithWebHostBuilder(_ => { });
        using var _unused = host.CreateClient();

        Assert.Equal(
            Permit,
            host.Services.GetRequiredService<IConfiguration>().GetValue<int>(LoginRateLimiting.AnonymousReadPermitKey));
    }

    /// <summary>
    /// Офіс за одним NAT о 9:00: утричі більше канонічних читань за межу — жодної відмови.
    /// </summary>
    /// <remarks>
    /// ⚠ Невідома мова (<c>zz</c>) теж канонічна: її обслуговує запис мови за замовчуванням (L1-02), а не новий
    /// ключ кешу — тобто перебір мов базу не множить і межі не потребує.
    /// </remarks>
    /// <param name="path">Канонічне читання.</param>
    [Theory]
    [InlineData("/api/v1/public/bootstrap")]
    [InlineData("/api/v1/ui-strings/en?scope=public")]
    [InlineData("/api/v1/ui-strings/en")]
    [InlineData("/api/v1/ui-strings/zz?scope=PUBLIC")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public async Task Канонічні_читання_екрана_входу_понад_межу_з_однієї_адреси_не_відхиляються(string path)
    {
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.50");

        var responses = await Task.WhenAll(
            Enumerable.Range(0, (3 * Permit) + 1).Select(_ => client.GetAsync(new Uri(path, UriKind.Relative))))
            .ConfigureAwait(true);

        try
        {
            Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}; {app.ErrorsText}"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>Неканонічне анонімне читання (законний клієнт його не шле) — під межею, з <c>Retry-After</c>.</summary>
    /// <remarks>
    /// ⚠ Приклад — приватна область каталогу без входу: дія анонімна (<c>[AllowAnonymous]</c>), тож запит доходить
    /// до обмежувача. Закриті <c>[Authorize]</c> маршрути (<c>coverage</c>, <c>export.csv</c>) відсікає
    /// <c>UseAuthorization</c> ще ДО обмежувача — дешевим 401 без бази.
    /// </remarks>
    /// <param name="path">Анонімне читання, закрите входом.</param>
    [Theory]
    [InlineData("/api/v1/ui-strings/en?scope=private")]
    [InlineData("/api/v1/ui-strings/ru?scope=Private&lang=x")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public async Task Сто_двадцять_перший_неканонічний_анонімний_запит_за_хвилину_отримує_429_з_Retry_After(string path)
    {
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.51");

        // Одночасною пачкою (як у LoginRateLimitTests): дозвіл береться на вході в конвеєр, і вікно не встигає змінитися.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, Permit + 1).Select(_ => client.GetAsync(new Uri(path, UriKind.Relative))))
            .ConfigureAwait(true);

        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        var allowed = responses.Except(rejected).ToList();

        Assert.Single(rejected);
        Assert.Equal(Permit, allowed.Count);

        // Пропущені доходять до звичайної відмови «потрібен вхід», а не до 5xx.
        Assert.All(
            allowed,
            r => Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{r.StatusCode}; {app.ErrorsText}"));
        Assert.True(rejected[0].Headers.RetryAfter is not null, "429 без Retry-After");

        // Інша адреса — власний розділ: законний користувач не страждає від чужого перебору.
        using var honest = Client(host, "198.51.100.52");
        var other = await honest.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(true);
        Assert.True(other.StatusCode == HttpStatusCode.Unauthorized, $"{other.StatusCode}");

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>Межа між «з пам'яті» і «під межею» — форма запиту.</summary>
    /// <param name="path">Шлях.</param>
    /// <param name="query">Рядок запиту.</param>
    /// <param name="memoryServed">Чи читання канонічне.</param>
    [Theory]
    [InlineData("/api/v1/public/bootstrap", "", true)]
    [InlineData("/API/V1/PUBLIC/BOOTSTRAP", "?x=1", true)]
    [InlineData("/api/v1/ui-strings/en", "", true)]
    [InlineData("/api/v1/ui-strings/kz", "?scope=public", true)]
    [InlineData("/api/v1/ui-strings/en", "?scope=Public&v=3", true)]
    [InlineData("/api/v1/ui-strings/en", "?scope=private", false)]
    [InlineData("/api/v1/ui-strings/en", "?scope=public&scope=private", false)]
    [InlineData("/api/v1/ui-strings", "", false)]
    [InlineData("/api/v1/ui-strings/", "", false)]
    [InlineData("/api/v1/ui-strings/en/", "", false)]
    [InlineData("/api/v1/ui-strings/en/login.title", "", false)]
    [InlineData("/api/v1/ui-strings/coverage", "", false)]
    [InlineData("/api/v1/ui-strings/export.csv", "?lang=ru", false)]
    [InlineData("/api/v1/public/other", "", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "PSS-29")]
    public void Канонічність_визначається_формою_запиту(string path, string query, bool memoryServed)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.QueryString = query.Length == 0 ? QueryString.Empty : new QueryString(query);

        Assert.Equal(memoryServed, LoginRateLimiting.IsMemoryServedRead(context));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public async Task Автентифікований_користувач_межі_анонімних_читань_не_витрачає()
    {
        var name = $"anon_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(
                         new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.53");

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}; {app.ErrorsText}");

        // Корпоративний NAT: решта майданчика теж ходить з цієї адреси, але з cookie. ⚠ Приватна область — саме
        // та форма, що анонімно стоїть під межею: канонічна публічна межі не має взагалі й умову не перевіряла б.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, Permit + 20).Select(_ =>
                client.GetAsync(new Uri("/api/v1/ui-strings/en?scope=private", UriKind.Relative)))).ConfigureAwait(true);

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}; {app.ErrorsText}"));

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static WebApplicationFactory<Program> WithRemoteIp(EcrApiFactory app)
        => app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>()));

    private static HttpClient Client(WebApplicationFactory<Program> host, string remoteIp)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        return client;
    }
}
