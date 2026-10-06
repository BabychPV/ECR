// tests/Ecr.Api.Tests/Health/HealthReadyPublicDescriptionTests.cs
using System.Text.Json;
using Ecr.Api.Health;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// A2-11: анонімний <c>/health/ready</c> називає причину жовтого/червоного <c>db</c>
/// короткою нейтральною фразою — і не віддає нічого чутливого: ані опису перевірки,
/// ані подробиць, ані тексту винятку (рядок підключення, ім'я сервера, шлях).
/// </summary>
/// <remarks>
/// Предмет — писар звіту (<see cref="HealthResponse"/>), тому без БД і без хоста.
/// Що <see cref="DatabaseHealthCheck"/> справді кладе код причини — інтеграційні
/// <see cref="DatabaseHealthUnprotectedKeysTests"/> і <see cref="DatabaseHealthRcsiOffTests"/>.
/// </remarks>
public sealed class HealthReadyPublicDescriptionTests
{
    /// <summary>Фрагменти, яких в анонімному звіті не має бути НІКОЛИ.</summary>
    private static readonly string[] Secrets =
    [
        "sql-prod-01",
        "Password=",
        "s3cr3t-P@ss",
        "User ID=",
        "Server=",
        @"C:\Program Files\ECR",
        "sec.DataProtectionKey",
        "Auth:DataProtection",
        "0123456789ABCDEF0123456789ABCDEF01234567",
        "Sql/06-rcsi.sql",
        PublicHealthReason.DataKey,
    ];

    private const string SecretDescription =
        "Session keys are stored unencrypted in sec.DataProtectionKey: no certificate is configured "
        + "(Auth:DataProtection:CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567).";

    private const string ConnectionText =
        @"A network-related error occurred while connecting to Server=sql-prod-01;User ID=ecr;Password=s3cr3t-P@ss "
        + @"(log C:\Program Files\ECR\logs\ecr.log)";

