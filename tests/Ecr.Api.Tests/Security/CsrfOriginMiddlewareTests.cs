// tests/Ecr.Api.Tests/Security/CsrfOriginMiddlewareTests.cs

using Ecr.Api.Security;
using Ecr.Application.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <see cref="CsrfOriginMiddleware"/> без хоста і бази (<c>L1-04</c>): небезпечний
/// запит, який браузер позначив як чужий, відхиляється ДО обробника; усе, що
/// браузер позначив як своє, і все без позначок, — проходить.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (лише локально):
/// <list type="bullet">
/// <item><c>IsCrossSite</c> завжди <c>false</c> — червоніють усі
/// <c>…_відхилено</c>;</item>
/// <item>додати <c>same-site</c> до дозволених — червоніє
/// <see cref="SameSite_сусідній_хост_відхилено"/> (сама атака аудиту);</item>
/// <item>завжди <c>true</c> або перевіряти й <c>GET</c> — червоніють
/// <c>…_проходить</c>;</item>
/// <item>не звіряти з <c>X-Forwarded-Host</c> — червоніють випадки проксі
/// з <c>true</c>.</item>
/// </list>
/// </remarks>
public sealed class CsrfOriginMiddlewareTests
{
    private const string Host = "ecr.corp.local";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Небезпечний_метод_з_чужим_Origin_відхилено(string method)
    {
        var (context, nextCalled) = Request(method, origin: "https://evil.example");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal(CsrfOriginMiddleware.MessageKey, denied.Details?["messageKey"]);
        Assert.False(nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData("cross-site")]
    [InlineData("CROSS-SITE")]
    [InlineData("щось-нове")]
    public async Task Sec_Fetch_Site_чужий_відхилено_навіть_зі_своїм_Origin(string site)
    {
        var (context, nextCalled) = Request("POST", origin: $"https://{Host}", site: site);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);
        Assert.False(nextCalled.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    public async Task SameSite_сусідній_хост_відхилено()
    {
        // Саме те, від чого не рятує `SameSite=Strict`: cookie їде, бо сайт той самий.
        var (context, nextCalled) = Request("POST", origin: "https://evil.corp.local", site: "same-site");

        await Assert.ThrowsAsync<AccessDeniedException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);
        Assert.False(nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData("null")]
    [InlineData("https://evil.corp.local")]
    [InlineData("https://ecr.corp.local:8443")]
    [InlineData("http://ecr.corp.local:5080")]
    [InlineData("file://ecr.corp.local")]
    [InlineData("не-адреса")]
    public async Task Без_Sec_Fetch_Site_Origin_іншого_хоста_чи_порту_відхилено(string origin)
    {
        var (context, nextCalled) = Request("POST", origin: origin);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);
        Assert.False(nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData(null, null, Host)]                                     // не браузер: скрипт, служба, тест, curl
    [InlineData(null, "same-origin", Host)]                            // браузер на своєму origin
    [InlineData("https://evil.example", "same-origin", Host)]          // вирішує Sec-Fetch-Site
    [InlineData(null, "none", Host)]                                   // дія самого користувача
    [InlineData("https://ecr.corp.local", null, Host)]                 // старий браузер, той самий хост
    [InlineData("https://ECR.corp.local", null, Host)]                 // хост без огляду на регістр
    [InlineData("https://ecr.corp.local", null, "ecr.corp.local:443")]
    [InlineData("http://ecr.corp.local", null, Host)]                  // TLS знято проксі: схема не порівнюється
    [InlineData("http://localhost:5075", null, "localhost:5075")]      // dev
    [InlineData("http://localhost:5085", "same-origin", "localhost:5084")] // dev/Playwright через проксі Vite: замір Chromium
    public async Task Свій_або_непозначений_запит_проходить(string? origin, string? site, string host)
    {
        var (context, nextCalled) = Request("POST", origin: origin, site: site, host: host);

        await Invoke(context, nextCalled).ConfigureAwait(true);

        Assert.True(nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData("https://ecr.corp.local", "ecr.corp.local", true)]               // проксі переписав Host, зовнішній — у X-Forwarded-Host
    [InlineData("https://ecr.corp.local", "ecr.corp.local, inner.local", true)]  // ланцюжок проксі: перший — зовнішній
    [InlineData("https://ecr.corp.local:8443", "ecr.corp.local:8443", true)]
    [InlineData("https://evil.example", "ecr.corp.local", false)]
    [InlineData("https://ecr.corp.local", "", false)]                             // проксі переписав Host і нічого не передав
    public async Task За_проксі_без_Sec_Fetch_Site_Origin_звіряється_і_з_X_Forwarded_Host(
        string origin, string forwardedHost, bool passes)
    {
        var (context, nextCalled) = Request("POST", origin: origin, host: "app01:5080");
        context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        if (passes)
        {
            await Invoke(context, nextCalled).ConfigureAwait(true);
        }
        else
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);
        }

        Assert.Equal(passes, nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData("same-origin", true)]
    [InlineData("cross-site", false)]
    public async Task За_проксі_Sec_Fetch_Site_вирішує_незалежно_від_Host(string site, bool passes)
    {
        // Сучасний браузер: Host переписано проксі, X-Forwarded-Host немає — байдуже.
        var (context, nextCalled) = Request("POST", origin: "https://ecr.corp.local", site: site, host: "app01:5080");

        if (passes)
        {
            await Invoke(context, nextCalled).ConfigureAwait(true);
        }
        else
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);
        }

        Assert.Equal(passes, nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "L1-04")]
    [InlineData("GET", "/api/v1/documents")]
    [InlineData("HEAD", "/api/v1/documents")]
    [InlineData("OPTIONS", "/api/v1/documents")]
    [InlineData("GET", "/health/ready")]
    [InlineData("POST", "/api/v1/csp-report")]
    [InlineData("POST", "/API/V1/CSP-REPORT")]
    public async Task Безпечний_метод_і_приймач_CSP_не_перевіряються(string method, string path)
    {
        var (context, nextCalled) = Request(method, origin: "https://evil.example", site: "cross-site", path: path);

        await Invoke(context, nextCalled).ConfigureAwait(true);

        Assert.True(nextCalled.Value);
    }

    private static Task Invoke(HttpContext context, StrongBox nextCalled)
        => new CsrfOriginMiddleware(_ =>
        {
            nextCalled.Value = true;
            return Task.CompletedTask;
        }).InvokeAsync(context);

    private static (HttpContext Context, StrongBox NextCalled) Request(
        string method, string? origin = null, string? site = null, string host = Host, string path = "/api/v1/documents")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (site is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = site;
        }

        return (context, new StrongBox());
    }

    private sealed class StrongBox
    {
        public bool Value { get; set; }
    }
}
