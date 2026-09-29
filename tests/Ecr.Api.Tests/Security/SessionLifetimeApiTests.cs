// tests/Ecr.Api.Tests/Security/SessionLifetimeApiTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Api.Auth;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S21: вихід робить cookie недійсною на сервері; сесія не довша за
/// <see cref="AuthenticationSetup.AbsoluteSessionLifetime"/> від входу.
/// </summary>
[Collection("SqlServer")]
public sealed class SessionLifetimeApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Session-Life-2026!";

    private static readonly Uri Me = new("/api/v1/me", UriKind.Relative);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    [Trait("Finding", "S21")]
    public async Task Після_виходу_скопійована_cookie_дає_401()
    {
        await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var login = await LoginAsync(client).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        // «Скопійована» cookie — те, що лишилося в чужому браузері чи перехоплене.
        var copied = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        using var thief = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        thief.DefaultRequestHeaders.Add("Cookie", copied);

        Assert.Equal(HttpStatusCode.OK, (await thief.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        var logout = await client.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), content: null)
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // ⛔ До S21 вихід лише стирав cookie в браузері — копія відповідала 200.
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.GetAsync(Me).ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S21")]
    public async Task Активна_сесія_живе_в_межах_12_годин_і_не_довше()
    {
        await ArrangeAsync().ConfigureAwait(true);

        var time = new ShiftedTime();
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, options => options.TimeProvider = time)));
        using var client = app.CreateClient();

        var login = await LoginAsync(client).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}");

        // Запит кожні 5 год: ковзний строк (8 год) щоразу продовжує cookie —
        // саме так «підтримувана» сесія до S21 жила безкінечно.
        time.Offset = TimeSpan.FromHours(5);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        time.Offset = TimeSpan.FromHours(10);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        // 13 год від входу, 3 год від останнього продовження — ковзний строк
        // ще дійсний, абсолютний — ні.
        time.Offset = TimeSpan.FromHours(13);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);
    }

    private Task<HttpResponseMessage> LoginAsync(HttpClient client)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"life_{_tag}", password = Password });

    private async Task ArrangeAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = new User($"life_{_tag}", $"life_{_tag}", AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Годинник cookie, зсунутий уперед на задану величину.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
