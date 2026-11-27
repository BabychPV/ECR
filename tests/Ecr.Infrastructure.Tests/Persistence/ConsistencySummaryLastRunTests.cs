// tests/Ecr.Infrastructure.Tests/Persistence/ConsistencySummaryLastRunTests.cs
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ 24.11 (аудит «Consistency issues»): підсумок журналу несе останній завершений прогін з переліком правил —
/// екран показує, ЩО перевірено, а не лише «0 знахідок».
/// </summary>
/// <remarks>
/// Мутація: <c>summary with { LastRun = … }</c> → без присвоєння — червоніє наскрізний тест; <c>ParseChecks</c> без
/// читання <c>skipped</c> — червоніє розбір.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-7.7")]
public sealed class ConsistencySummaryLastRunTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Підсумок_несе_правила_останнього_завершеного_прогону()
    {
        var at = new DateTime(2026, 11, 24, 16, 0, 0, DateTimeKind.Utc);
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var run = new MaintenanceRun("consistency-check", at);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);
        run.Complete(
            "Degraded",
            """{"issues":3,"checks":[{"code":"ORPHANED_CELL","found":0,"complete":true,"skipped":false,"ms":5},{"code":"CALC_RESULT_VERSION_NOT_EFFECTIVE","found":3,"complete":false,"skipped":false,"ms":7},{"code":"NON_CURRENT_RUN_ROWS","found":0,"complete":false,"skipped":true,"ms":0}]}""",
            at.AddSeconds(2));
        await db.SaveChangesAsync(CancellationToken.None);

        var summary = await new ConsistencyIssueReader(db).ReadSummaryAsync(openOnly: true, CancellationToken.None);

        Assert.NotNull(summary.LastRun);
        Assert.Equal("Degraded", summary.LastRun.Status);
        Assert.Equal(
            [("ORPHANED_CELL", 0, true, false), ("CALC_RESULT_VERSION_NOT_EFFECTIVE", 3, false, false), ("NON_CURRENT_RUN_ROWS", 0, false, true)],
            summary.LastRun.Checks.Select(c => (c.RuleCode, c.Found, c.Complete, c.Skipped)).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"issues\":0}")]
    [InlineData("not json")]
    public void Деталі_без_переліку_правил_дають_порожньо(string? details)
        => Assert.Empty(ConsistencyIssueReader.ParseChecks(details));
}
