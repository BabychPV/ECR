using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Health;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/health/facts</c> і <c>GET /api/v1/health/partitions/script</c>
/// (<c>BE-18</c>).
/// </summary>
[Collection("SqlServer")]
public sealed class SystemHealthControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Health-Facts-2026!";
    private const string ViewHealth = "System.ViewHealth";

    private static readonly Uri Facts = new("/api/v1/health/facts", UriKind.Relative);
    private static readonly Uri Script = new("/api/v1/health/partitions/script", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Анонім_і_користувач_без_права_не_отримують_ні_фактів_ні_команди()
    {
        using var app = new EcrApiFactory(sql);

        foreach (var address in new[] { Facts, Script })
        {
            using var anonymous = app.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(address)).StatusCode);

            using var stranger = await SignedInAsync(app);
            var denied = await stranger.GetAsync(address);

            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            // ⚠ Відмова називає право — і НЕ несе того, заради чого прийшли.
            var body = await denied.Content.ReadAsStringAsync();
            Assert.Contains(ViewHealth, body, StringComparison.Ordinal);
            Assert.DoesNotContain("productVersion", body, StringComparison.Ordinal);
            Assert.DoesNotContain("usp_EnsurePartitions", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Факти_описують_цей_процес_а_транспорт_без_налаштування_не_названо_робочим()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth);

        var response = await client.GetAsync(Facts);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // Те саме обрізання, що й в анонімному bootstrap: без хеша коміту.
        Assert.Matches(@"^\d+(\.\d+){0,3}$", root.GetProperty("productVersion").GetString());

        // ⚠ UTC і в минулому: локальний час без позначки клієнт зсунув би на
        // пояс браузера, і «працює 3 години» стало б «працює −2 години».
        var startedAt = root.GetProperty("startedAt").GetString() ?? string.Empty;
        Assert.EndsWith("Z", startedAt, StringComparison.Ordinal);
        Assert.True(root.GetProperty("startedAt").GetDateTime() <= DateTime.UtcNow);

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("environment").GetString()));

        // ⛔ Фікстура не задає `Smtp:Host`, тож транспорту НЕМАЄ — і відповідь
        // мусить це визнати, а не показати заспокійливе «Smtp».
        var transport = root.GetProperty("notificationTransport");
        Assert.False(transport.GetProperty("isConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, transport.GetProperty("kind").ValueKind);

        // ⚠ Фабрика прибирає всіх постачальників журналу, файлового теж — і
        // відповідь не називає теку, в яку ніхто не пише. Активний приймач —
        // у `FileLogTests`.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("logDirectory").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Вид_транспорту_називається_лише_коли_він_налаштований()
    {
        Assert.Equal(
            new NotificationTransportDto(true, GetSystemFactsHandler.SmtpKind),
            GetSystemFactsHandler.TransportOf(isConfigured: true));

        Assert.Equal(
            new NotificationTransportDto(false, null),
            GetSystemFactsHandler.TransportOf(isConfigured: false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Команда_для_DBA_віддається_простим_текстом()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth);

        var response = await client.GetAsync(Script);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains(GetPartitionScriptHandler.Command, text, StringComparison.Ordinal);

        // ⛔ Кожен рядок — або коментар T-SQL, або сама команда: текст іде в
        // буфер обміну й далі в SSMS як є, і речення поза коментарем зламало б
        // виконання.
        Assert.All(
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(
                line.StartsWith("--", StringComparison.Ordinal) || line == GetPartitionScriptHandler.Command,
                $"Рядок не є ні коментарем, ні командою: «{line}»"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Команда_збігається_з_кроком_завдання_SQL_Agent()
    {
        // ⛔ Команда не вигадана: це той самий виклик, яким межі рухає
        // розгортання. Зміниться крок завдання (інша процедура, інший запас) —
        // текст для DBA мусить змінитися разом із ним, а не тихо застаріти.
        var agentJobs = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "14-agent-jobs.sql"));

        Assert.Contains(
            $"@command = N'{GetPartitionScriptHandler.Command}'", agentJobs, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Ecr.sln не знайдено від {AppContext.BaseDirectory}.");
    }

    /// <summary>Клієнт із сеансом локального користувача з названими правами.</summary>
    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SignedInAsync(sql, app, permissions);

    /// <summary>Те саме для похідного хоста (<c>WithWebHostBuilder</c>) — потрібне <c>FileLogTests</c>.</summary>
    internal static async Task<HttpClient> SignedInAsync(
        SqlServerFixture sql, WebApplicationFactory<Program> app, params string[] permissions)
    {
        var name = $"hlth_{Guid.NewGuid():N}"[..20];

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

        await using (var db = new EcrDbContext(options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            if (permissions.Length > 0)
            {
                var role = new Role(
                    Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new Ecr.Domain.ValueObjects.LocalizedText(
                        new Dictionary<string, string> { ["en"] = "Health facts test" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync();

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync();
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {(app as EcrApiFactory)?.ErrorsText}");

        return client;
    }
}
