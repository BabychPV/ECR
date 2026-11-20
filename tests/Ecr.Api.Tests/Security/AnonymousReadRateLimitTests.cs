using System.Net;
using System.Net.Http.Json;
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
/// PSS-29: анонімні читання, що ходять у базу (<c>GET /api/v1/public/bootstrap</c>, <c>GET /api/v1/ui-strings/{lang}</c>),
/// мають межу 120/хв на адресу; автентифікованих вона не стосується.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати гілку <c>IsAnonymousRead</c> з глобального обмежувача — червоніє тест 121-го запиту;
/// прибрати умову <c>IsAuthenticated != true</c> — червоніє тест автентифікованого користувача (відмова в обслуговуванні
/// власним користувачам за NAT); зробити ключ розділу сталим — червоніє тест іншої адреси.
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

    [Theory]
    [InlineData("/api/v1/public/bootstrap")]
    [InlineData("/api/v1/ui-strings/en?scope=public")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-29")]
    public async Task Сто_двадцять_перший_анонімний_запит_за_хвилину_отримує_429_з_Retry_After(string path)
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
        Assert.All(allowed, r => Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}; {app.ErrorsText}"));
        Assert.True(rejected[0].Headers.RetryAfter is not null, "429 без Retry-After");

        // Інша адреса — власний розділ: законний користувач не страждає від чужого перебору.
        using var honest = Client(host, "198.51.100.52");
        var other = await honest.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(true);
        Assert.True(other.IsSuccessStatusCode, $"{other.StatusCode}");

        foreach (var response in responses)
        {
            response.Dispose();
        }
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

        // Корпоративний NAT: решта майданчика теж ходить з цієї адреси, але з cookie.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, Permit + 20).Select(_ =>
                client.GetAsync(new Uri("/api/v1/ui-strings/en", UriKind.Relative)))).ConfigureAwait(true);

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);

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
