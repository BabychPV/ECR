using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Ecr.Domain.Entities.Security;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Роль, призначена ГРУПІ AD, діє ПІСЛЯ входу — на запитах уже з нашою cookie
/// (<c>P-02</c>, ФВ-6.15/6.15a).
/// </summary>
/// <remarks>
/// ⛔ Наскрізно, через справжню cookie: тести з підставленим
/// <c>ICurrentUser</c> були зелені, доки групи з квитка не доживали навіть до
/// другого запиту. Negotiate у TestServer не працює (`Q-054`), тому квиток
/// подає тестова схема з ТИМ САМИМ іменем — усе після неї продуктове.
/// </remarks>
[Collection("SqlServer")]
public sealed class WindowsGroupRolesTests(SqlServerFixture sql)
{
    private const string ViewHealth = "System.ViewHealth";

    private static readonly Uri Login = new("/api/v1/login/windows", UriKind.Relative);
    private static readonly Uri Facts = new("/api/v1/health/facts", UriKind.Relative);

    private static readonly WebApplicationFactoryClientOptions NoCookieJar = new() { HandleCookies = false };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Роль_групи_діє_на_наступному_запиті_з_cookie()
    {
        var group = await ArrangeGroupRoleAsync();

        using var app = new EcrApiFactory(sql);
        using var host = WithFakeNegotiate(app);

        var cookie = await SignInAsync(host, app, NewSid(), [group]);
        using var response = await GetAsync(host, Facts, cookie);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15")]
    public async Task Без_групи_в_квитку_права_немає()
    {
        await ArrangeGroupRoleAsync();

        using var app = new EcrApiFactory(sql);
        using var host = WithFakeNegotiate(app);

        var cookie = await SignInAsync(host, app, NewSid(), [NewSid()]);
        using var response = await GetAsync(host, Facts, cookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.15a")]
    public async Task Із_двохсот_груп_квитка_в_cookie_йде_лише_та_що_має_призначення()
    {
        var group = await ArrangeGroupRoleAsync();
        var ticket = Enumerable.Range(0, 199).Select(_ => NewSid()).Append(group).ToArray();

        using var app = new EcrApiFactory(sql);
        using var host = WithFakeNegotiate(app);

        var cookie = await SignInAsync(host, app, NewSid(), ticket);

        // ⛔ Одна cookie, а не `chunks-N`: увесь квиток роздув би її за ~4 КБ.
        var value = cookie[(cookie.IndexOf('=', StringComparison.Ordinal) + 1)..];
        Assert.DoesNotContain("chunks-", value, StringComparison.Ordinal);
        Assert.True(cookie.Length < 2048, $"Cookie {cookie.Length} символів.");

        var principal = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme)
            .TicketDataFormat.Unprotect(value)?.Principal;

        Assert.NotNull(principal);
        Assert.Equal(group, Assert.Single(principal.FindAll(ClaimTypes.GroupSid)).Value);

        using var response = await GetAsync(host, Facts, cookie);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");
    }

    private static string NewSid() => $"S-1-5-21-{Guid.NewGuid():N}"[..40];

    /// <summary>Роль із правом <see cref="ViewHealth"/>, призначена лише групі.</summary>
    private async Task<string> ArrangeGroupRoleAsync()
    {
        var group = NewSid();

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var role = new Role(
            Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Group role test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission(role.Id, ViewHealth));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, userId: null, principalSid: group));
        await db.SaveChangesAsync();

        return group;
    }

    private static WebApplicationFactory<Program> WithFakeNegotiate(EcrApiFactory app)
        => app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FakeNegotiateHandler>(
                NegotiateDefaults.AuthenticationScheme, _ => { })));

    /// <summary>Доменний вхід; віддає cookie рядком — так, як її носить браузер.</summary>
    private static async Task<string> SignInAsync(
        WebApplicationFactory<Program> host, EcrApiFactory app, string sid, IReadOnlyList<string> groups)
    {
        using var client = host.CreateClient(NoCookieJar);
        using var request = new HttpRequestMessage(HttpMethod.Post, Login);
        request.Headers.Add(FakeNegotiateHandler.SidHeader, sid);
        request.Headers.Add(FakeNegotiateHandler.GroupsHeader, string.Join(',', groups));

        using var login = await client.SendAsync(request);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetAsync(
        WebApplicationFactory<Program> host, Uri address, string cookie)
    {
        using var client = host.CreateClient(NoCookieJar);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Add("Cookie", cookie);

        return await client.SendAsync(request);
    }

    /// <summary>Квиток Negotiate із заголовків запиту: PrimarySid, Name, GroupSid.</summary>
    private sealed class FakeNegotiateHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SidHeader = "X-Test-Sid";
        public const string GroupsHeader = "X-Test-Groups";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sid = Request.Headers[SidHeader].ToString();
            if (string.IsNullOrEmpty(sid))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = Request.Headers[GroupsHeader].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(g => new Claim(ClaimTypes.GroupSid, g))
                .Append(new Claim(ClaimTypes.PrimarySid, sid))
                .Append(new Claim(ClaimTypes.Name, $"TEST\\{sid[^12..]}"));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
