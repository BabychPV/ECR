using System.Net;
using System.Text.Json;
using Ecr.Application.Health;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Розширені факти <c>GET /api/v1/health/facts</c>: <c>jobs</c>, <c>sources</c>, <c>db</c>, <c>about</c>
/// (UI-39). Головне — БЕЗПЕКА: лише числа й мітки часу, жодних шляхів та імен.
/// </summary>
[Collection("SqlServer")]
public sealed class SystemHealthFactsTests(SqlServerFixture sql)
{
    private const string ViewHealth = "System.ViewHealth";

    private static readonly Uri Facts = new("/api/v1/health/facts", UriKind.Relative);
    private static readonly string[] SignInModes = ["Windows", "Local", "WindowsAndLocal", "None"];
    private static readonly string[] HiddenFields = ["freeSpaceDataDiskGb", "lastBackupAt", "signInMode", "failed24h"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Факти_віддають_jobs_sources_db_about_лише_числами_і_мітками()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, ViewHealth);

        var root = await ReadFactsAsync(client, app);

        var jobs = root.GetProperty("jobs");
        Assert.True(jobs.GetProperty("running").GetInt32() >= 0);
        Assert.True(jobs.GetProperty("queued").GetInt32() >= 0);
        Assert.True(jobs.GetProperty("failed24h").GetInt32() >= 0);

        var sources = root.GetProperty("sources");
        Assert.True(sources.GetProperty("active").GetInt32() >= 0);
        Assert.True(sources.GetProperty("failed").GetInt32() >= 0);
        Assert.True(sources.GetProperty("gaps").GetInt32() >= 0);

        // ⚠ Число ≥ 0 АБО null (СУБД без права на dm_os_volume_stats) — але не рядок із шляхом.
        var db = root.GetProperty("db");
        var free = db.GetProperty("freeSpaceDataDiskGb");
        Assert.True(
            free.ValueKind == JsonValueKind.Null || (free.ValueKind == JsonValueKind.Number && free.GetInt64() >= 0),
            $"freeSpaceDataDiskGb: {free}");
        AssertUtcOrNull(db.GetProperty("lastBackupAt"));

        // Вікна обслуговування в моделі налаштувань немає — null, не вигадка.
        Assert.Equal(JsonValueKind.Null, db.GetProperty("maintenanceWindow").ValueKind);

