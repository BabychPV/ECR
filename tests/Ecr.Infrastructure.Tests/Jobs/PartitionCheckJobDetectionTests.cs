using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
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
/// ⚠ Межі партиціонування (`02-partitions.sql`) фіксовані аж до
/// <c>202712</c> — DDL тут НЕ потрібен: обидві гілки доводяться самим
/// годинником (<c>IClock</c>, і так уже injected-залежність job-и). Рання
/// дата лишає багато меж попереду (Succeeded); дата за місяць до останньої
/// зашитої межі лишає їх менше мінімуму (Degraded). Мутувати сам
/// `pf_ByPeriodKey` було б ризиковано — на ньому сидять реальні
/// партиційовані таблиці, спільні з рештою тестів колекції.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PartitionCheckJobDetectionTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Достатній_запас_дає_Succeeded()
    {
        // 2026-01: попереду ще 23 зашиті межі — далеко за мінімумом (2).
        var (status, details) = await RunAsync(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Succeeded", status);
        Assert.Contains("boundariesAhead", details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Недостатній_запас_дає_Degraded()
    {
        // 2027-11: єдина межа, що лишилась попереду зашитого діапазону, —
        // 202712. Одна межа проти мінімуму у дві — нестача (той самий поріг,
        // що перевіряє Нестача_запасу_партицій_дає_попередження).
        var (status, details) = await RunAsync(new DateTime(2027, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Degraded", status);
        Assert.Contains("boundariesAhead", details, StringComparison.Ordinal);
    }

    private async Task<(string Status, string Details)> RunAsync(DateTime utcNow)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var job = new PartitionCheckJob(db, Substitute.For<ISqlCapabilities>(), new TestClock(utcNow));
        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        await using var reader = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var run = await reader.MaintenanceRuns
            .Where(r => r.JobCode == PartitionCheckJob.Code)
            .OrderByDescending(r => r.StartedAt)
            .FirstAsync(CancellationToken.None);

        return (run.Status, run.DetailsJson ?? string.Empty);
    }
}
