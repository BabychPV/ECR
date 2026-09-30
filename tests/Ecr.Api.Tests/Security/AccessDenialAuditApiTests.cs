using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.AspNetCore.TestHost;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// ФВ-5.24: відмова в доступі (<c>403</c>) лишає слід у <c>aud.SecurityEvent</c>.
/// </summary>
/// <remarks>
/// Доти <c>ExceptionHandlingMiddleware</c> просто повертав <c>403</c>, і жодна відмова
/// (з причиною й ресурсом) ніде не фіксувалась. Тут — справжній HTTP до справжньої бази.
/// </remarks>
[Collection("SqlServer")]
public sealed class AccessDenialAuditApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Denial-Audit-2026!";
    private const string EventType = "AccessDenied";

    private static readonly Uri Facts = new("/api/v1/health/facts", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Відмова_403_лишає_рівно_одну_подію_з_очікуваними_полями_без_PII()
    {
        using var app = new EcrApiFactory(sql);
        var (client, userId, userName) = await SignInAsync(app).ConfigureAwait(true);
        using var _ = client;

        var before = DateTime.UtcNow.AddSeconds(-2);

        // Query містить «персональні» значення: жодне не має потрапити в журнал.
        var denied = await client.GetAsync(
            new Uri("/api/v1/health/facts?email=leak%40example.com&q=SecretQuery", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        var rows = await EventsAsync(userId).ConfigureAwait(true);
        var row = Assert.Single(rows);

        Assert.True(row.ChangedAt >= before && row.ChangedAt <= DateTime.UtcNow.AddSeconds(2), $"ChangedAt={row.ChangedAt:O}");
        Assert.Equal(problem.GetProperty("correlationId").GetString(), row.CorrelationId);

        var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
        Assert.Equal(problem.GetProperty("errorCode").GetString(), details.GetProperty("code").GetString());
        Assert.Equal("System.ViewHealth", details.GetProperty("permission").GetString());
        Assert.Equal("GET", details.GetProperty("method").GetString());
        Assert.Equal("api/v1/health/facts", details.GetProperty("route").GetString());

        // ⛔ Без PII: ні query, ні імені користувача, ні тексту запиту.
        foreach (var forbidden in new[] { "leak", "example.com", "SecretQuery", userName, "?" })
        {
            Assert.DoesNotContain(forbidden, row.DetailsJson!, StringComparison.Ordinal);
        }

        // Той самий користувач і маршрут ще тричі — обмежувач лишає один рядок.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Facts).ConfigureAwait(true)).StatusCode);
        }

        Assert.Single(await EventsAsync(userId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Ресурс_із_маршруту_журналюється_лише_числом()
    {
        using var app = new EcrApiFactory(sql);
        var (client, userId, _) = await SignInAsync(app, "System.ViewHealth").ConfigureAwait(true);
        using var _ = client;

        // Є System.ViewHealth, але немає Integration.View → 403 на конкретний запуск збору.
        var denied = await client.GetAsync(new Uri("/api/v1/collection-runs/7", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var row = Assert.Single(await EventsAsync(userId).ConfigureAwait(true));
        var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
        Assert.Equal("api/v1/collection-runs/{id:long}", details.GetProperty("route").GetString());
        Assert.Equal("id", details.GetProperty("resourceType").GetString());
        Assert.Equal(7, details.GetProperty("resourceId").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Анонімний_401_у_журнал_не_потрапляє()
    {
        using var app = new EcrApiFactory(sql);
        using var anonymous = app.CreateClient();

        var response = await anonymous.GetAsync(Facts).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Анонімна відмова — це 401 від автентифікації, без тіла: ідентифікатор кореляції
        // береться із заголовка, і за ним у журналі не має бути нічого.
        var correlation = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));

        Assert.Equal(0, await CountByCorrelationAsync(correlation).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Збій_запису_журналу_не_змінює_відповідь_403()
    {
        using var app = new EcrApiFactory(sql);
        using var failing = app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddScoped<IAuditWriter>(sp =>
                new DenialFailingAuditWriter(
                    ActivatorUtilities.CreateInstance<AuditWriter>(sp))))
        );

        var (client, userId, _) = await SignInAsync(failing).ConfigureAwait(true);
        using var _ = client;

        // Знімок ДО запиту: старт хоста міг залогувати Error від чужого стану спільної бази
        // (напр. RecurringScheduleService із розкладом з невалідним cron). Це не наш збій.
        var errorsBefore = app.ServerErrors.Count;

        var denied = await client.GetAsync(Facts).ConfigureAwait(true);

        // Статус і тіло — ті самі, що без збою: 403 з кодом і причиною, не 500.
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.StartsWith("ECR-", problem.GetProperty("errorCode").GetString(), StringComparison.Ordinal);
        Assert.Equal(403, problem.GetProperty("status").GetInt32());

        // Збій — лише в лозі, рівнем Warning; помилок сервера немає.
        Assert.Contains(app.ServerLog, line => line.Contains("Відмову в доступі не записано", StringComparison.Ordinal));
        // Помилок сервера НА ЦЕЙ запит немає (записи, що були до нього, — чужі й не рахуються).
        var newErrors = app.ServerErrors.Skip(errorsBefore).ToList();
        Assert.True(newErrors.Count == 0, string.Join("\n", newErrors));

        Assert.Empty(await EventsAsync(userId).ConfigureAwait(true));
    }

    /// <summary>Справжній запис, окрім події-відмови: вхід і решта журналу працюють.</summary>
    private sealed class DenialFailingAuditWriter(IAuditWriter inner) : IAuditWriter
    {
        public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct)
            => inner.WriteCellChangesAsync(changes, ct);

        public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
            => inner.WriteStructureChangeAsync(change, ct);

        public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
            => inner.WriteSecurityEventAsync(evt, ct);

        public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct)
            => inner.WriteSecurityEventsAsync(events, ct);

        public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
            => evt.EventType == EventType
                ? throw new InvalidOperationException("журнал недоступний (тест)")
                : inner.WriteIndependentSecurityEventAsync(evt, ct);

        public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
            => inner.WritePublicationEventAsync(evt, ct);
    }

    private sealed record EventRow(DateTime ChangedAt, string? DetailsJson, string? CorrelationId);

    private async Task<List<EventRow>> EventsAsync(int userId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ChangedAt, DetailsJson, CorrelationId FROM aud.SecurityEvent "
            + "WHERE EventType = @t AND ChangedByUserId = @u ORDER BY Id;";
        command.Parameters.AddWithValue("@t", EventType);
        command.Parameters.AddWithValue("@u", userId);

        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new EventRow(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private async Task<int> CountByCorrelationAsync(string correlationId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = @t AND CorrelationId = @c;";
        command.Parameters.AddWithValue("@t", EventType);
        command.Parameters.AddWithValue("@c", correlationId);
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    /// <summary>Локальний користувач із названими правами, вхід; повертає й Id для запиту в журнал.</summary>
    private async Task<(HttpClient Client, int UserId, string UserName)> SignInAsync(
        WebApplicationFactory<Program> app, params string[] permissions)
    {
        var name = $"deny_{Guid.NewGuid():N}"[..20];
        int userId;

        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using (var db = new EcrDbContext(options))
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
                        new Dictionary<string, string> { ["en"] = "Denial audit test" }));
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
        Assert.True(login.IsSuccessStatusCode, $"Вхід {name}: {login.StatusCode}");

        return (client, userId, name);
    }
}
