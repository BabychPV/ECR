// tests/Ecr.Infrastructure.Tests/Persistence/CalculationIssueRunLifetimeTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ P-1124: проблема розрахунку (<c>calc.CalculationIssue</c>) не переживає свого прогону — сиріт не буває (на копії live
/// їх було 323 після перебудов Land і прибирання D-560/D-575): поточні проблеми неперерахованих таблиць переходять на
/// новий прогін при перемиканні, мертві (таблиці, якої в документі вже немає) зникають разом зі старим прогоном; кожен
/// шлях видалення прогону прибирає його проблеми, а забутий шлях падає на <c>FK_CIssue_Run</c>, а не смітить.
/// </summary>
/// <remarks>
/// Мутації (локально, не комітити): прибрати виклик <c>CarryOverAsync</c> у <c>RunCalculationHandler.SwitchAsync</c> —
/// червоніє тест перемикання (жива проблема зникає з прибраним прогоном); прибрати <c>DeleteForRunSql</c> у
/// <c>SupersededResultsPurger.DeleteRunRowAsync</c> — прогін лишається (547), червоніє тест перемикання; прибрати виклик
/// помічника в <c>DocumentDeletionStore</c> — тест видалення документа падає 547.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationIssueRunLifetimeTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Копія аркуша, якої в документі немає: проблема з нею — «таблиця вже не в документі».</summary>
    private const long GoneCopy = 424_242_424;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1124")]
    public async Task Перемикання_переносить_проблему_неперерахованої_таблиці_а_мертву_прибирає_разом_зі_старим_прогоном()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202707);
        long a, b;
        await using (var db = f.Builder.CreateContext())
        {
            await BindAsync(db, f);
            a = await PurgeKit.AddRunAsync(db, f, "Running", T0);
            await PurgeKit.InsertRowsAsync(db, f, a, 1, 1, 1, "A");
        }

        await CompleteAsync(f, a);
        await using (var db = f.Builder.CreateContext())
        {
            db.CalculationIssues.Add(Issue(f, a, copy: null, "LIVE"));
            db.CalculationIssues.Add(Issue(f, a, copy: GoneCopy, "GONE"));
            await db.SaveChangesAsync();
            b = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddMinutes(5));
            await PurgeKit.InsertRowsAsync(db, f, b, 1, 1, 1, "B");
        }

        // Новий прогін таблиці стенда не перераховує (профіль без перерахованих таблиць — як аркуш поза областю).
        await CompleteAsync(f, b);

        await using var check = f.Builder.CreateContext();
        Assert.False(await PurgeKit.RunExistsAsync(check, a), "старий прогін мав бути прибраний (D-560)");
        var left = await check.CalculationIssues.AsNoTracking()
            .Where(i => i.DocumentId == f.Document.DocumentId && i.PeriodKey == f.Period)
            .Select(i => new { i.RowKey, i.CalculationRunId })
            .ToListAsync();
        var live = Assert.Single(left);
        Assert.Equal("LIVE", live.RowKey);
        Assert.Equal(b, live.CalculationRunId);
        Assert.Equal(0, await OrphansAsync(check, f));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1124")]
    public async Task Рядок_прогону_з_проблемами_не_видаляється_мимо_них()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202708);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Superseded", T0);
        db.CalculationIssues.Add(Issue(f, run, copy: null, "R1"));
        await db.SaveChangesAsync();

        // Шлях, що забув про проблеми, — відмова бази, а не сирота.
        var error = await Assert.ThrowsAsync<SqlException>(
            () => db.Database.ExecuteSqlAsync($"DELETE FROM calc.CalculationRun WHERE Id = {run}"));
        Assert.Equal(547, error.Number);
        Assert.True(await PurgeKit.RunExistsAsync(db, run));
        Assert.Equal(1, await db.CalculationIssues.CountAsync(i => i.CalculationRunId == run));
    }

    /// <summary>Форма шляху <c>RecalculationJob</c> («зайвий» прогін названого періоду): помічник, далі EF <c>Remove</c>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1124")]
    public async Task Помічник_прибирає_проблеми_прогону_перед_видаленням_його_рядка()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202709);
        long run, other;
        await using (var setup = f.Builder.CreateContext())
        {
            run = await PurgeKit.AddRunAsync(setup, f, "Running", T0);
            other = await PurgeKit.AddRunAsync(setup, f, "Superseded", T0.AddMinutes(-5));
            setup.CalculationIssues.Add(Issue(f, run, copy: null, "R1"));
            setup.CalculationIssues.Add(Issue(f, other, copy: null, "R2"));
            await setup.SaveChangesAsync();
        }

        // Як у задачі: рядок прогону відстежується контекстом, його проблеми — ні.
        await using var db = f.Builder.CreateContext();
        var tracked = await db.CalculationRuns.SingleAsync(r => r.Id == run);
        Assert.Equal(1, await CalculationIssueRunCleanup.DeleteForRunsAsync(db, [run], CancellationToken.None));
        db.CalculationRuns.Remove(tracked);
        await db.SaveChangesAsync();

        Assert.False(await PurgeKit.RunExistsAsync(db, run));
        Assert.Equal(0, await db.CalculationIssues.CountAsync(i => i.CalculationRunId == run));
        Assert.Equal(1, await db.CalculationIssues.CountAsync(i => i.CalculationRunId == other));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P-1124")]
    public async Task Видалення_документа_прибирає_прогони_разом_з_їхніми_проблемами()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202710);
        long run;
        await using (var db = f.Builder.CreateContext())
        {
            run = await PurgeKit.AddRunAsync(db, f, "Current", T0);
            await PurgeKit.InsertRowsAsync(db, f, run, 1, 1, 1, "D");
            db.CalculationIssues.Add(Issue(f, run, copy: null, "R1"));
            await db.SaveChangesAsync();
        }

        await using (var db = f.Builder.CreateContext())
        {
            await new DocumentDeletionStore(db).DeleteAsync(f.Document.DocumentId, CancellationToken.None);
        }

        await using var check = f.Builder.CreateContext();
        Assert.False(await PurgeKit.RunExistsAsync(check, run));
        Assert.Equal(0, await check.CalculationIssues.CountAsync(i => i.CalculationRunId == run));
    }

    private static CalculationIssue Issue(PurgeStand f, long runId, long? copy, string rowKey)
        => new(f.Document.DocumentId, f.Period, f.Document.TableDefId, copy, rowKey, 1, f.MethodologyId, runId, T0,
            ICalculationIssueStore.MissingInputs);

    /// <summary>Таблиця стенда прив'язана: самолікування AN-160 (таблиці без прив'язок) проблем не чіпає.</summary>
    private static async Task BindAsync(EcrDbContext db, PurgeStand f)
    {
        db.CalculationBindings.Add(new CalculationBinding(
            f.Document.TableDefId, f.Document.ColumnDefIds[0], f.MethodologyId, "OUT1", "{}"));
        await db.SaveChangesAsync();
    }

    private static Task<int> OrphansAsync(EcrDbContext db, PurgeStand f)
        => db.CalculationIssues.AsNoTracking()
            .CountAsync(i => i.DocumentId == f.Document.DocumentId && !db.CalculationRuns.Any(r => r.Id == i.CalculationRunId));

    private static async Task CompleteAsync(PurgeStand f, long runId)
    {
        await using var db = f.Builder.CreateContext();
        var clock = new TestClock(T0);
        var handler = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock, purgeOptions: PurgeKit.On()),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            issues: new CalculationIssueStore(db, clock));

        await handler.CompleteAsync(runId, new ModuleProfile(), CancellationToken.None);
    }
}
