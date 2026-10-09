// tests/Ecr.Api.Tests/Security/SessionUserMiddlewareTests.cs

using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.Api.Security;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <see cref="SessionUserMiddleware"/> без хоста і бази (AN-108 / S2-05): небезпечний запит вкладки, що
/// вважає себе іншим користувачем (<c>X-Ecr-User</c>), ніж власник cookie, відхиляється ДО обробника;
/// без заголовка, зі збігом, безпечні методи і вхід/вихід — проходять.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: <c>IsMismatch</c> завжди <c>false</c> — червоніють <c>…_відхилено</c>; завжди
/// <c>true</c> або перевіряти й <c>GET</c> / без заголовка — червоніють <c>…_проходить</c>.
/// </remarks>
public sealed class SessionUserMiddlewareTests
{
    private const int Owner = 42;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "S2-05")]
    [InlineData("PATCH", "7")]
    [InlineData("POST", "7")]
    [InlineData("PUT", "43")]
    [InlineData("DELETE", "7")]
    [InlineData("PATCH", "не-число")]
    [InlineData("PATCH", "-42")]
    public async Task Вкладка_іншого_користувача_відхилено(string method, string header)
    {
        var (context, nextCalled) = Request(method, header);

        var denied = await Assert.ThrowsAsync<BusinessRuleException>(() => Invoke(context, nextCalled)).ConfigureAwait(true);

        Assert.Equal(ErrorCodes.SessionUserMismatch, denied.ErrorCode);
        Assert.Equal(SessionUserMiddleware.MessageKey, denied.Details?["messageKey"]);
        Assert.False(nextCalled.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "S2-05")]
    [InlineData("PATCH", null, "/api/v1/documents/1/cells")]        // без заголовка: скрипти, служби, tools/land
    [InlineData("PATCH", "", "/api/v1/documents/1/cells")]
    [InlineData("PATCH", "42", "/api/v1/documents/1/cells")]        // той самий користувач
    [InlineData("PATCH", " 42 ", "/api/v1/documents/1/cells")]
    [InlineData("GET", "7", "/api/v1/documents/1/tables/2")]        // безпечний метод не перевіряється
    [InlineData("POST", "7", "/api/v1/logout")]                     // вихід і вхід — легальна зміна користувача
    [InlineData("POST", "7", "/api/v1/login/local")]
    [InlineData("POST", "7", "/API/V1/LOGIN/WINDOWS")]
    public async Task Без_розбіжності_проходить(string method, string? header, string path)
    {
        var (context, nextCalled) = Request(method, header, path);

        await Invoke(context, nextCalled).ConfigureAwait(true);

        Assert.True(nextCalled.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "S2-05")]
    public async Task Анонімний_запит_не_тут_відмовить_авторизація()
    {
        var (context, nextCalled) = Request("PATCH", "7", authenticated: false);

        await Invoke(context, nextCalled).ConfigureAwait(true);

        Assert.True(nextCalled.Value);
    }

    private static Task Invoke(HttpContext context, StrongBox nextCalled)
        => new SessionUserMiddleware(_ =>
        {
            nextCalled.Value = true;
            return Task.CompletedTask;
        }).InvokeAsync(context);

    private static (HttpContext Context, StrongBox NextCalled) Request(
        string method, string? header, string path = "/api/v1/documents/1/cells", bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (header is not null)
        {
            context.Request.Headers[SessionUserMiddleware.HeaderName] = header;
        }

        context.User = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthenticationSetup.UserIdClaim, Owner.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return (context, new StrongBox());
    }

    private sealed class StrongBox
    {
        public bool Value { get; set; }
    }
}
