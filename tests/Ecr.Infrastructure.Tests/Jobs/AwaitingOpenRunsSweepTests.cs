// tests/Ecr.Infrastructure.Tests/Jobs/AwaitingOpenRunsSweepTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-575 / P-1123-18 (людина 23.11: «прибрати їх одразу»): застарілі результати методологій у не відкритому місяці
/// (<c>Scheduled</c>) АКТИВНОГО проєкту — актуальний <c>calc.CalculationRun</c> і його <c>calc.CalculationResult</c> —
/// прибирає задача станів щогодини і той самий прохід на старті застосунку; відкритий місяць, чернетка проєкту й період із
/// затвердженим аркушем — не чіпаються.
/// </summary>
/// <remarks>
/// ⛔ Дефект (репетиція b11, копія EcrLandW15zR): прогін до D-575 лишив актуальний прогін 202612 документа 96 з 14 результатами,
/// які комірки грудня показували б до відкриття місяця.
/// ⛔ Мутація (локально, не комітити): прибрати виклик <c>DropAwaitingOpenRunsAsync</c> у <c>PeriodStateJob.SweepAwaitingOpenAsync</c>
/// — червоніють «задача станів» і «старт»; контролі зелені.
/// </remarks>
[Collection("SqlServer")]
public sealed class AwaitingOpenRunsSweepTests(SqlServerFixture sql)
{
    /// <summary>25 січня: за OPN45 січень ще не відкрито (15 лютого), за OPN0 — відкрито з 1 січня.</summary>
    private static readonly DateTime BeforeOpen = new(2026, 1, 25, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-575")]
    public async Task Задача_станів_прибирає_прогін_і_результати_не_відкритого_місяця_а_контролі_лишає()
    {
        var scheduled = await ArrangeAsync(openOffsetDays: 45, active: true);
        var open = await ArrangeAsync(openOffsetDays: 0, active: true);
        var draft = await ArrangeAsync(openOffsetDays: 45, active: false);
        var approved = await ArrangeAsync(openOffsetDays: 45, active: true, approvedSheet: true);
        try
        {
            await using (var db = scheduled.Stand.Builder.CreateContext())
            {
                await Job(db).ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            }

            await AssertSweptAsync(scheduled);
            await AssertKeptAsync(open);
            await AssertKeptAsync(draft);
            await AssertKeptAsync(approved);
        }
        finally
        {
            await RetireAsync(scheduled, open, draft, approved);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-575")]
    public async Task Старт_прибирає_не_відкритий_місяць_за_ефективним_станом_а_відкритий_датою_лишає()
    {
        var scheduled = await ArrangeAsync(openOffsetDays: 45, active: true);

        // Межа відкриття вже минула, а задача станів ще не пройшла: збережено Scheduled, ефективно — Open. Не чіпати.
        var effectivelyOpen = await ArrangeAsync(openOffsetDays: 0, active: true);
        try
        {
            await using (var db = scheduled.Stand.Builder.CreateContext())
            {
                Assert.True(await Job(db).SweepAwaitingOpenAsync(CancellationToken.None) >= 2);
            }

            await AssertSweptAsync(scheduled);
            await AssertKeptAsync(effectivelyOpen);
            Assert.Equal(PeriodState.Scheduled, await StoredStateAsync(effectivelyOpen));
        }
        finally
        {
            await RetireAsync(scheduled, effectivelyOpen);
        }
    }

    private static PeriodStateJob Job(EcrDbContext db)
        => new(
            db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(BeforeOpen),
            new MaterializationScheduler(db, Substitute.For<IBackgroundJobScheduler>()),
            results: new CalculationResultStore(db, new TestClock(BeforeOpen), purgeOptions: PurgeKit.On()));

    private sealed record World(PurgeStand Stand, long RunId);

    /// <summary>Документ січня з актуальним прогоном (3 результати); межі за політикою; за потреби — активний і затверджений аркуш.</summary>
    private async Task<World> ArrangeAsync(int openOffsetDays, bool active, bool approvedSheet = false)
    {
        var stand = await PurgeKit.ArrangeAsync(sql, 202601);
        await using var db = stand.Builder.CreateContext();

        var project = await db.Projects.SingleAsync(p => p.Id == stand.Document.ProjectId);
        var policy = new PeriodPolicy(EcrCode.Create($"OPN{openOffsetDays}"), openOffsetDays, graceOffsetDays: 30,
            hardCloseOffsetDays: 60, yearGraceOffsetDays: 45);
        var period = await db.Periods.SingleAsync(p => p.ProjectId == project.Id && p.PeriodKeyValue == stand.Period);
        period.RecomputeBoundaries(policy, SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());
        if (active)
        {
            project.Activate(BeforeOpen.AddDays(-30));
        }

        if (approvedSheet)
        {
            var state = new ApprovalState(stand.Document.DocumentId, stand.Document.SheetDefId, stand.Period);
            state.Submit(1, BeforeOpen.AddDays(-2));
            state.Approve(1, BeforeOpen.AddDays(-1));
            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync();
        Assert.Equal(PeriodState.Scheduled, period.State);

        var runId = await PurgeKit.AddRunAsync(db, stand, "Current", BeforeOpen.AddDays(-3));
        await PurgeKit.InsertRowsAsync(db, stand, runId, 3, 1, 1, "S");

        // ✎ P-1124: і проблема розрахунку прогону — застаріла разом із ним (не відкритий місяць), без сироти.
        db.CalculationIssues.Add(new CalculationIssue(
            stand.Document.DocumentId, stand.Period, stand.Document.TableDefId, null, "R-1", 1, stand.MethodologyId, runId,
            BeforeOpen.AddDays(-3), CalculationIssue.MissingInputsKind));
        await db.SaveChangesAsync();
        return new World(stand, runId);
    }

    private static async Task AssertSweptAsync(World w)
    {
        await using var db = w.Stand.Builder.CreateContext();
        Assert.False(await db.CalculationRuns.AnyAsync(r => r.Id == w.RunId && r.Status == CalculationRun.CurrentStatus));
        Assert.Equal(0, await db.CalculationResults.CountAsync(r => r.CalculationRunId == w.RunId && r.PeriodKey == w.Stand.Period));

        // ✎ P-1124: прогін прибрано повністю (D-560) — і його проблеми з ним; посилань у нікуди немає.
        Assert.False(await db.CalculationRuns.AnyAsync(r => r.Id == w.RunId));
        Assert.Equal(0, await db.CalculationIssues.CountAsync(i => i.CalculationRunId == w.RunId));
        Assert.Equal(0, await db.CalculationIssues.CountAsync(
            i => i.DocumentId == w.Stand.Document.DocumentId && !db.CalculationRuns.Any(r => r.Id == i.CalculationRunId)));
    }

    private static async Task AssertKeptAsync(World w)
    {
        await using var db = w.Stand.Builder.CreateContext();
        Assert.True(await db.CalculationRuns.AnyAsync(r => r.Id == w.RunId && r.Status == CalculationRun.CurrentStatus));
        Assert.Equal(3, await db.CalculationResults.CountAsync(r => r.CalculationRunId == w.RunId && r.PeriodKey == w.Stand.Period));
        Assert.Equal(1, await db.CalculationIssues.CountAsync(i => i.CalculationRunId == w.RunId));
    }

    private static async Task<PeriodState> StoredStateAsync(World w)
    {
        await using var db = w.Stand.Builder.CreateContext();
        return await db.Periods
            .Where(p => p.ProjectId == w.Stand.Document.ProjectId && p.PeriodKeyValue == w.Stand.Period)
            .Select(p => p.State)
            .SingleAsync();
    }

    /// <summary>Архівує активні проєкти тесту: задача станів обходить лише активні.</summary>
    private static async Task RetireAsync(params World[] worlds)
    {
        foreach (var w in worlds)
        {
            await using var db = w.Stand.Builder.CreateContext();
            var project = await db.Projects.SingleAsync(p => p.Id == w.Stand.Document.ProjectId);
            if (project.Status == ProjectStatus.Active)
            {
                project.Archive(BeforeOpen.AddDays(400));
                await db.SaveChangesAsync();
            }
        }
    }
}
