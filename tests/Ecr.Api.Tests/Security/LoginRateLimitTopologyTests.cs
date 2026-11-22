// tests/Ecr.Api.Tests/Security/LoginRateLimitTopologyTests.cs

using System.Net;
using System.Threading.RateLimiting;
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
/// PERF-07 (AUDIT-PERF1000): вхід Windows має власну, вищу межу на адресу; межа <c>login/local</c> (60/хв) не змінилась;
/// за увімкненої довіри до <c>X-Forwarded-For</c> від відомих проксі межа ділиться за справжнім клієнтом.
/// </summary>
/// <remarks>
/// ⚠ Без бази й без PBKDF2: глобальний обмежувач перевіряється напряму (як у <c>ForwardedClientAddressTests</c>),
/// тож 600 запитів не залежать від швидкості машини.
/// </remarks>
public sealed class LoginRateLimitTopologyTests
{
    private const string Proxy = "10.0.0.5";
    private const string WindowsKey = "Security:RateLimit:WindowsLoginPermitPerMinute";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Шістсот_входів_Windows_з_однієї_адреси_проходять_а_шістсот_перший_ні()
    {
        var limiter = Limiter();

        for (var i = 1; i <= 600; i++)
        {
            using var lease = limiter.AttemptAcquire(Windows("198.51.100.42"));

            // Мутація: прибрати розділ Windows (шлях знову під спільним префіксом) — падає на 61-му.
            Assert.True(lease.IsAcquired, $"вхід Windows №{i} відбито межею");
        }

        using var rejected = limiter.AttemptAcquire(Windows("198.51.100.42"));
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Локальний_вхід_лишається_на_60_за_хвилину_і_не_ділить_вікно_з_Windows()
    {
        var limiter = Limiter();

        for (var i = 1; i <= 60; i++)
        {
            using var lease = limiter.AttemptAcquire(Local("198.51.100.42"));
            Assert.True(lease.IsAcquired, $"локальний вхід №{i}");
        }

        // Мутація: підняти дефолт локального входу — 61-ша проходить.
        using var rejected = limiter.AttemptAcquire(Local("198.51.100.42"));
        Assert.False(rejected.IsAcquired);

        // Вичерпаний локальний вхід не зачіпає Windows тієї самої адреси.
        using var windows = limiter.AttemptAcquire(Windows("198.51.100.42"));
        Assert.True(windows.IsAcquired);

        // І навпаки: шторм Windows не з'їдає локальне вікно тієї самої адреси.
        var other = Limiter();
        for (var i = 0; i < 100; i++)
        {
            other.AttemptAcquire(Windows("198.51.100.43")).Dispose();
        }

        using var local = other.AttemptAcquire(Local("198.51.100.43"));
        Assert.True(local.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Дефолти_Windows_600_і_локальний_60_у_коді_й_у_appsettings()
    {
        Assert.Equal(600, LoginRateLimiting.DefaultWindowsLoginPermitPerMinute);
        Assert.Equal(60, LoginRateLimiting.DefaultLoginPermitPerMinute);

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"немає {path}");

        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        Assert.Equal(600, configuration.GetValue<int>(WindowsKey));
        Assert.Equal(60, configuration.GetValue<int>("Security:RateLimit:LoginPermitPerMinute"));
        Assert.False(configuration.GetValue<bool>("Security:RateLimit:TrustForwardedFor"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Межа_Windows_налаштовується_ключем()
    {
        var limiter = Limiter((WindowsKey, "5"));

        for (var i = 1; i <= 5; i++)
        {
            using var lease = limiter.AttemptAcquire(Windows("198.51.100.42"));
            Assert.True(lease.IsAcquired);
        }

        using var rejected = limiter.AttemptAcquire(Windows("198.51.100.42"));
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Вхід_Windows_за_довіреним_проксі_ділиться_за_справжнім_клієнтом()
    {
        var limiter = Limiter(
            ("Security:RateLimit:TrustForwardedFor", "true"),
            ("Security:RateLimit:KnownProxies", Proxy),
            (WindowsKey, "3"));

        for (var i = 1; i <= 3; i++)
        {
            // Лівий запис підроблено — ключ береться з найправішого, що не є проксі.
            using var lease = limiter.AttemptAcquire(Windows(Proxy, $"203.0.113.{i}, 198.51.100.20"));
            Assert.True(lease.IsAcquired, $"спроба №{i}");
        }

        using var rejected = limiter.AttemptAcquire(Windows(Proxy, "203.0.113.9, 198.51.100.20"));
        Assert.False(rejected.IsAcquired);

        // Інший справжній клієнт за тим самим проксі — свій розділ, а не спільна адреса проксі.
        using var neighbour = limiter.AttemptAcquire(Windows(Proxy, "198.51.100.21"));
        Assert.True(neighbour.IsAcquired);

        // Запит повз проксі (сокет не з KnownProxies) рахується за сокетом, хоч би що було в заголовку.
        for (var i = 1; i <= 3; i++)
        {
            using var lease = limiter.AttemptAcquire(Windows("192.0.2.77", $"198.51.100.{100 + i}"));
            Assert.True(lease.IsAcquired);
        }

        using var direct = limiter.AttemptAcquire(Windows("192.0.2.77", "198.51.100.200"));
        Assert.False(direct.IsAcquired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void За_NAT_без_довіри_до_заголовка_усі_ділять_одну_адресу()
    {
        // Довіра вимкнена (типово): заголовок ігнорується, адреса — сокет проксі/NAT. Це і є випадок,
        // який виручає окрема межа Windows (див. runbook).
        var limiter = Limiter((WindowsKey, "3"));

        for (var i = 1; i <= 3; i++)
        {
            using var lease = limiter.AttemptAcquire(Windows(Proxy, $"198.51.100.{i}"));
            Assert.True(lease.IsAcquired);
        }

        using var rejected = limiter.AttemptAcquire(Windows(Proxy, "198.51.100.99"));
        Assert.False(rejected.IsAcquired);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("багато")]
    public void Недійсна_межа_Windows_зупиняє_старт_з_ім_ям_ключа(string value)
    {
        var problems = EcrConfigurationValidation.Validate(Config((WindowsKey, value)));

        var problem = Assert.Single(problems);
        Assert.Contains(WindowsKey, problem, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Validate(Config((WindowsKey, "600"))));
    }

    private static PartitionedRateLimiter<HttpContext> Limiter(params (string Key, string Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddEcrRateLimiting(Config(settings));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
    }

    private static DefaultHttpContext Windows(string socket, string? forwarded = null)
        => Request(socket, forwarded, "/api/v1/login/windows");

    private static DefaultHttpContext Local(string socket, string? forwarded = null)
        => Request(socket, forwarded, "/api/v1/login/local");

    private static DefaultHttpContext Request(string socket, string? forwarded, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(socket);
        context.Request.Path = path;
        if (forwarded is not null)
        {
            context.Request.Headers[ForwardedClientAddress.ForwardedForHeader] = forwarded;
        }

        return context;
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
