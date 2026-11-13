// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.B60.cs
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

// LAND-YEAR B60: стан аркуша відкритого якоря для не-місячних цілей і «пізня» правка прогону закритого якоря.
public sealed partial class YearLayoutTailSqlTests
{
    // ── B60: відкритий якір і «пізня» правка закритого якоря ─────────────

    /// <summary>
    /// B60(а): відкритий січень, аркуш січня ПОДАНИЙ, інші місяці — чернетки. Прогін якоря пише не-місячний підсумок
    /// (агрегатний стан аркуша — чернетка, як для PATCH), а місячну CM1 поданого січня — ні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B60_відкритий_якір_пише_неmісячне_за_агрегатом_аркуша_а_CM1_за_січнем()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            await SubmitAsync(world, Anchor);

            await RecalculateAnchorAsync(world);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(-1m, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>Контроль B60(а): аркуш поданий в УСІХ місяцях (агрегат Submitted) — підсумок не пишеться, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B60_контроль_поданий_у_всіх_місяцях_підсумок_не_пишеться()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            for (var m = 1; m <= 12; m++)
            {
                await SubmitAsync(world, m);
            }

            await RecalculateAnchorAsync(world);

            Assert.Equal(0m, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(-1m, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// B60(б): закритий якір, решта року — Grace (агрегат Grace). Прогін якоря (B24) пише підсумок як ПІЗНЮ зміну
    /// (<c>IsLateEdit = 1</c>, річне правило), а не за станом закритого січня. Контроль: решта року Open — не пізня.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-70")]
    public async Task B60_прогін_закритого_якоря_пізній_за_агрегатом_року(bool restInGrace)
    {
        var world = await ArrangeAsync();
        try
        {
            await SeedInputsAsync(world);
            await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
            await SetCellAsync(world, Anchor, world.Total, 0m, calculated: true);
            await CloseAsync(world, Anchor);
            var states = States(closed: [Anchor]);
            if (restInGrace)
            {
                for (var m = 2; m <= 12; m++)
                {
                    await GraceAsync(world, m);
                    states[Period(m)] = PeriodState.Grace;
                }
            }

            await RunJobAsync(world, periodKey: Period(Anchor), year: null, states);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(restInGrace, await LastIsLateEditAsync(world, Anchor, world.Total));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>B60(а): CM9 власника = 90, застарілий TOT = 0 і CM1 = −1 у відкритому якорі.</summary>
    private async Task PrepareAnchorRunAsync(World world)
    {
        await SeedInputsAsync(world);
        await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
        await SetCellAsync(world, Anchor, world.Total, 0m, calculated: true);
        await SetCellAsync(world, Anchor, world.CalcMonth[Anchor], -1m, calculated: true);
    }

    private async Task RecalculateAnchorAsync(World world)
    {
        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        await Formulas(db, world, store, router, Periods(world, States(closed: [])))
            .RecalculateAllAsync(world.Doc.DocumentId, Key(Anchor), CancellationToken.None);
    }

    private static async Task GraceAsync(World world, int month)
    {
        await using var setup = world.Builder.CreateContext();
        var period = await setup.Periods.SingleAsync(p => p.ProjectId == world.Doc.ProjectId && p.PeriodKeyValue == Period(month));
        period.AdvanceTo(PeriodState.Grace, DateTime.UtcNow);
        await setup.SaveChangesAsync();
    }

    /// <summary>Аркуш документа поданий у місяці <paramref name="month"/> (стан процесу <c>Submitted</c>).</summary>
    private static async Task SubmitAsync(World world, int month)
    {
        await using var setup = world.Builder.CreateContext();
        var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, Period(month));
        state.Submit(userId: 5, DateTime.UtcNow);
        setup.ApprovalStates.Add(state);
        await setup.SaveChangesAsync();
    }

    /// <summary>Позначка «пізня» в ОСТАННЬОМУ записі журналу комірки (документ цього тесту, його період і колонка).</summary>
    private async Task<bool> LastIsLateEditAsync(World world, int month, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP (1) CAST(IsLateEdit AS int) FROM aud.CellChange WHERE DocumentId = {world.Doc.DocumentId} " +
            $"AND PeriodKey = {Period(month)} AND ColumnDefId = {columnDefId} ORDER BY Id DESC";
        var result = await command.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (int)result! == 1;
    }
}
