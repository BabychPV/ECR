// tests/Ecr.Infrastructure.Tests/Jobs/CalculationRunSweepTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// P3 (ФВ-9.8): прибирання закриває осиротілі <c>calc.CalculationRun</c> у
/// <c>Running</c> (<see cref="AbandonedWorkSweeper"/>) — той самий критерій, що
/// для прогонів збору.
/// </summary>
/// <remarks>
/// ⚠ Прибирання глобальне, тож моменти — у 2031 році, а перевіряються стани
/// СВОЇХ прогонів, не лічильники.
/// <para>
/// Мутаційний доказ (прогнано, див. опис коміту): прибрати поріг віку
/// (<c>StartedAt &lt; startedBefore</c>) → свіжий прогін закрито, червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationRunSweepTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2031, 5, 11, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Прибирання_закриває_осиротілий_прогін_розрахунку_і_не_чіпає_свіжий_та_завершені()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;

        await using var db = chain.CreateContext();

        var orphan = new CalculationRun(document.ProjectId, periodKey, null, At.AddMinutes(-10));
        var fresh = new CalculationRun(document.ProjectId, periodKey, null, At.AddMinutes(-1));
        var current = Finished(new CalculationRun(document.ProjectId, periodKey, null, At.AddHours(-3)));
        current.MakeCurrent();
        var superseded = Finished(new CalculationRun(document.ProjectId, periodKey, null, At.AddHours(-4)));
        superseded.Supersede();
        db.CalculationRuns.AddRange(orphan, fresh, current, superseded);
        await db.SaveChangesAsync();

        var outcome = await new AbandonedWorkSweeper(db, new JobProgressStore(db))
            .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, At, purge: false, CancellationToken.None);

        Assert.True(outcome.CalculationRuns >= 1);
        Assert.True(outcome.Any);

        await using var check = chain.CreateContext();
        var closed = await check.CalculationRuns.AsNoTracking().SingleAsync(r => r.Id == orphan.Id);
        Assert.Equal(("Failed", (DateTime?)At), (closed.Status, closed.FinishedAt));
        Assert.True(JobProgressMessageCodec.TryDecode(closed.ErrorMessage, out var reason), closed.ErrorMessage);
        Assert.Equal(AbandonedWorkSweeper.AbandonedCalculationRunKey, reason.Key);

        Assert.Equal(
            new List<(string, DateTime?)>
            {
                ("Running", null), ("Current", At.AddHours(-3)), ("Superseded", At.AddHours(-4)),
            },
            await StatesAsync(check, fresh.Id, current.Id, superseded.Id));
    }

    /// <remarks>
    /// Поки жива задача перерахунку, прогін закривається лише після
    /// <see cref="AbandonedWorkSweeper.CalculationRunAbandonedAfter"/>. Мутація
    /// «межа не залежить від живого перерахунку» → 10-хвилинний прогін закрито,
    /// червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Прогін_при_живому_перерахунку_закривається_лише_після_жорсткої_межі()
    {
        var at = At.AddDays(1);
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var periodKey = document.PeriodKey.Value;

        await using var db = chain.CreateContext();

        var recent = new CalculationRun(document.ProjectId, periodKey, null, at.AddMinutes(-10));
        var ancient = new CalculationRun(
            document.ProjectId, periodKey, null, at - AbandonedWorkSweeper.CalculationRunAbandonedAfter - TimeSpan.FromMinutes(5));
        db.CalculationRuns.AddRange(recent, ancient);
        await db.SaveChangesAsync();

        var liveJob = $"IRecalculationJob-{Guid.NewGuid():N}";
        await new JobProgressStore(db).StartAsync(
            liveJob, typeof(IRecalculationJob).FullName!, at.AddMinutes(-1), CancellationToken.None);

        try
        {
            await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, at, purge: false, CancellationToken.None);

            await using var check = chain.CreateContext();
            Assert.Equal("Running", (await check.CalculationRuns.AsNoTracking().SingleAsync(r => r.Id == recent.Id)).Status);
            Assert.Equal("Failed", (await check.CalculationRuns.AsNoTracking().SingleAsync(r => r.Id == ancient.Id)).Status);
        }
        finally
        {
            await db.JobProgresses.Where(p => p.JobId == liveJob).ExecuteDeleteAsync();
        }
    }

    private static CalculationRun Finished(CalculationRun run)
    {
        run.Complete("Succeeded", run.StartedAt, profileJson: null, errorMessage: null);
        return run;
    }

    private static async Task<List<(string Status, DateTime? FinishedAt)>> StatesAsync(
        EcrDbContext db, params long[] ids)
    {
        var rows = await db.CalculationRuns.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.FinishedAt })
            .ToDictionaryAsync(r => r.Id);

        return [.. ids.Select(id => (rows[id].Status, rows[id].FinishedAt))];
    }
}
