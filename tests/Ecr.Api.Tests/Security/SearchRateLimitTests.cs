// tests/Ecr.Api.Tests/Security/SearchRateLimitTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>Межа частоти <c>GET /api/v1/search</c> на користувача (BE-19).</summary>
[Collection("SqlServer")]
public sealed class SearchRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Search-Limit-2026!";

    /// <summary>Межа — ЛІТЕРАЛОМ: константа продукту рухалася б разом із перевіркою.</summary>
    private const int Permit = 30;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тридцять_запитів_проходять_тридцять_перший_дає_429_ECR_REQ_0429()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 1; i <= Permit; i++)
        {
            using var allowed = await SearchAsync(client).ConfigureAwait(true);
            Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"запит №{i}: {allowed.StatusCode}");
        }

        using var rejected = await SearchAsync(client).ConfigureAwait(true);

        // Мутація: прибрати `[EnableRateLimiting]` із `SearchController.Search` — падає тут.
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);

        var body = await rejected.Content.ReadAsStringAsync().ConfigureAwait(true);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());

        // Заголовок і подробиця — з каталогу, а не сам код чи запасне речення.
        Assert.NotEqual("ECR-REQ-0429", json.GetProperty("title").GetString());
        Assert.Contains("Retry-After", json.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вичерпана_межа_одного_користувача_не_блокує_іншого()
    {
        using var app = new EcrApiFactory(sql);
        using var greedy = await SignedInAsync(app).ConfigureAwait(true);
        using var other = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 0; i < Permit; i++)
        {
            using var _ = await SearchAsync(greedy).ConfigureAwait(true);
        }

        using var blocked = await SearchAsync(greedy).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // Мутація: сталий ключ розділу в `SearchRateLimitPolicy.GetPartition` — падає тут.
        using var allowed = await SearchAsync(other).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    private static Task<HttpResponseMessage> SearchAsync(HttpClient client)
        => client.GetAsync(new Uri("/api/v1/search?q=zz", UriKind.Relative));

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"srl_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"вхід {name}: {login.StatusCode}; {app.ErrorsText}");
        return client;
    }
}
