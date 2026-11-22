// tests/Ecr.Infrastructure.Tests/Jobs/RoutineJobPurgeTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// PERF-15 (аудит 21.11): успішні рутинні задачі (перерахунок формул) прибираються через добу, а не через 30 діб, щоб
/// «Мої задачі» (<c>HideRoutine</c> до <c>Take</c>) не проходила тисячі їхніх рядків в індексі автора на кожен опит.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально): (1) прибрати умову <c>JobCode == formula</c> у <c>PurgeRoutineSucceededAsync</c> — червоний
/// <c>Видаляє_лише_успішну_рутину_формул_старшу_за_межу</c> (зникає чужа задача); (2) прибрати <c>State == Succeeded</c> —
/// той самий тест (зникає провалена рутина); (3) прибрати виклик у <c>AbandonedWorkSweeper.PurgeAsync</c> — червоний
/// <c>Прохід_ретенції_прибирає_успішну_рутину_за_добу_а_решту_за_тридцять</c>.
/// <para>Межа тут — давня епоха: під нею лише рядки цього тесту (метод глобальний за задумом).</para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-15")]
public sealed class RoutineJobPurgeTests(SqlServerFixture sql)
{
    private static readonly DateTime Epoch = new(2001, 8, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string FormulaCode = typeof(IFormulaRecalculationJob).FullName!;

    [Fact]
    public async Task Видаляє_лише_успішну_рутину_формул_старшу_за_межу()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var tag = Guid.NewGuid().ToString("N");
        var oldRoutine = $"r-old-{tag}";
        var freshRoutine = $"r-fresh-{tag}";
        var failedRoutine = $"r-failed-{tag}";
        var oldOther = $"o-old-{tag}";
        string[] own = [oldRoutine, freshRoutine, failedRoutine, oldOther];

        try
        {
            await FinishedAsync(store, oldRoutine, FormulaCode, "Succeeded", Epoch.AddDays(-2));
            await FinishedAsync(store, freshRoutine, FormulaCode, "Succeeded", Epoch.AddHours(-1));
            await FinishedAsync(store, failedRoutine, FormulaCode, "Failed", Epoch.AddDays(-2));
            await FinishedAsync(store, oldOther, "Ecr.Test.Job", "Succeeded", Epoch.AddDays(-2));

            var deleted = await store.PurgeRoutineSucceededAsync(
                Epoch - IJobProgressStore.RetainRoutineSucceededFor, 100_000, CancellationToken.None);

            Assert.Equal(1, deleted);
            Assert.Null(await StateAsync(db, oldRoutine));
            Assert.Equal("Succeeded", await StateAsync(db, freshRoutine));
            Assert.Equal("Failed", await StateAsync(db, failedRoutine));
            Assert.Equal("Succeeded", await StateAsync(db, oldOther));
        }
        finally
        {
            await db.JobProgresses.Where(p => own.Contains(p.JobId)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Порція_обмежує_кількість_видалених()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var tag = Guid.NewGuid().ToString("N");
        string[] own = [$"b1-{tag}", $"b2-{tag}", $"b3-{tag}"];

        try
        {
            foreach (var id in own)
            {
                await FinishedAsync(store, id, FormulaCode, "Succeeded", Epoch.AddDays(-3));
            }

            var deleted = await store.PurgeRoutineSucceededAsync(Epoch - TimeSpan.FromDays(1), 2, CancellationToken.None);

            Assert.Equal(2, deleted);
            Assert.Equal(1, await db.JobProgresses.CountAsync(p => own.Contains(p.JobId)));
        }
        finally
        {
            await db.JobProgresses.Where(p => own.Contains(p.JobId)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Прохід_ретенції_прибирає_успішну_рутину_за_добу_а_решту_за_тридцять()
    {
        var now = new DateTime(2031, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var progress = Substitute.For<IJobProgressStore>();

        await using var db = sql.CreateContext();
        await new AbandonedWorkSweeper(db, progress)
            .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, now, purge: true, CancellationToken.None);

        await progress.Received().PurgeFinishedAsync(
            now - IJobProgressStore.RetainFinishedFor, AbandonedWorkSweeper.PurgeBatch, Arg.Any<CancellationToken>());
        await progress.Received().PurgeRoutineSucceededAsync(
            now - TimeSpan.FromHours(24), AbandonedWorkSweeper.PurgeBatch, Arg.Any<CancellationToken>());
    }

    private static async Task FinishedAsync(JobProgressStore store, string jobId, string code, string state, DateTime at)
    {
        await store.StartAsync(jobId, code, at, CancellationToken.None);
        await store.FinishAsync(jobId, state, state == "Failed" ? "x" : null, at, CancellationToken.None);
    }

    private static async Task<string?> StateAsync(EcrDbContext db, string jobId)
        => await db.JobProgresses.AsNoTracking().Where(p => p.JobId == jobId).Select(p => p.State).FirstOrDefaultAsync();
}
