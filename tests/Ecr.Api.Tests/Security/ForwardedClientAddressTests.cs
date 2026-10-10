// tests/Ecr.Api.Tests/Security/ForwardedClientAddressTests.cs

using System.Net;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S1-01 (AUDIT-2026-10-09b): за <c>TrustForwardedFor</c> клієнт — найправіший запис
/// <c>X-Forwarded-For</c>, що не належить довіреному проксі; ліві записи пише сам клієнт.
/// </summary>
/// <remarks>
/// ⚠ Без бази й без PBKDF2: ключ розділу і глобальний обмежувач перевіряються напряму,
/// тож результат не залежить від швидкості машини (на відміну від пачки входів у
/// <c>LoginRateLimitTests</c>).
/// </remarks>
public sealed class ForwardedClientAddressTests
{
    private const string Proxy = "10.0.0.5";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Лівий_XFF_не_відкриває_новий_розділ()
    {
        var resolver = new ForwardedClientAddress(trustForwardedFor: true);

        // ARR/nginx дописали справжнього клієнта (198.51.100.20) праворуч до підробленого.
        var keys = Enumerable.Range(1, 5)
            .Select(i => resolver.KeyOf(Request(Proxy, $"203.0.113.{i}, 198.51.100.20")))
            .Distinct()
            .ToList();

        // Мутація: повернути `Split(',')[0]` (найлівіший) — тут п'ять різних ключів.
        Assert.Equal("198.51.100.20", Assert.Single(keys));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Праве_значення_ділить_клієнтів()
    {
        var resolver = new ForwardedClientAddress(trustForwardedFor: true);

        Assert.Equal("10.0.0.5", resolver.KeyOf(Request("10.1.1.1", "1.1.1.1, 10.0.0.5")));
        Assert.Equal("10.0.0.6", resolver.KeyOf(Request("10.1.1.1", "1.1.1.1, 10.0.0.6")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Записи_довірених_проксі_пропускаються_справа_наліво()
    {
        var resolver = new ForwardedClientAddress(
            trustForwardedFor: true, [IPAddress.Parse(Proxy), IPAddress.Parse("10.0.0.9")]);

        // Клієнт → 10.0.0.9 (внутрішній балансувальник) → 10.0.0.5 (ARR) → Kestrel.
        Assert.Equal("198.51.100.20", resolver.KeyOf(Request(Proxy, "6.6.6.6, 198.51.100.20, 10.0.0.9")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Запит_повз_довірений_проксі_рахується_за_сокетом()
    {
        var resolver = new ForwardedClientAddress(trustForwardedFor: true, [IPAddress.Parse(Proxy)]);

        // Напряму на порт Kestrel: заголовок пише сам нападник.
        Assert.Equal("192.0.2.77", resolver.KeyOf(Request("192.0.2.77", "203.0.113.1")));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("198.51.100.20:51234", "198.51.100.20")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::ffff:198.51.100.20", "198.51.100.20")]
    public void Адреса_з_портом_і_IPv6_розбираються(string forwarded, string expected)
    {
        var resolver = new ForwardedClientAddress(trustForwardedFor: true);

        Assert.Equal(expected, resolver.KeyOf(Request(Proxy, "6.6.6.6, " + forwarded)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("unknown")]
    [InlineData("198.51.100.20, не-адреса")]
    [InlineData("")]
    [InlineData(" , ")]
    public void Сміття_праворуч_дає_адресу_сокета_а_не_довільний_рядок(string forwarded)
    {
        var resolver = new ForwardedClientAddress(trustForwardedFor: true);

        Assert.Equal(Proxy, resolver.KeyOf(Request(Proxy, forwarded)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_прапорця_заголовок_ігнорується()
    {
        var resolver = new ForwardedClientAddress(trustForwardedFor: false);

        Assert.Equal(Proxy, resolver.KeyOf(Request(Proxy, "198.51.100.20")));
        Assert.Equal(ForwardedClientAddress.UnknownClient, resolver.KeyOf(new DefaultHttpContext()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Обмежувач_входу_за_проксі_не_обходиться_лівим_XFF()
    {
        var configuration = Config(
            ("Security:RateLimit:TrustForwardedFor", "true"),
            ("Security:RateLimit:LoginPermitPerMinute", "3"));
        var services = new ServiceCollection();
        services.AddEcrRateLimiting(configuration);
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;

        for (var i = 1; i <= 3; i++)
        {
            using var allowed = limiter.AttemptAcquire(Login($"203.0.113.{i}, 198.51.100.20"));
            Assert.True(allowed.IsAcquired, $"спроба №{i}");
        }

        // Мутація: повернути найлівіший запис у ключ розділу — четверта спроба проходить.
        using var rejected = limiter.AttemptAcquire(Login("203.0.113.4, 198.51.100.20"));
        Assert.False(rejected.IsAcquired);

        // Інший справжній клієнт за тим самим проксі — свій розділ.
        using var neighbour = limiter.AttemptAcquire(Login("203.0.113.4, 198.51.100.21"));
        Assert.True(neighbour.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Недійсна_адреса_проксі_зупиняє_старт_з_ім_ям_ключа()
    {
        var problems = EcrConfigurationValidation.Validate(
            Config(("Security:RateLimit:KnownProxies", "10.0.0.5; proxy.local")));

        var problem = Assert.Single(problems);
        Assert.Contains("Security:RateLimit:KnownProxies", problem, StringComparison.Ordinal);
        Assert.Contains("proxy.local", problem, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Validate(
            Config(("Security:RateLimit:KnownProxies", "10.0.0.5, 10.0.0.6"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Прапорець_без_переліку_проксі_дає_попередження()
    {
        var warning = Assert.Single(EcrConfigurationValidation.Warnings(
            Config(("Security:RateLimit:TrustForwardedFor", "true"))));
        Assert.Contains("Security:RateLimit:KnownProxies", warning, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Warnings(Config(
            ("Security:RateLimit:TrustForwardedFor", "true"),
            ("Security:RateLimit:KnownProxies", Proxy))));
    }

    private static DefaultHttpContext Request(string socket, string forwarded)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(socket);
        context.Request.Headers[ForwardedClientAddress.ForwardedForHeader] = forwarded;
        return context;
    }

    private static DefaultHttpContext Login(string forwarded)
    {
        var context = Request(Proxy, forwarded);
        context.Request.Path = "/api/v1/login/local";
        return context;
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
