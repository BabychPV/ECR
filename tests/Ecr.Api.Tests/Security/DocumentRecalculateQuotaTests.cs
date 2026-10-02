// tests/Ecr.Api.Tests/Security/DocumentRecalculateQuotaTests.cs

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Межа частоти перерахунку документа: <c>POST /api/v1/documents/{id}/recalculate</c>, на пару
/// «користувач + документ», типово 6/хв; відхилені запити межу не витрачають.
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>[DocumentRecalculateQuota]</c> з <c>DocumentsController.Recalculate</c> — червоніють
/// інтеграційні тести 1–3; прибрати <c>quota.Refund</c> у <c>DocumentRecalculateQuotaMiddleware</c> —
/// червоніє тест «відхилені запити не витрачають межу»; ключ без документа — червоніє «інший документ».
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentRecalculateQuotaTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-RecalcQuota-2026!";

    /// <summary>Межа тесту — ключем конфігурації; літерал, а не константа продукту.</summary>
    private const int Permit = 2;

    private string _userName = string.Empty;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_у_циклі_не_перевищує_межу_N_плюс_перший_запит_дає_429_з_Retry_After()
    {
        var (client, doc, app) = await SignInAsync(["Document.View"], true).ConfigureAwait(true);
        using var _ = app;

        for (var i = 1; i <= Permit; i++)
        {
            using var allowed = await RecalcAsync(client, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        }

        using var rejected = await RecalcAsync(client, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-REQ-0429", json.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Той_самий_ліміт_діє_для_власника_Calculation_Recalculate()
    {
        var (client, doc, app) = await SignInAsync(["Document.View", "Calculation.Recalculate"], true).ConfigureAwait(true);
        using var _ = app;

        for (var i = 0; i < Permit; i++)
        {
            using var allowed = await RecalcAsync(client, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        }

        using var rejected = await RecalcAsync(client, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інший_документ_і_інший_користувач_межею_не_блокуються()
    {
        var (client, doc, app) = await SignInAsync(["Document.View"], true).ConfigureAwait(true);
        using var _ = app;

        for (var i = 0; i < Permit; i++)
        {
            using var allowed = await RecalcAsync(client, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        }

        using var blocked = await RecalcAsync(client, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // Інший документ того самого користувача: власний лічильник (404 — не 429).
        using var otherDoc = await RecalcAsync(client, doc.DocumentId + 100_000, doc.PeriodKey.Value).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherDoc.StatusCode);

        // Інший користувач на тому самому документі: власний лічильник.
        var (second, _) = await LoginAsync(app, ["Document.View"], true, doc).ConfigureAwait(true);
        using var otherUser = await RecalcAsync(second, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, otherUser.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відхилені_запити_403_404_422_межу_не_витрачають()
    {
        // 403: без Document.View (право Calculation.Recalculate є, але не те) — тих самих запитів більше за межу.
        var (noRight, doc, appA) = await SignInAsync(["Calculation.Recalculate"], true).ConfigureAwait(true);
        using var a = appA;
        for (var i = 0; i < Permit * 3; i++)
        {
            using var denied = await RecalcAsync(noRight, doc.DocumentId, doc.PeriodKey.Value).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // 404 (немає гранта) і 422 (хибний період) — на документі читача.
        var (reader, doc2, appB) = await SignInAsync(["Document.View"], false).ConfigureAwait(true);
        using var b = appB;
        for (var i = 0; i < Permit * 3; i++)
        {
            using var missing = await RecalcAsync(reader, doc2.DocumentId, doc2.PeriodKey.Value).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        var (reader2, doc3, appC) = await SignInAsync(["Document.View"], true).ConfigureAwait(true);
        using var c = appC;
        for (var i = 0; i < Permit * 3; i++)
        {
            using var invalid = await RecalcAsync(reader2, doc3.DocumentId, 99).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        // Межа читача цілком збережена: рівно Permit успішних, далі 429.
        for (var i = 0; i < Permit; i++)
        {
            using var allowed = await RecalcAsync(reader2, doc3.DocumentId, doc3.PeriodKey.Value).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        }

        using var over = await RecalcAsync(reader2, doc3.DocumentId, doc3.PeriodKey.Value).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Лічильник_рахує_вікно_на_ключ_повертає_дозвіл_і_відкриває_нове_вікно()
    {
        var clock = new ManualClock();
        var quota = new DocumentRecalculateQuota(
            new ConfigurationBuilder().AddInMemoryCollection([new(DocumentRecalculateQuota.PermitKey, "2")]).Build(), clock);

        Assert.True(quota.TryAcquire("a", out var w1, out _));
        Assert.True(quota.TryAcquire("a", out _, out _));
        Assert.False(quota.TryAcquire("a", out _, out var retry));
        Assert.True(retry >= TimeSpan.FromSeconds(1) && retry <= TimeSpan.FromSeconds(60));
        Assert.True(quota.TryAcquire("b", out _, out _));

        quota.Refund("a", w1);
        Assert.True(quota.TryAcquire("a", out _, out _));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True(quota.TryAcquire("a", out _, out _));
        quota.Refund("a", w1); // повернення з минулого вікна нічого не дає
        Assert.True(quota.TryAcquire("a", out _, out _));
        Assert.False(quota.TryAcquire("a", out _, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Повторне_повернення_того_самого_вікна_не_робить_лічильник_від_ємним()
    {
        var quota = new DocumentRecalculateQuota(
            new ConfigurationBuilder().AddInMemoryCollection([new(DocumentRecalculateQuota.PermitKey, "2")]).Build(), new ManualClock());

        Assert.True(quota.TryAcquire("a", out var w, out _));
        quota.Refund("a", w);
        quota.Refund("a", w); // зайве повернення: лічильник уже 0, нижче нуля не йде
        quota.Refund("a", w);

        Assert.True(quota.TryAcquire("a", out _, out _));
        Assert.True(quota.TryAcquire("a", out _, out _));
        Assert.False(quota.TryAcquire("a", out _, out _)); // межа лишилась 2, а не 4
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_ключа_діє_дефолт_6_а_нуль_у_конфігурації_відхиляється_валідацією()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Empty(EcrConfigurationValidation.Validate(empty));
        Assert.Equal(6, DocumentRecalculateQuota.DefaultPermitPerMinute);

        var quota = new DocumentRecalculateQuota(empty);
        for (var i = 0; i < 6; i++)
        {
            Assert.True(quota.TryAcquire("k", out _, out _));
        }

        Assert.False(quota.TryAcquire("k", out _, out _));

        var bad = new ConfigurationBuilder()
            .AddInMemoryCollection([new(DocumentRecalculateQuota.PermitKey, "0")]).Build();
        Assert.Single(EcrConfigurationValidation.Validate(bad));
    }

    private static Task<HttpResponseMessage> RecalcAsync(HttpClient client, long documentId, int periodKey)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative),
            new { periodKey = periodKey.ToString(CultureInfo.InvariantCulture) });

    private async Task<(HttpClient Client, TestDocument Doc, WebApplicationFactory<Program> App)> SignInAsync(
        string[] permissions, bool grantOnProject)
    {
        var app = StartApp();
        var (client, doc) = await LoginAsync(app, permissions, grantOnProject, null).ConfigureAwait(false);
        return (client, doc, app);
    }

    private WebApplicationFactory<Program> StartApp()
    {
        // Черга — заглушка: тест рахує відповіді ендпоінта, а не виконує перерахунок.
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        return new EcrApiFactory(sql).WithWebHostBuilder(b =>
        {
            b.UseSetting("Security:RateLimit:RecalculatePermitPerMinute", Permit.ToString(CultureInfo.InvariantCulture));
            b.ConfigureTestServices(services => services.AddSingleton(jobs));
        });
    }

    private async Task<(HttpClient Client, TestDocument Doc)> LoginAsync(
        WebApplicationFactory<Program> app, string[] permissions, bool grantOnProject, TestDocument? existing)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = existing ?? await builder.BuildAsync().ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            var userName = $"rcq_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"RCQ_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Recalc quota" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (grantOnProject)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            _userName = userName;
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = _userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}");
        return (client, doc);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)by.TotalMilliseconds;
    }
}
