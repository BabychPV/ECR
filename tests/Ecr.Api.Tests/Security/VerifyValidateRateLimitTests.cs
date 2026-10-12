// tests/Ecr.Api.Tests/Security/VerifyValidateRateLimitTests.cs

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Ecr.Api.Auth;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Межа частоти <c>POST /reports/snapshots/{id}/verify</c> (L1-05) і <c>POST /documents/{id}/validate</c> (P1-08)
/// на користувача: 100 за хвилину типово, понад межу — 429 <c>ECR-REQ-0429</c> із <c>Retry-After</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class VerifyValidateRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Verify-Limit-2026!";

    /// <summary>Мала межа для інтеграційних перевірок (задається налаштуванням, не константою продукту).</summary>
    private const int SmallPermit = 3;

    private static DefaultHttpContext ContextOf(string? userId)
    {
        var context = new DefaultHttpContext();
        if (userId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthenticationSetup.UserIdClaim, userId)], "test"));
        }

        return context;
    }

    private static int AllowedInFirstWindow(UserPerMinuteRateLimitPolicy policy, string userId)
    {
        var partition = policy.GetPartition(ContextOf(userId));
        using var limiter = partition.Factory(partition.PartitionKey);

        var allowed = 0;
        for (var i = 0; i < 1000; i++)
        {
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                break;
            }

            allowed++;
        }

        return allowed;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_ключів_конфігурації_діє_дефолт_100_на_хвилину_для_обох_політик()
    {
        var empty = new ConfigurationBuilder().Build();

        Assert.Equal(100, UserPerMinuteRateLimitPolicy.DefaultPermitPerMinute);
        Assert.Equal(100, AllowedInFirstWindow(new SnapshotVerifyRateLimitPolicy(empty), "1"));
        Assert.Equal(100, AllowedInFirstWindow(new DocumentValidateRateLimitPolicy(empty), "1"));
        Assert.Empty(EcrConfigurationValidation.Validate(empty));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Межа_береться_з_власного_ключа_кожної_політики()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SnapshotVerifyRateLimitPolicy.PermitKey] = "4",
            [DocumentValidateRateLimitPolicy.PermitKey] = "7",
        }).Build();

        Assert.Equal(4, AllowedInFirstWindow(new SnapshotVerifyRateLimitPolicy(cfg), "1"));
        Assert.Equal(7, AllowedInFirstWindow(new DocumentValidateRateLimitPolicy(cfg), "1"));

        var zero = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SnapshotVerifyRateLimitPolicy.PermitKey] = "0",
        }).Build();
        Assert.Single(EcrConfigurationValidation.Validate(zero));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Розділ_за_користувачем_окремий_від_іншого_користувача_й_анонімів()
    {
        var policy = new SnapshotVerifyRateLimitPolicy(new ConfigurationBuilder().Build());

        var userOne = policy.GetPartition(ContextOf("1")).PartitionKey;
        var userTwo = policy.GetPartition(ContextOf("2")).PartitionKey;
        var anonymous = policy.GetPartition(ContextOf(null)).PartitionKey;

        Assert.NotEqual(userOne, userTwo);
        Assert.NotEqual(userOne, anonymous);
        Assert.Equal(userOne, policy.GetPartition(ContextOf("1")).PartitionKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_зрізу_понад_межу_дає_429_з_Retry_After_а_інший_користувач_не_обмежений()
    {
        using var app = ConfiguredApp();
        using var greedy = await SignedInAsync(app).ConfigureAwait(true);
        using var other = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 1; i <= SmallPermit; i++)
        {
            using var allowed = await VerifyAsync(greedy).ConfigureAwait(true);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await VerifyAsync(greedy).ConfigureAwait(true);

        // Мутація: прибрати `[EnableRateLimiting]` з `ReportsController.VerifySnapshot` — падає тут.
        await AssertRejectedAsync(rejected).ConfigureAwait(true);

        // Мутація: сталий ключ розділу в `UserPerMinuteRateLimitPolicy.GetPartition` — падає тут.
        using var neighbour = await VerifyAsync(other).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Валідація_документа_понад_межу_дає_429_і_не_ділить_межу_з_перевіркою_зрізу()
    {
        using var app = ConfiguredApp();
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 1; i <= SmallPermit; i++)
        {
            using var allowed = await ValidateAsync(client).ConfigureAwait(true);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await ValidateAsync(client).ConfigureAwait(true);

        // Мутація: прибрати `[EnableRateLimiting]` з `DocumentsController.Validate` — падає тут.
        await AssertRejectedAsync(rejected).ConfigureAwait(true);

        // Політики мають окремі лічильники: вичерпана валідація не гасить перевірку зрізу.
        using var verify = await VerifyAsync(client).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, verify.StatusCode);
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage rejected)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.True(rejected.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString()));
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client)
        => client.PostAsync(new Uri("/api/v1/reports/snapshots/999999/verify", UriKind.Relative), null);

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient client)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/documents/999999/validate", UriKind.Relative),
            new { periodKey = "2026" });

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> ConfiguredApp()
    {
        var permit = SmallPermit.ToString(CultureInfo.InvariantCulture);

        return new EcrApiFactory(sql).WithWebHostBuilder(b =>
        {
            b.UseSetting(SnapshotVerifyRateLimitPolicy.PermitKey, permit);
            b.UseSetting(DocumentValidateRateLimitPolicy.PermitKey, permit);
        });
    }

    private async Task<HttpClient> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var name = $"vvl_{Guid.NewGuid():N}"[..20];

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

        Assert.True(login.IsSuccessStatusCode, $"вхід {name}: {login.StatusCode}");
        return client;
    }
}
