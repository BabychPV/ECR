// tests/Ecr.Infrastructure.Tests/Jobs/MonthInputChangeQueryTests.cs
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ Огляд M1 (<c>D-564a</c>, і нічний <c>D-562</c>): документ-джерело (B179) будить споживача, коли його актуальний прогін
/// СТАВ актуальним після межі — за кінцем прогону (<c>FinishedAt</c>), а не за стартом: прогін, що стартував до межі й
/// завершився після, споживач не бачив.
/// </summary>
/// <remarks>МУТАЦІЯ: повернути <c>sr.StartedAt &gt;= since</c> — червоний рядок «завершився після межі».</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-01")]
public sealed class MonthInputChangeQueryTests(SqlServerFixture sql)
{
    private static readonly DateTime Since = new(2072, 2, 10, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public async Task Джерело_будить_за_кінцем_свого_прогону(int finishedMinutesAfterSince, bool wakes)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var consumer = await builder.BuildAsync();
        var source = await builder.BuildAsync();
        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, DocumentId, Status, StartedAt, FinishedAt)
            VALUES ({source.ProjectId}, {source.PeriodKey.Value}, {source.DocumentId}, N'Current',
                    {Since.AddHours(-1)}, {Since.AddMinutes(finishedMinutesAfterSince)});
            """);

        var reason = await NightlyInputChangeQuery.FirstChangeAsync(
            db, new NightlyInputProbe(consumer.ProjectId, consumer.DocumentId, Since, [], [source.DocumentId]),
            cellBranch: false, CancellationToken.None);

        Assert.Equal(wakes ? NightlyReason.Source : null, reason);
    }
}