        var about = root.GetProperty("about");
        Assert.Matches(@"^\d+(\.\d+){0,3}$", about.GetProperty("build").GetString());
        Assert.Equal(root.GetProperty("productVersion").GetString(), about.GetProperty("build").GetString());
        Assert.Contains(about.GetProperty("signInMode").GetString(), SignInModes);
        Assert.Contains("en", about.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        AssertUtcOrNull(about.GetProperty("lastErrorAt"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_нові_поля_не_віддаються()
    {
        using var app = new EcrApiFactory(sql);
        using var stranger = await SystemHealthControllerTests.SignedInAsync(sql, app);

        var denied = await stranger.GetAsync(Facts);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var body = await denied.Content.ReadAsStringAsync();
        foreach (var field in HiddenFields)
        {
            Assert.DoesNotContain(field, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відповідь_не_містить_шляхів_імен_сервера_машини_чи_бази()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, ViewHealth);

        var root = await ReadFactsAsync(client, app);

        string databaseName;
        await using (var db = sql.CreateContext())
        {
            databaseName = db.Database.GetDbConnection().Database;
        }

        var forbidden = new List<string> { "\\", "Server=", "Data Source", "Integrated Security", "Password=" };
        forbidden.Add(Environment.MachineName);
        forbidden.Add(databaseName);

        // Дивимось на РОЗІБРАНІ рядкові значення, а не на сирий текст: у ньому `\u…`-екрани
        // дали б хибний збіг на зворотну скісну.
        foreach (var value in Strings(root))
        {
            foreach (var needle in forbidden.Where(n => n.Length > 0))
            {
                Assert.False(
                    value.Contains(needle, StringComparison.OrdinalIgnoreCase),
                    $"У відповіді знайдено заборонене «{needle}»: {value}");
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічильники_ростуть_рівно_на_заведені_дані()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, ViewHealth);

        var before = await ReadFactsAsync(client, app);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow;
        int dataSourceId;
        int sourceEntityId;
        string runningJobId = $"hf-run-{tag}";
        string failedJobId = $"hf-fail-{tag}";
        string oldFailedJobId = $"hf-old-{tag}";

        await using (var db = sql.CreateContext())
        {
            var dataSource = new DataSource(
                EcrCode.Create($"Hf{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Health facts" }),
                ExternalTransport.PiWebApi,
                "https://example.test",
                "secret");
            db.DataSources.Add(dataSource);
            await db.SaveChangesAsync();
            dataSourceId = dataSource.Id;

            var entity = new SourceEntity(dataSource.Id, $"Hf{tag}", RegistrySourceKind.External);
            db.SourceEntities.Add(entity);
            await db.SaveChangesAsync();
            sourceEntityId = entity.Id;

            // Останній запуск сутності — провал → активне джерело «з помилкою».
            var run = new CollectionRun(entity.Id, now.AddHours(-2), now.AddHours(-1), false, null, now.AddMinutes(-5));
            run.Complete("Failed", 0, now.AddMinutes(-4), "boom");
            db.CollectionRuns.Add(run);

            // Прогалина покриття: пропущений інтервал.
            db.CollectionCoverages.Add(CollectionCoverage.Skipped(
                entity.Id, 202601, CollectionCoverage.SkippedPointCeiling, "ceiling", now));

            db.JobProgresses.Add(new JobProgress(runningJobId, "hf.test", now));

            var failed = new JobProgress(failedJobId, "hf.test", now);
            failed.Finish("Failed", "boom", now);
            db.JobProgresses.Add(failed);

            // Провал дворічної давності не входить у «за добу».
            var old = new JobProgress(oldFailedJobId, "hf.test", now.AddDays(-3));
            old.Finish("Failed", "boom", now.AddDays(-3));
            db.JobProgresses.Add(old);

            await db.SaveChangesAsync();
        }

        try
        {
            // sources.failed кешується на 60 с (на хост): `before` його уже закешував, а дані
            // щойно змінено — скидаємо кеш хоста, а не вимикаємо його в продукті.
            app.Services.GetRequiredService<HealthCountCache>().Invalidate();

            var after = await ReadFactsAsync(client, app);

            int Delta(string block, string field)
                => after.GetProperty(block).GetProperty(field).GetInt32()
                   - before.GetProperty(block).GetProperty(field).GetInt32();

            Assert.Equal(1, Delta("jobs", "running"));
            Assert.Equal(1, Delta("jobs", "failed24h"));
            Assert.Equal(1, Delta("sources", "active"));
            Assert.Equal(1, Delta("sources", "failed"));
            Assert.Equal(1, Delta("sources", "gaps"));

            var lastError = after.GetProperty("about").GetProperty("lastErrorAt").GetDateTimeOffset().UtcDateTime;
            Assert.True(lastError >= now.AddSeconds(-5), $"lastErrorAt {lastError:O} < {now:O}");
        }
        finally
        {
            await using var db = sql.CreateContext();
            await db.CollectionCoverages.Where(c => c.SourceEntityId == sourceEntityId).ExecuteDeleteAsync();
            await db.CollectionRuns.Where(r => r.SourceEntityId == sourceEntityId).ExecuteDeleteAsync();
            await db.SourceEntities.Where(e => e.Id == sourceEntityId).ExecuteDeleteAsync();
            await db.DataSources.Where(s => s.Id == dataSourceId).ExecuteDeleteAsync();
            await db.JobProgresses
                .Where(p => p.JobId == runningJobId || p.JobId == failedJobId || p.JobId == oldFailedJobId)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зонд_СУБД_що_впав_дає_null_а_не_виняток()
    {
        // Імітація «немає права на msdb / dm_os_volume_stats»: обидва зонди падають на СУБД.
        await using var db = sql.CreateContext();
        var store = new SystemHealthStore(
            db,
            NullLogger<SystemHealthStore>.Instance,
            new HealthProbeSql("SELECT CAST(1 / 0 AS bigint) AS Value", "SELECT CAST(1 / 0 AS datetime2) AS Value"));

        var snapshot = await store.ReadAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.Null(snapshot.FreeSpaceDataDiskGb);
        Assert.Null(snapshot.LastBackupAt);

        // Лічильники з наших таблиць від падіння зондів не страждають.
        Assert.True(snapshot.Jobs.Running >= 0);
        Assert.True(snapshot.Sources.Active >= 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Робочі_зонди_дають_число_не_менше_нуля_або_null()
    {
        await using var db = sql.CreateContext();
        var store = new SystemHealthStore(db, NullLogger<SystemHealthStore>.Instance);

        var snapshot = await store.ReadAsync(DateTime.UtcNow, CancellationToken.None);

        Assert.True(snapshot.FreeSpaceDataDiskGb is null or >= 0);
        Assert.True(snapshot.LastBackupAt is null || snapshot.LastBackupAt.Value.Kind == DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(true, true, "WindowsAndLocal")]
    [InlineData(true, false, "Windows")]
    [InlineData(false, true, "Local")]
    [InlineData(false, false, "None")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Режим_входу_за_зареєстрованими_схемами(bool windows, bool local, string expected)
        => Assert.Equal(expected, SystemHostFacts.SignInModeOf(windows, local));

    private static async Task<JsonElement> ReadFactsAsync(HttpClient client, EcrApiFactory app)
    {
        var response = await client.GetAsync(Facts);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.Clone();
    }

    private static void AssertUtcOrNull(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        Assert.EndsWith("Z", value.GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in Strings(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in Strings(item))
                    {
                        yield return nested;
                    }
                }

                break;
            default:
                break;
        }
    }
}
