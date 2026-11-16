// tests/Ecr.Api.Tests/Security/DocumentExportRateLimitTests.cs

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
/// PT-6 (пентест 2026-11): межа постановки важких задач вивантаження на КОРИСТУВАЧА — політика
/// <c>document-export</c>, типово 6 за хвилину, ОДИН кошик на експорт документа, експорт огляду року й побудову
/// зрізу; понад межу — 429 <c>ECR-REQ-0429</c> / <c>tooManyExports</c> із <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Детерміновано: вікно — хвилина, усі запити кейсу йдуть одразу; годинник не чекається. Документ і звіт —
/// неіснуючі: обробник відповів би 403/404, а обмежувач рахує ДО нього.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentExportRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Export-Limit-2026!";
    private const string ExportPath = "/api/v1/documents/999999/export";
    private const string YearExportPath = "/api/v1/documents/999999/calculation-results/year/export";
    private const string BuildPath = "/api/v1/reports/NOSUCHREPORT/build";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PT-6")]
    public void Без_ключа_межа_експорту_6_на_хвилину_а_ключ_її_змінює()
    {
        var empty = new ConfigurationBuilder().Build();

        // Мутація: без власного дефолту нащадка межа — 100 (дефолт бази).
        Assert.Equal(6, AllowedInFirstWindow(new DocumentExportRateLimitPolicy(empty), "1"));

        var two = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DocumentExportRateLimitPolicy.PermitKey] = "2",
        }).Build();
        Assert.Equal(2, AllowedInFirstWindow(new DocumentExportRateLimitPolicy(two), "1"));

        var zero = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DocumentExportRateLimitPolicy.PermitKey] = "0",
        }).Build();

        // Мутація: прибрати ключ із мінімумів `EcrConfigurationValidation` — нуль проходить мовчки.
        Assert.Single(EcrConfigurationValidation.Validate(zero));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PT-6")]
    public void Експорти_і_побудова_зрізу_під_політикою_document_export()
    {
        Assert.Equal(
            DocumentExportRateLimitPolicy.PolicyName,
            typeof(DocumentsController).GetMethod(nameof(DocumentsController.Export))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(
            DocumentExportRateLimitPolicy.PolicyName,
            typeof(DocumentsController).GetMethod(nameof(DocumentsController.ExportCalculationResultsYear))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal(
            DocumentExportRateLimitPolicy.PolicyName,
            typeof(ReportsController).GetMethod(nameof(ReportsController.Build))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PT-6")]
    public async Task Експорт_понад_межу_дає_429_tooManyExports_кошик_спільний_а_інший_користувач_не_обмежений()
    {
        const int permit = 2;
        using var app = new EcrApiFactory(sql).WithWebHostBuilder(b =>
            b.UseSetting(DocumentExportRateLimitPolicy.PermitKey, permit.ToString(CultureInfo.InvariantCulture)));
        using var greedy = await SignedInAsync(app).ConfigureAwait(true);
        using var other = await SignedInAsync(app).ConfigureAwait(true);

        // Два дозволені: експорт документа й експорт огляду року — з ОДНОГО кошика.
        using (var first = await ExportAsync(greedy).ConfigureAwait(true))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        }

        using (var second = await YearExportAsync(greedy).ConfigureAwait(true))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        }

        // Мутація: прибрати `[EnableRateLimiting]` з `DocumentsController.Export` — тут 403/404, не 429.
        using var rejected = await ExportAsync(greedy).ConfigureAwait(true);
        await AssertRejectedAsync(rejected).ConfigureAwait(true);

        // Мутація: прибрати атрибут з `ExportCalculationResultsYear` / `ReportsController.Build` — не 429.
        using var rejectedYear = await YearExportAsync(greedy).ConfigureAwait(true);
        await AssertRejectedAsync(rejectedYear).ConfigureAwait(true);
        using var rejectedBuild = await BuildAsync(greedy).ConfigureAwait(true);
        await AssertRejectedAsync(rejectedBuild).ConfigureAwait(true);

        // Сусід має свій розділ.
        using var neighbour = await ExportAsync(other).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour.StatusCode);

        // Вичерпаний кошик не гасить читання того самого користувача.
        using var read = await greedy.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, read.StatusCode);
    }

    private static Task<HttpResponseMessage> ExportAsync(HttpClient client)
        => client.PostAsJsonAsync(new Uri(ExportPath, UriKind.Relative), new { periodKey = 202601 });

    private static Task<HttpResponseMessage> YearExportAsync(HttpClient client)
        => client.PostAsJsonAsync(new Uri(YearExportPath, UriKind.Relative), new { year = 2026 });

    private static Task<HttpResponseMessage> BuildAsync(HttpClient client)
        => client.PostAsJsonAsync(new Uri(BuildPath, UriKind.Relative), new { projectId = 1, periodKey = 202601 });

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

    private static async Task AssertRejectedAsync(HttpResponseMessage rejected)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.True(rejected.Headers.RetryAfter!.Delta > TimeSpan.Zero);

        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        Assert.Equal(DocumentExportRateLimitPolicy.DetailKey, json.GetProperty("messageKey").GetString());
    }

    private async Task<HttpClient> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var name = $"exl_{Guid.NewGuid():N}"[..20];

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
