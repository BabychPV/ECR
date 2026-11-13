// tests/Ecr.Api.Tests/Security/TableDiscussionRateLimitTests.cs

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Ecr.Api.Auth;
using Ecr.Api.Controllers;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S-6 (аудит 2026-11-10, рішення PO 11.11 «(а) додати»): межа частоти чату таблиці на КОРИСТУВАЧА.
/// <c>GET …/mention-candidates</c> ділить кошик пошуку (<c>search</c>, 30 за 10 с); <c>POST …/messages</c> —
/// власна політика <c>discussion-post</c>, 20 за хвилину; понад межу — 429 <c>ECR-REQ-0429</c> із <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Детерміновано: вікна задано налаштуванням (хвилина для запису, година для пошуку), а всі запити кейсу йдуть одразу,
/// тож межа вікна посеред кейсу не перетинається; годинник не чекається.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableDiscussionRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Chat-Limit-2026!";

    /// <summary>Неіснуючий документ: обробник відповідає 403/404, а обмежувач рахує ДО нього.</summary>
    private const string MessagesPath = "/api/v1/documents/999999/tables/T1/messages";

    private const string CandidatesPath = "/api/v1/documents/999999/tables/T1/mention-candidates?q=ab";

    private static DefaultHttpContext ContextOf(string userId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthenticationSetup.UserIdClaim, userId)], "test")),
    };

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
    public void Без_ключа_межа_запису_в_чат_20_на_хвилину_а_ключ_її_змінює()
    {
        var empty = new ConfigurationBuilder().Build();

        // Мутація: без власного дефолту нащадка (`defaultPermit` у базі) межа — 100.
        Assert.Equal(20, AllowedInFirstWindow(new TableDiscussionPostRateLimitPolicy(empty), "1"));

        // Наявні нащадки бази свого дефолту не змінили.
        Assert.Equal(100, AllowedInFirstWindow(new DocumentValidateRateLimitPolicy(empty), "1"));

        var four = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TableDiscussionPostRateLimitPolicy.PermitKey] = "4",
        }).Build();
        Assert.Equal(4, AllowedInFirstWindow(new TableDiscussionPostRateLimitPolicy(four), "1"));

        var zero = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TableDiscussionPostRateLimitPolicy.PermitKey] = "0",
        }).Build();

        // Мутація: прибрати ключ із мінімумів `EcrConfigurationValidation` — нуль проходить мовчки.
        Assert.Single(EcrConfigurationValidation.Validate(zero));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Дії_чату_під_своїми_політиками()
    {
        var controller = typeof(TableDiscussionsController);

        Assert.Equal(
            SearchRateLimitPolicy.PolicyName,
            controller.GetMethod(nameof(TableDiscussionsController.MentionCandidates))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(
            TableDiscussionPostRateLimitPolicy.PolicyName,
            controller.GetMethod(nameof(TableDiscussionsController.Post))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        // Читання повідомлень межі не має: опитування живого оновлення йде кожні 10 с.
        Assert.Null(controller.GetMethod(nameof(TableDiscussionsController.Messages))!
            .GetCustomAttribute<EnableRateLimitingAttribute>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_у_чат_понад_межу_дає_429_tooManyChatMessages_а_інший_користувач_не_обмежений()
    {
        const int permit = 3;
        using var app = new EcrApiFactory(sql).WithWebHostBuilder(b =>
            b.UseSetting(TableDiscussionPostRateLimitPolicy.PermitKey, permit.ToString(CultureInfo.InvariantCulture)));
        using var greedy = await SignedInAsync(app).ConfigureAwait(true);
        using var other = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 1; i <= permit; i++)
        {
            using var allowed = await PostAsync(greedy).ConfigureAwait(true);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await PostAsync(greedy).ConfigureAwait(true);

        // Мутація: прибрати `[EnableRateLimiting]` з `TableDiscussionsController.Post` — тут 404, не 429.
        await AssertRejectedAsync(rejected, TableDiscussionPostRateLimitPolicy.DetailKey).ConfigureAwait(true);

        // Сусід має свій розділ.
        using var neighbour = await PostAsync(other).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour.StatusCode);

        // Вичерпаний запис не гасить читання чату того самого користувача.
        using var read = await greedy.GetAsync(new Uri(MessagesPath, UriKind.Relative)).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, read.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кандидати_згадок_ділять_кошик_пошуку()
    {
        using var app = new EcrApiFactory(sql).WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:RateLimit:SearchPermit", "2");
            b.UseSetting("Security:RateLimit:SearchWindowSeconds", "3600");
        });
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        using var other = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 1; i <= 2; i++)
        {
            using var allowed = await client.GetAsync(new Uri(CandidatesPath, UriKind.Relative)).ConfigureAwait(true);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await client.GetAsync(new Uri(CandidatesPath, UriKind.Relative)).ConfigureAwait(true);

        // Мутація: прибрати `[EnableRateLimiting]` з `TableDiscussionsController.MentionCandidates` — тут 404, не 429.
        await AssertRejectedAsync(rejected, "err.ECR-REQ-0429.tooManySearches").ConfigureAwait(true);

        // Кошик спільний з пошуком: вичерпані кандидати гасять і `GET /search` того самого користувача.
        using var search = await client.GetAsync(new Uri("/api/v1/search?q=ab", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, search.StatusCode);

        // Запис у чат — інша політика: кошик пошуку його не гасить.
        using var post = await PostAsync(client).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, post.StatusCode);

        // Інший користувач — свій розділ.
        using var neighbour = await other.GetAsync(new Uri(CandidatesPath, UriKind.Relative)).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client)
        => client.PostAsJsonAsync(new Uri(MessagesPath, UriKind.Relative), new { body = "x", periodKey = 202601 });

    private static async Task AssertRejectedAsync(HttpResponseMessage rejected, string messageKey)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.True(rejected.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        Assert.Equal(messageKey, json.GetProperty("messageKey").GetString());
    }

    private async Task<HttpClient> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var name = $"tcl_{Guid.NewGuid():N}"[..20];

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
