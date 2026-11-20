// tests/Ecr.Api.Tests/Security/ScriptAuthoringRateLimitTests.cs

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Ecr.Api.Auth;
using Ecr.Api.Controllers;
using Ecr.Api.Security;
using Ecr.Application.Errors;
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
/// PSS-21: межі авторства скрипта — кошик ПРОГОНІВ (<c>script-run</c>: перегляд і паритет, типово 30/хв на користувача),
/// кошик ПЕРЕВІРОК (<c>script-check</c>: validate і format-const, 120/хв) і ворота одночасних прогонів на процес
/// (<see cref="ScriptRunGate"/>); понад межу — 429 <c>ECR-REQ-0429</c> із власним ключем.
/// </summary>
/// <remarks>
/// Детерміновано: вікно — хвилина, усі запити кейсу йдуть одразу. Методологія й версія — неіснуючі: дія відповіла б
/// 404, а обмежувач рахує ДО неї. Реєстрація — <see cref="ScriptAuthoringLimits.AddEcrScriptAuthoringLimits"/>, не
/// <c>LoginRateLimiting</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptAuthoringRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Script-Limit-2026!";
    private const string Base = "/api/v1/methodologies/999999/versions/999999/script";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PSS-21")]
    public void Без_ключа_межі_30_і_120_на_хвилину_а_ключ_їх_змінює()
    {
        var empty = new ConfigurationBuilder().Build();

        // Мутація: без власного дефолту нащадка межа — 100 (дефолт бази).
        Assert.Equal(30, AllowedInFirstWindow(new ScriptAuthoringRateLimitPolicy(empty), "1"));
        Assert.Equal(120, AllowedInFirstWindow(new ScriptCheckRateLimitPolicy(empty), "1"));

        var two = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ScriptAuthoringRateLimitPolicy.PermitKey] = "2",
            [ScriptCheckRateLimitPolicy.PermitKey] = "3",
        }).Build();
        Assert.Equal(2, AllowedInFirstWindow(new ScriptAuthoringRateLimitPolicy(two), "1"));
        Assert.Equal(3, AllowedInFirstWindow(new ScriptCheckRateLimitPolicy(two), "1"));

        // Розділ — користувач: сусід має свій кошик.
        var policy = new ScriptAuthoringRateLimitPolicy(two);
        Assert.NotEqual(policy.GetPartition(ContextOf("1")).PartitionKey, policy.GetPartition(ContextOf("2")).PartitionKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PSS-21")]
    public void Прогони_під_script_run_перевірки_під_script_check()
    {
        static string? PolicyOf(Type controller, string action)
            => controller.GetMethod(action)!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName;

        Assert.Equal(ScriptAuthoringRateLimitPolicy.PolicyName, PolicyOf(typeof(MethodologyScriptController), nameof(MethodologyScriptController.Preview)));
        Assert.Equal(ScriptAuthoringRateLimitPolicy.PolicyName, PolicyOf(typeof(MethodologyScriptMigrationController), nameof(MethodologyScriptMigrationController.Parity)));
        Assert.Equal(ScriptCheckRateLimitPolicy.PolicyName, PolicyOf(typeof(MethodologyScriptController), nameof(MethodologyScriptController.Validate)));
        Assert.Equal(ScriptCheckRateLimitPolicy.PolicyName, PolicyOf(typeof(MethodologyScriptController), nameof(MethodologyScriptController.FormatConst)));

        // Збереження скрипта — право редагування, межі прогонів на нього не діють (рішення людини §6 п. 8).
        Assert.Null(PolicyOf(typeof(MethodologyScriptController), nameof(MethodologyScriptController.Save)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PSS-21")]
    public async Task Ворота_прогонів_понад_межу_й_чергу_відмовляють_429_scriptRunBusy_а_звільнене_місце_приймає()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ScriptRunGate.ConcurrencyKey] = "1",
            [ScriptRunGate.QueueLimitKey] = "0",
        }).Build();
        using var gate = new ScriptRunGate(configuration);

        var first = await gate.EnterAsync(CancellationToken.None);

        // Мутація: ворота без межі (PermitLimit = int.MaxValue) — другий прогін проходить.
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => gate.EnterAsync(CancellationToken.None));
        Assert.Equal("ECR-REQ-0429", refused.ErrorCode);
        Assert.Equal(ScriptRunGate.DetailKey, refused.Details!["messageKey"]);

        first.Dispose();
        using var second = await gate.EnterAsync(CancellationToken.None);
        Assert.True(second.IsAcquired);

        Assert.Equal(4, ScriptRunGate.DefaultConcurrency);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PSS-21")]
    public async Task Перегляд_понад_межу_дає_429_tooManyScriptRuns_перевірки_мають_свій_кошик_а_сусід_не_обмежений()
    {
        const int permit = 2;
        using var app = new EcrApiFactory(sql).WithWebHostBuilder(b =>
        {
            b.UseSetting(ScriptAuthoringRateLimitPolicy.PermitKey, permit.ToString(CultureInfo.InvariantCulture));
            b.UseSetting(ScriptCheckRateLimitPolicy.PermitKey, "1");
        });
        using var greedy = await SignedInAsync(app).ConfigureAwait(true);
        using var other = await SignedInAsync(app).ConfigureAwait(true);

        for (var i = 0; i < permit; i++)
        {
            using var allowed = await PreviewAsync(greedy).ConfigureAwait(true);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        // Мутація: прибрати `[EnableRateLimiting]` з `Preview` — тут 403/404, не 429.
        using var rejected = await PreviewAsync(greedy).ConfigureAwait(true);
        await AssertRejectedAsync(rejected, ScriptAuthoringRateLimitPolicy.DetailKey).ConfigureAwait(true);

        // Паритет — той самий кошик прогонів.
        using var parity = await greedy.PostAsJsonAsync(
            new Uri("/api/v1/methodologies/999999/versions/999999/script/parity", UriKind.Relative),
            new { documentId = 1, periodKey = 209901 }).ConfigureAwait(true);
        await AssertRejectedAsync(parity, ScriptAuthoringRateLimitPolicy.DetailKey).ConfigureAwait(true);

        // Перевірки — свій кошик: перша проходить попри вичерпані прогони, друга — 429 tooManyScriptChecks.
        using (var check = await ValidateAsync(greedy).ConfigureAwait(true))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, check.StatusCode);
        }

        using var checkRejected = await ValidateAsync(greedy).ConfigureAwait(true);
        await AssertRejectedAsync(checkRejected, ScriptCheckRateLimitPolicy.DetailKey).ConfigureAwait(true);

        // Сусід має свій розділ.
        using var neighbour = await PreviewAsync(other).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, neighbour.StatusCode);
    }

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient client)
        => client.PostAsJsonAsync(new Uri(Base + "/preview", UriKind.Relative), new { documentId = 1, periodKey = 209901 });

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient client)
        => client.PostAsJsonAsync(new Uri(Base + "/validate", UriKind.Relative), new { text = "rows from T1;" });

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

    private static async Task AssertRejectedAsync(HttpResponseMessage rejected, string detailKey)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);

        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
        Assert.Equal(detailKey, json.GetProperty("messageKey").GetString());
    }

    private async Task<HttpClient> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
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

        Assert.True(login.IsSuccessStatusCode, $"вхід {name}: {login.StatusCode}");
        return client;
    }
}
