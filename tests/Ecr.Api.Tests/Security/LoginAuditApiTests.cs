using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PSS-24: успішні входи, невдалі спроби й блокування — у <c>aud.SecurityEvent</c> (незмінний журнал безпеки), з правильною
/// адресою клієнта, без пароля й без PII понад ім'я входу.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати виклик журналу з <c>LoginHandler.FailAsync</c> — червоніють тести невдалих спроб і
/// блокування; прибрати його після успіху — червоніє тест успіху; повернути <c>Connection.RemoteIpAddress</c> в
/// <c>AuthController.RemoteIp</c> — червоніє тест адреси за <c>X-Forwarded-For</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class LoginAuditApiTests(SqlServerFixture sql)
{
    private const string Password = "Login-Audit-2026-Winter!";
    private const string WrongPassword = "ПАРОЛЬ-який-не-має-потрапити-в-журнал-77!";
    private static readonly Uri LoginUri = new("/api/v1/login/local", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Три_невдалі_входи_дають_три_LoginFailed_з_адресою_і_без_пароля()
    {
        var (name, userId) = await ArrangeUserAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.11");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, name, WrongPassword).ConfigureAwait(true)).StatusCode);
        }

        var rows = await EventsAsync(LoginAuditEventTypes.Failed, name).ConfigureAwait(true);
        Assert.Equal(3, rows.Count);

        foreach (var row in rows)
        {
            Assert.Equal(userId, row.ChangedByUserId);
            Assert.Equal(userId, row.TargetUserId);
            Assert.False(string.IsNullOrEmpty(row.CorrelationId));

            var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
            Assert.Equal(name, details.GetProperty("userName").GetString());
            Assert.Equal("Local", details.GetProperty("provider").GetString());
            Assert.Equal("Failed", details.GetProperty("outcome").GetString());
            Assert.Equal("BadPassword", details.GetProperty("reason").GetString());
            Assert.Equal("198.51.100.11", details.GetProperty("ip").GetString());

            // ⛔ Ні пароля, ні його фрагмента.
            Assert.DoesNotContain(WrongPassword, row.DetailsJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("пароль", row.DetailsJson!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Невідоме_ім_я_пишеться_з_анонімним_автором_і_іменем_у_подробицях()
    {
        var name = $"nobody_{Guid.NewGuid():N}"[..20];
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.12");

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, name, WrongPassword).ConfigureAwait(true)).StatusCode);

        var row = Assert.Single(await EventsAsync(LoginAuditEventTypes.Failed, name).ConfigureAwait(true));
        Assert.Equal(LoginAuditEventTypes.AnonymousActorId, row.ChangedByUserId);
        Assert.Null(row.TargetUserId);

        var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
        Assert.Equal("UnknownUser", details.GetProperty("reason").GetString());
        Assert.Equal("198.51.100.12", details.GetProperty("ip").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Успішний_вхід_пишеться_як_LoginSucceeded_без_причини_і_без_пароля()
    {
        var (name, userId) = await ArrangeUserAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.13");

        var login = await LoginAsync(client, name, Password).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}; {app.ErrorsText}");

        var row = Assert.Single(await EventsAsync(LoginAuditEventTypes.Succeeded, name).ConfigureAwait(true));
        Assert.Equal(userId, row.ChangedByUserId);

        var details = JsonDocument.Parse(row.DetailsJson!).RootElement;
        Assert.Equal("Succeeded", details.GetProperty("outcome").GetString());
        Assert.Equal("198.51.100.13", details.GetProperty("ip").GetString());
        Assert.False(details.TryGetProperty("reason", out _));
        Assert.DoesNotContain(Password, row.DetailsJson!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Блокування_запису_пишеться_одним_AccountLocked_на_спробі_що_вичерпала_бюджет()
    {
        var (name, userId) = await ArrangeUserAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var host = WithRemoteIp(app);
        using var client = Client(host, "198.51.100.14");

        // Політика за замовчуванням: 5 невдалих спроб → блокування; шоста вже на заблокованому записі.
        for (var i = 0; i < 6; i++)
        {
            await LoginAsync(client, name, WrongPassword).ConfigureAwait(true);
        }

        var locked = Assert.Single(await EventsAsync(LoginAuditEventTypes.AccountLocked, name).ConfigureAwait(true));
        Assert.Equal(userId, locked.ChangedByUserId);

        var details = JsonDocument.Parse(locked.DetailsJson!).RootElement;
        Assert.Equal("Locked", details.GetProperty("outcome").GetString());
        Assert.Equal("198.51.100.14", details.GetProperty("ip").GetString());

        // Кожна з шести спроб — теж LoginFailed (блокувальна й та, що на вже заблокованому записі).
        var failed = await EventsAsync(LoginAuditEventTypes.Failed, name).ConfigureAwait(true);
        Assert.Equal(6, failed.Count);
        Assert.Contains(failed, r => r.DetailsJson!.Contains("\"LockedOut\"", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Адреса_в_журналі_входів_береться_з_того_самого_визначника_що_й_межа_частоти()
    {
        var (name, _) = await ArrangeUserAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var host = app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Security:RateLimit:TrustForwardedFor", "true");
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
        });
        using var client = Client(host, "10.0.0.5");

        // Проксі (сокет 10.0.0.5) дописує справжнього клієнта праворуч.
        var request = new HttpRequestMessage(HttpMethod.Post, LoginUri)
        {
            Content = JsonContent.Create(new { userName = name, password = WrongPassword }),
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.77");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request).ConfigureAwait(true)).StatusCode);

        var row = Assert.Single(await EventsAsync(LoginAuditEventTypes.Failed, name).ConfigureAwait(true));
        Assert.Equal("203.0.113.77", JsonDocument.Parse(row.DetailsJson!).RootElement.GetProperty("ip").GetString());

        // ⛔ Мутаційний доказ: повернути `Connection.RemoteIpAddress` в `AuthController.RemoteIp` — тут буде 10.0.0.5, і
        // так само в `sec.LoginAttempt`: обидва журнали тепер мають одну й ту саму адресу.
        await using var db = NewDb();
        var attempt = await db.LoginAttempts.SingleAsync(a => a.UserName == name).ConfigureAwait(true);
        Assert.Equal("203.0.113.77", attempt.IpAddress);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Збій_запису_журналу_не_змінює_відповідь_входу()
    {
        var (name, _) = await ArrangeUserAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var failing = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IAuditWriter>(sp =>
                new LoginFailingAuditWriter(ActivatorUtilities.CreateInstance<AuditWriter>(sp)))));
        using var client = failing.CreateClient();

        var wrong = await LoginAsync(client, name, WrongPassword).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = await LoginAsync(client, name, Password).ConfigureAwait(true);
        Assert.True(right.IsSuccessStatusCode, $"{right.StatusCode}; {app.ErrorsText}");

        Assert.Empty(await EventsAsync(LoginAuditEventTypes.Failed, name).ConfigureAwait(true));
        Assert.Empty(await EventsAsync(LoginAuditEventTypes.Succeeded, name).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PSS-24")]
    public async Task Події_входу_незмінні_UPDATE_і_DELETE_відхиляються_тригером()
    {
        var (name, _) = await ArrangeUserAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        await LoginAsync(client, name, WrongPassword).ConfigureAwait(true);

        var row = Assert.Single(await EventsAsync(LoginAuditEventTypes.Failed, name).ConfigureAwait(true));

        foreach (var statement in new[]
                 {
                     $"UPDATE aud.SecurityEvent SET DetailsJson = N'{{}}' WHERE Id = {row.Id}",
                     $"DELETE FROM aud.SecurityEvent WHERE Id = {row.Id}",
                 })
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync()).ConfigureAwait(true);
            Assert.Equal(50060, error.Number);
        }
    }

    /// <summary>Справжній запис, окрім подій входу.</summary>
    private sealed class LoginFailingAuditWriter(IAuditWriter inner) : IAuditWriter
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
            => evt.EventType is LoginAuditEventTypes.Succeeded or LoginAuditEventTypes.Failed
                or LoginAuditEventTypes.AccountLocked
                ? throw new InvalidOperationException("журнал недоступний (тест)")
                : inner.WriteIndependentSecurityEventAsync(evt, ct);

        public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
            => inner.WritePublicationEventAsync(evt, ct);
    }

    private sealed record EventRow(
        long Id, int ChangedByUserId, int? TargetUserId, string? DetailsJson, string? CorrelationId);

    /// <summary>Події входу цього імені (ім'я — у подробицях, тож фільтр за ним, а не за автором).</summary>
    private async Task<List<EventRow>> EventsAsync(string eventType, string userName)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ChangedByUserId, TargetUserId, DetailsJson, CorrelationId FROM aud.SecurityEvent "
            + "WHERE EventType = @t AND JSON_VALUE(DetailsJson, '$.userName') = @n ORDER BY Id;";
        command.Parameters.AddWithValue("@t", eventType);
        command.Parameters.AddWithValue("@n", userName);

        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new EventRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static WebApplicationFactory<Program> WithRemoteIp(EcrApiFactory app)
        => app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>()));

    private static HttpClient Client(WebApplicationFactory<Program> host, string remoteIp)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        return client;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password)
        => await client.PostAsJsonAsync(LoginUri, new { userName, password }).ConfigureAwait(false);

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<(string Name, int UserId)> ArrangeUserAsync()
    {
        var name = $"laud_{Guid.NewGuid():N}"[..20];

        await using var db = NewDb();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (name, user.Id);
    }
}
