// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.B58.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

// LAND-YEAR B58: fan-out до закритого якоря річної розкладки у відкритому році (світ і прибирання — основна частина класу).
public sealed partial class YearLayoutTailSqlTests
{
    // ── B58: fan-out до закритого якоря ─────────────────────────────────

    /// <summary>
    /// B58: січень (якір) закритий, рік відкритий. Правка M9 у вересні перераховує CM9 власника, і fan-out доходить до
    /// ЗАКРИТОГО якоря — його не-місячний підсумок <c>TOT</c> оновлюється одразу (як прогін B24), а місячна <c>CM1</c>
    /// закритого січня — ні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B58_правка_CMm_оновлює_підсумок_закритого_якоря_у_відкритому_році()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareFanOutAsync(world);

            await RunFanOutAsync(world, States(closed: [Anchor]));

            // CM9 = M9 × IN = 11 × 10 у власнику; TOT = CM9 у закритому якорі (було 90).
            Assert.Equal(11m * Input, await PhysicalAsync(world, 9, world.CalcMonth[9]));
            Assert.Equal(11m * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(-1m, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>Контроль B58: закритий увесь рік (агрегат Closed) — підсумок якоря не пишеться, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B58_контроль_закритий_увесь_рік_підсумок_якоря_не_пишеться()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareFanOutAsync(world);

            await RunFanOutAsync(world, States(closed: [.. Enumerable.Range(1, 12)]));

            Assert.Equal(11m * Input, await PhysicalAsync(world, 9, world.CalcMonth[9]));
            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>B58: CM9 = 90 у власнику, підсумок TOT = 90 у закритому якорі, CM1 якоря = −1; правка M9: 9 → 11.</summary>
    private async Task PrepareFanOutAsync(World world)
    {
        await SeedInputsAsync(world);
        await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
        await SetCellAsync(world, Anchor, world.Total, 9 * Input, calculated: true);
        await SetCellAsync(world, Anchor, world.CalcMonth[Anchor], -1m, calculated: true);
        await CloseAsync(world, Anchor);
        await SetCellAsync(world, 9, world.Doc.MonthColumnDefIds[8], 11m);
    }

    private async Task RunFanOutAsync(World world, Dictionary<int, PeriodState> states)
    {
        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var periods = Periods(world, states);
        var fanOut = new CrossPeriodFanOut(
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now)),
            Versions(world), periods, Substitute.For<IBackgroundJobScheduler>(), db, router, world.Metadata);
        var job = new FormulaRecalculationJob(Formulas(db, world, store, router, periods), db, fanOut, periods);

        await job.ExecuteAsync(
            new FormulaRecalculationRequest(
                world.Others[9].TableInstanceId, Period(9),
                [new DirtyCell(world.Others[9].RowIds[0], world.Doc.MonthColumnDefIds[8])]),
            new CapturingJobProgress(),
            CancellationToken.None);
    }

    private static async Task CloseAsync(World world, int month)
    {
        await using var setup = world.Builder.CreateContext();
        var period = await setup.Periods.SingleAsync(p => p.ProjectId == world.Doc.ProjectId && p.PeriodKeyValue == Period(month));
        period.AdvanceTo(PeriodState.Grace, DateTime.UtcNow);
        period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);
        await setup.SaveChangesAsync();
    }
}
