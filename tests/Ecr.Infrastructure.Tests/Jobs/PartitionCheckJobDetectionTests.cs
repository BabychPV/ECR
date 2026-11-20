using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-185: реальний запуск <c>PartitionCheckJob</c> проти справжніх меж
/// <c>pf_ByPeriodKey</c>, а не текст файлу.
/// </summary>
/// <remarks>
/// ⛔ Старий <c>Достатній_запас_не_породжує_шуму</c> робив лише
/// <c>Assert.Contains("enough ? \"Succeeded\" : \"Degraded\"", source, ...)</c>
/// — доведено мутацією, що інверсія умови (<c>ahead &gt;= Minimum</c> →
/// <c>ahead &lt; Minimum</c>) лишала перевірений тернарний вираз (лише
/// текст) незмінним.
/// <para>
/// ✎ PSS-09: межі з `02-partitions.sql` більше не зашиті (до січня року Y+2 від дати розгортання), тож дата «за місяць
/// до останньої межі» береться з самої бази, а не літералом. Бази — ВЛАСНІ: задача тепер ще й рахує рядки в крайніх
/// партиціях, а спільна база колекції має в правій партиції чужі рядки тестів (PeriodKey 2099xx).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PartitionCheckJobDetectionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Достатній_запас_дає_Succeeded()
    {
        await WithDatabaseAsync("_pcd1", async cs =>
        {
            // 2026-01: попереду два роки меж — далеко за мінімумом (2); крайні партиції порожні.
            var (status, details) = await RunAsync(cs, new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal("Succeeded", status);
            Assert.Contains("boundariesAhead", details, StringComparison.Ordinal);
            Assert.Contains("\"edgeRows\":{\"first\":0,\"last\":0},", details, StringComparison.Ordinal);
            Assert.DoesNotContain("edgeWarning", details, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Недостатній_запас_дає_Degraded()
    {
        await WithDatabaseAsync("_pcd2", async cs =>
        {
            // Місяць перед останньою межею: попереду лише вона — одна межа проти мінімуму у дві (той самий поріг,
            // що перевіряє Нестача_запасу_партицій_дає_попередження).
            var last = await ScalarAsync(cs, """
                SELECT MAX(CAST(rv.value AS int)) FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id WHERE pf.name = N'pf_ByPeriodKey'
                """);
            var lastMonth = new DateTime((int)(last / 100), (int)(last % 100), 1, 0, 0, 0, DateTimeKind.Utc);
            var (status, details) = await RunAsync(cs, lastMonth.AddMonths(-1).AddDays(14));

            Assert.Equal("Degraded", status);
            Assert.Contains("\"boundariesAhead\":1,", details, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// PSS-09: рядки в крайніх партиціях — лічильники й ПОПЕРЕДЖЕННЯ <c>edgeWarning</c>, але не Degraded (✎ рев'ю M1:
    /// документи історичних і «далеких» років дозволені, щонічний Degraded був би шумом). Ліва: період до першої межі
    /// (202512); права: період за останньою межею. Мутація: прибрати <c>edgeWarning</c> з подробиць — червоне.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "PSS-09")]
    public async Task Рядки_в_крайній_партиції_дають_попередження_з_лічильниками_без_Degraded()
    {
        await WithDatabaseAsync("_pcd3", async cs =>
        {
            var early = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

            await new TestDocumentBuilder(cs).BuildAsync(periodKey: 202512, rowCount: 1);
            var (status, details) = await RunAsync(cs, early);
            Assert.Equal("Succeeded", status);
            Assert.Matches("\"edgeRows\":\\{\"first\":[1-9][0-9]*,\"last\":0\\},\"edgeWarning\":true", details);

            await new TestDocumentBuilder(cs).BuildAsync(periodKey: 209901, rowCount: 1);
            (status, details) = await RunAsync(cs, early);
            Assert.Equal("Succeeded", status);
            Assert.Matches("\"edgeRows\":\\{\"first\":[1-9][0-9]*,\"last\":[1-9][0-9]*\\},\"edgeWarning\":true", details);
        });
    }

    private static async Task WithDatabaseAsync(string suffix, Func<string, Task> body)
    {
        var database = SqlServerFixture.WithOwnDatabase(suffix);
        await database.InitializeAsync();
        try
        {
            await body(database.ConnectionString);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task<(string Status, string Details)> RunAsync(string cs, DateTime utcNow)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(cs)
            .Options);

        var job = new PartitionCheckJob(db, Substitute.For<ISqlCapabilities>(), new TestClock(utcNow));
        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        await using var reader = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(cs)
            .Options);

        var run = await reader.MaintenanceRuns
            .Where(r => r.JobCode == PartitionCheckJob.Code)
            .OrderByDescending(r => r.Id)
            .FirstAsync(CancellationToken.None);

        return (run.Status, run.DetailsJson ?? string.Empty);
    }

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
