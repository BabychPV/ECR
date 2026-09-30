// tests/Ecr.Api.Tests/Startup/HttpsTransportTests.cs
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ecr.Api.Startup;
using Ecr.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// D14-08/R-01: сертифікат HTTPS вибирається за відбитком із <c>LocalMachine\My</c> і
/// справді віддається Kestrel; непридатний сертифікат — відмова з причиною.
/// </summary>
/// <remarks>
/// ⛔ Предмет — справжня поведінка, не форма конфігурації: тест піднімає живий Kestrel на
/// петлі, робить HTTPS-запит і читає сертифікат, який сервер ПРЕД'ЯВИВ. Сховище Windows
/// підмінено швом <c>find</c> (у CI на Linux <c>LocalMachine\My</c> недоступне), сам
/// сертифікат — справжній (самопідписаний, у пам'яті).
/// </remarks>
public sealed class HttpsTransportTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("ab cd", "ABCD")]
    [InlineData("  ab cd  ", "ABCD")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Відбиток_нормалізується_як_у_вікні_сертифіката_Windows(string? raw, string expected)
        => Assert.Equal(expected, HttpsTransport.Normalize(raw));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF012345678")]
    public void Відбиток_не_із_40_hex_відхиляється_без_звернення_до_сховища(string raw)
    {
        var touched = false;
        var selection = HttpsTransport.Select(raw, _ => { touched = true; return []; });

        Assert.Null(selection.Certificate);
        Assert.Contains("40", selection.Problem, StringComparison.Ordinal);
        Assert.False(touched);
    }

    /// <remarks>
    /// Мутація (прогнано): прибрати гілку <c>found.Count == 0</c> → <c>found[0]</c> кидає
    /// <c>ArgumentOutOfRangeException</c> замість причини, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Сертифіката_немає_в_сховищі_причина_а_не_виняток()
    {
        var selection = HttpsTransport.Select(new string('A', 40), _ => []);

        Assert.Null(selection.Certificate);
        Assert.Contains("немає в LocalMachine\\My", selection.Problem, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Мутація (прогнано): прибрати перевірку <c>HasPrivateKey</c> → сертифікат без ключа
    /// проходить, тест червоний (служба піднялася б і не змогла б завершити TLS).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Сертифікат_без_закритого_ключа_відхиляється()
    {
        using var full = NewCertificate(TimeSpan.FromDays(400));
        using var publicOnly = X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));

        var selection = HttpsTransport.Select(full.Thumbprint, _ => [publicOnly]);

        Assert.Null(selection.Certificate);
        Assert.Contains("без закритого ключа", selection.Problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Недоступне_сховище_причина_а_не_сирий_виняток()
    {
        var selection = HttpsTransport.Select(
            new string('B', 40), _ => throw new CryptographicException("store"));

        Assert.Null(selection.Certificate);
        Assert.Contains("недоступне", selection.Problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Придатний_сертифікат_шукається_за_нормалізованим_відбитком()
    {
        using var cert = NewCertificate(TimeSpan.FromDays(400));
        string? asked = null;

        var spaced = string.Join(' ', Enumerable.Range(0, 20).Select(i => cert.Thumbprint.Substring(i * 2, 2))).ToLowerInvariant();
        var selection = HttpsTransport.Select(spaced, t => { asked = t; return [cert]; });

        Assert.Equal(cert.Thumbprint, asked);
        Assert.Same(cert, selection.Certificate);
        Assert.Null(selection.Problem);
    }

    /// <remarks>
    /// ⛔ Головний доказ: Kestrel з адресою <c>https://</c> із <c>ASPNETCORE_URLS</c> віддає
    /// САМЕ сертифікат із заданим відбитком. Мутація (прогнано): у <c>ConfigureEcrHttps</c>
    /// прибрати <c>ConfigureHttpsDefaults</c> → Kestrel не знаходить сертифіката й не стартує
    /// (червоний), а з dev-сертифікатом машини — віддав би чужий (теж червоний).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Kestrel_віддає_сертифікат_із_заданим_відбитком()
    {
        using var cert = NewCertificate(TimeSpan.FromDays(400));
        await using var app = BuildApp(cert, port: null, "https://127.0.0.1:0");
        await app.StartAsync();

        string? presented = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, served, _, _) =>
            {
                presented = served?.Thumbprint;
                return true;
            },
        };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync(new Uri(AddressOf(app) + "/ping"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(cert.Thumbprint, presented);
    }

    /// <remarks>
    /// Перенаправлення — лише за <c>Transport:Https:Port</c>. Мутація (прогнано): у
    /// <c>UseEcrHttpsRedirection</c> прибрати виклик <c>UseHttpsRedirection</c> → друга половина
    /// (порт задано) дає 200 замість 308, червоний. Умова «порт задано» сама по собі не
    /// спостережна: без порту <c>UseHttpsRedirection</c> нічого не перенаправляє (перевірено
    /// мутацією <c>if (true)</c> — тест лишався зеленим), тож перша половина — лише доказ,
    /// що за замовчуванням запит іде як є.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_порту_HTTPS_перенаправлення_немає_із_портом_є_308_на_https()
    {
        using var cert = NewCertificate(TimeSpan.FromDays(400));

        await using (var plain = BuildApp(cert, port: null, "http://127.0.0.1:0"))
        {
            await plain.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var response = await client.GetAsync(new Uri(AddressOf(plain) + "/ping"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var redirecting = BuildApp(cert, port: 44399, "http://127.0.0.1:0");
        await redirecting.StartAsync();
        using var noFollow = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var redirect = await noFollow.GetAsync(new Uri(AddressOf(redirecting) + "/ping?x=1"));

        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.Equal("https://127.0.0.1:44399/ping?x=1", redirect.Headers.Location?.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Заданий_відбиток_без_сертифіката_зупиняє_старт_з_причиною()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HttpsTransport.CertificateThumbprintKey] = new string('C', 40),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.ConfigureEcrHttps(_ => []));

        Assert.Contains("немає в LocalMachine\\My", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Без_відбитка_поведінка_не_змінюється()
    {
        var builder = WebApplication.CreateBuilder();
        var called = false;

        var state = builder.ConfigureEcrHttps(_ => { called = true; return []; });

        Assert.Same(TransportState.NoCertificate, state);
        Assert.False(called);
    }

    private static WebApplication BuildApp(X509Certificate2 cert, int? port, string url)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        var settings = new Dictionary<string, string?> { [HttpsTransport.CertificateThumbprintKey] = cert.Thumbprint };
        if (port is { } p)
        {
            settings[HttpsTransport.PortKey] = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureEcrHttps(_ => [cert]);

        var app = builder.Build();
        app.UseEcrHttpsRedirection();
        app.MapGet("/ping", () => "pong");
        return app;
    }

    private static string AddressOf(WebApplication app)
        => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');

    /// <summary>
    /// Самопідписаний сертифікат із закритим ключем; перевипущений через PFX, бо ефемерний
    /// ключ Windows не годиться для SslStream («No credentials are available in the security package»).
    /// </summary>
    private static X509Certificate2 NewCertificate(TimeSpan lifetime)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ecr-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        using var ephemeral = request.CreateSelfSigned(start, start + lifetime);
        var pfx = ephemeral.Export(X509ContentType.Pfx, "t");
        return X509CertificateLoader.LoadPkcs12(pfx, "t");
    }
}
