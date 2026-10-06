using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/jobs/summary</c> — лічильники черги для смуги показників
/// екрана «Jobs» (UI-28, LS-F).
/// </summary>
/// <remarks>
/// ⛔ Таблиця <c>itg.JobProgress</c> спільна для колекції <c>SqlServer</c>, тому
/// абсолютні числа стверджуються лише для <c>mine=true</c> свіжого користувача:
/// його задачі — єдині, що він бачить. Для «усіх» перевіряється лише межа
/// доступу й те, що власні рядки входять у загальну суму.
/// </remarks>
[Collection("SqlServer")]
public sealed class JobsSummaryApiTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Password = "Api-Jobs-Summary-2026!";
    private const string ViewHealth = "System.ViewHealth";
    private const string ProbeCode = "Ecr.Tests.Probe.JobsSummary";

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Прибирає власні рядки: активні задачі в спільній базі не мають лишатися після тесту.</summary>
    public async Task DisposeAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await db.JobProgresses.Where(j => j.JobCode == ProbeCode).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Мої_лічильники_рахують_лише_власні_задачі_за_станом_вікном_і_першою_спробою()
    {
        using var app = new EcrApiFactory(sql);
        var (client, me) = await SignedInAsync(app).ConfigureAwait(true);
        using var _ = client;
        var (otherClient, other) = await SignedInAsync(app).ConfigureAwait(true);
        otherClient.Dispose();

        var now = DateTime.UtcNow;
        var recent = now.AddHours(-2);

        // Власні: 1 у черзі, 2 виконуються (затримка 1 с і 3 с), 1 успіх (2 с),
        // 2 провали за добу (перша спроба — 2 с; друга спроба — 100 с, у середню
        // НЕ входить), 1 провал дводенної давності — поза вікном.
        await AddJobAsync(me, "Queued", recent, 0, attempt: null).ConfigureAwait(true);
        await AddJobAsync(me, "Running", recent, 1000, attempt: 1).ConfigureAwait(true);
        await AddJobAsync(me, "Running", recent, 3000, attempt: 1).ConfigureAwait(true);
        await AddJobAsync(me, "Succeeded", recent, 2000, attempt: 1).ConfigureAwait(true);
        await AddJobAsync(me, "Failed", recent, 2000, attempt: 1).ConfigureAwait(true);
        await AddJobAsync(me, "Failed", recent, 100_000, attempt: 2).ConfigureAwait(true);
        await AddJobAsync(me, "Failed", now.AddDays(-2), 50_000, attempt: 1).ConfigureAwait(true);

        // Чужі задачі в `mine` не потрапляють.
        await AddJobAsync(other, "Running", recent, 500, attempt: 1).ConfigureAwait(true);
        await AddJobAsync(other, "Failed", recent, 500, attempt: 1).ConfigureAwait(true);

        var summary = await GetAsync(client, "/api/v1/jobs/summary?mine=true").ConfigureAwait(true);

        Assert.Equal(2, summary.GetProperty("running").GetInt32());
        Assert.Equal(1, summary.GetProperty("queued").GetInt32());
        Assert.Equal(2, summary.GetProperty("failed24h").GetInt32());
        Assert.Equal(1, summary.GetProperty("succeeded24h").GetInt32());

        // (1000 + 3000 + 2000 + 2000) / 4 — друга спроба й дводенний провал не входять.
        Assert.Equal(2000L, summary.GetProperty("avgStartLatencyMs").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_mine_і_без_права_відмова_а_не_нулі_і_власні_входять_у_загальну_суму()
    {
        using var app = new EcrApiFactory(sql);
        var (stranger, strangerId) = await SignedInAsync(app).ConfigureAwait(true);
        using var _s = stranger;

        // ⛔ 403, а не нулі: «задач немає» і «вам їх не показують» — різні відповіді.
        var denied = await stranger
            .GetAsync(new Uri("/api/v1/jobs/summary", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains(
            ViewHealth,
            await denied.Content.ReadAsStringAsync().ConfigureAwait(true),
            StringComparison.Ordinal);

        // Друга половина доказу: `mine=true` без права — 200.
        var own = await stranger
            .GetAsync(new Uri("/api/v1/jobs/summary?mine=true", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(own.IsSuccessStatusCode, $"{own.StatusCode}: {app.ErrorsText}");

        // Адміністратор: до і після власної задачі в черзі загальна черга зростає рівно на неї.
        var (admin, _) = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);
        using var _a = admin;

        var before = await GetAsync(admin, "/api/v1/jobs/summary").ConfigureAwait(true);
        await AddJobAsync(strangerId, "Queued", DateTime.UtcNow.AddMinutes(-1), 0, attempt: null)
            .ConfigureAwait(true);
        var after = await GetAsync(admin, "/api/v1/jobs/summary").ConfigureAwait(true);

        Assert.Equal(
            before.GetProperty("queued").GetInt32() + 1,
            after.GetProperty("queued").GetInt32());
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>
    /// Рядок прогресу в потрібному стані. <paramref name="latencyMs"/> — різниця між
    /// постановкою (<paramref name="at"/>) і стартом.
    /// </summary>
    private async Task AddJobAsync(int userId, string state, DateTime at, int latencyMs, int? attempt)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var job = new JobProgress($"jsum-{Guid.NewGuid():N}", ProbeCode, at, userId);
        job.Queue(at);

        if (attempt is { } n)
        {
            var started = at.AddMilliseconds(latencyMs);
            job.Begin(started, n);

            if (state is "Succeeded" or "Failed")
            {
                // Завершення — на той самий момент, що й оновлення вікна «за добу».
                job.Finish(state, state == "Failed" ? "probe" : null, started, state == "Failed" ? "ECR-INT-0500" : null);
            }
        }

        db.JobProgresses.Add(job);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<(HttpClient Client, int UserId)> SignedInAsync(
        EcrApiFactory app, params string[] permissions)
    {
        var name = $"jsum_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            userId = user.Id;

            if (permissions.Length > 0)
            {
                var role = new Role(
                    Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new Ecr.Domain.ValueObjects.LocalizedText(
                        new Dictionary<string, string> { ["en"] = "Jobs summary test" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return (client, userId);
    }
}