    /// <remarks>
    /// ⛔ Головний випадок. Мутація (прогнано локально): повернути в писарі
    /// <c>null</c> для <c>db</c> замість <see cref="PublicHealthReason.Describe"/> →
    /// червоний (опис null); віддати <c>e.Value.Description</c> → червоний (витік).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Degraded_db_має_нейтральну_причину_без_подробиць()
    {
        var entry = Entry(
            HealthStatus.Degraded,
            SecretDescription,
            data: Data(PublicHealthReason.SessionKeysUnprotected));

        var (body, check) = await ReadyAsync(("db", entry));

        Assert.Equal("Degraded", check.GetProperty("status").GetString());
        Assert.Equal("session keys not protected", check.GetProperty("description").GetString());
        Assert.Empty(check.GetProperty("data").EnumerateObject());
        AssertNoSecrets(body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Unhealthy_db_з_винятком_не_віддає_тексту_винятку()
    {
        var entry = Entry(
            HealthStatus.Unhealthy,
            "Database is unavailable.",
            new InvalidOperationException(ConnectionText, new IOException(ConnectionText)),
            Data(PublicHealthReason.DatabaseUnavailable));

        var (body, check) = await ReadyAsync(("db", entry));

        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.Equal("database unavailable", check.GetProperty("description").GetString());
        AssertNoSecrets(body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Невідомий_код_причини_не_повторюється_дослівно()
    {
        // Код — теж рядок із перевірки; у звіт іде лише фраза з білого списку.
        var entry = Entry(HealthStatus.Degraded, SecretDescription, data: Data(ConnectionText));

        var (body, check) = await ReadyAsync(("db", entry));

        Assert.Equal(PublicHealthReason.Generic, check.GetProperty("description").GetString());
        AssertNoSecrets(body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Degraded_db_без_коду_дає_загальну_фразу()
    {
        var (body, check) = await ReadyAsync(("db", Entry(HealthStatus.Degraded, SecretDescription)));

        Assert.Equal(PublicHealthReason.Generic, check.GetProperty("description").GetString());
        AssertNoSecrets(body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Healthy_db_як_і_раніше_без_опису()
    {
        var (_, check) = await ReadyAsync(("db", Entry(HealthStatus.Healthy, "Database is available.")));

        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("description").ValueKind);
    }

    /// <remarks>
    /// ⛔ Справжній <see cref="HealthCheckService"/>, а не підроблений запис: коли
    /// перевірка кидає виняток сама, фреймворк кладе в опис <c>ex.Message</c>.
    /// Мутація (прогнано локально): прибрати заміну тексту винятку в писарі → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Неперехоплений_виняток_будь_якої_перевірки_не_витікає_в_ready()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .AddCheck("jobs", new ThrowingCheck(), tags: ["ready"]);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Contains("sql-prod-01", report.Entries["jobs"].Description, StringComparison.Ordinal);

        var (body, check) = await WriteAsync(HealthResponse.WriteReadyAsync, report, "jobs");

        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.Equal(PublicHealthReason.CheckFailed, check.GetProperty("description").GetString());
        AssertNoSecrets(body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Свідомий_опис_поруч_із_винятком_лишається()
    {
        // Degraded(текст, ex): текст із каталогу не містить повідомлення винятку — моніторингу він потрібен.
        var entry = Entry(
            HealthStatus.Degraded,
            "Time zone database is unavailable.",
            new TimeZoneNotFoundException(ConnectionText));

        var (body, check) = await ReadyAsync(("tzdata", entry));

        Assert.Equal("Time zone database is unavailable.", check.GetProperty("description").GetString());
        AssertNoSecrets(body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Повний_звіт_db_зберігає_опис_і_не_показує_службовий_ключ()
    {
        var entry = Entry(
            HealthStatus.Degraded,
            SecretDescription,
            data: Data(PublicHealthReason.SessionKeysUnprotected));

        var (body, check) = await WriteAsync(HealthResponse.WriteAsync, Report(("db", entry)), "db");

        Assert.Equal(SecretDescription, check.GetProperty("description").GetString());
        Assert.Equal("v", check.GetProperty("data").GetProperty("edition").GetString());
        Assert.DoesNotContain(PublicHealthReason.DataKey, body, StringComparison.Ordinal);
    }

    /// <summary>Шукає по РОЗКОДОВАНИХ рядках і іменах полів: JSON екранує <c>\</c> і <c>+</c>, сирий текст збрехав би.</summary>
    private static void AssertNoSecrets(string body)
    {
        var text = string.Join('\n', Strings(JsonDocument.Parse(body).RootElement));
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var inner in Strings(property.Value))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in Strings(item))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
        }
    }

    private static HealthReportEntry Entry(
        HealthStatus status, string description, Exception? exception = null, Dictionary<string, object>? data = null)
        => new(status, description, TimeSpan.FromMilliseconds(1), exception, data);

    private static Dictionary<string, object> Data(string reason)
        => new(StringComparer.Ordinal)
        {
            ["edition"] = "v",
            ["filegroups"] = new[] { "DATA_HOT" },
            [PublicHealthReason.DataKey] = reason,
        };

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries)
        => new(entries.ToDictionary(e => e.Name, e => e.Entry, StringComparer.Ordinal), TimeSpan.FromMilliseconds(1));

    private static Task<(string Body, JsonElement Check)> ReadyAsync((string Name, HealthReportEntry Entry) entry)
        => WriteAsync(HealthResponse.WriteReadyAsync, Report(entry), entry.Name);

    private static async Task<(string Body, JsonElement Check)> WriteAsync(
        Func<HttpContext, HealthReport, Task> writer, HealthReport report, string name)
    {
        var context = new DefaultHttpContext();
        using var stream = new MemoryStream();
        context.Response.Body = stream;

        await writer(context, report);

        var body = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        var check = JsonDocument.Parse(body).RootElement.GetProperty("checks")
            .EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("name").GetString(), name, StringComparison.Ordinal))
            .Clone();
        return (body, check);
    }

    private sealed class ThrowingCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(ConnectionText);
    }
}
