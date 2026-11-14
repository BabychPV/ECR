// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.B103.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

// ✎ B103: стан помилки формули (calc.FormulaError) НЕ-місячної комірки відкритого якоря — за агрегатом року (B24/B60).
public sealed partial class YearLayoutTailSqlTests
{
    /// <summary>TOT і CM1 діляться на нуль (#DIV/0).</summary>
    private static readonly Dictionary<int, string> Erroring = new()
    {
        [TotalFormulaId] = "[CM9] / 0",
        [MonthFormulaBase + Anchor] = "[M1] / 0",
    };
    /// <summary>
    /// B103: відкритий січень, аркуш січня ПОДАНИЙ, інші місяці — чернетки. Помилка не-місячної TOT записується
    /// (агрегат аркуша — чернетка, як значення в B60(а)), а помилка місячної CM1 поданого січня — ні (стан січня).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-475")]
    public async Task B103_помилка_неmісячної_комірки_відкритого_якоря_записується_за_агрегатом()
    {
        var world = await ArrangeAsync();
        world = world with { Metadata = Metadata(world, Erroring) };
        try
        {
            await PrepareAnchorRunAsync(world);
            await SubmitAsync(world, Anchor);

            await RecalculateAnchorWithErrorsAsync(world);

            Assert.True(await HasFormulaErrorAsync(world, world.Total));
            Assert.False(await HasFormulaErrorAsync(world, world.CalcMonth[Anchor]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// B103: збережена помилка TOT, формула знову рахується — за агрегатом (чернетка) помилка знімається, хоча січень
    /// поданий.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-475")]
    public async Task B103_помилка_неmісячної_комірки_відкритого_якоря_знімається_за_агрегатом()
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            await SeedTotalErrorAsync(world);
            await SubmitAsync(world, Anchor);

            await RecalculateAnchorWithErrorsAsync(world);

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.False(await HasFormulaErrorAsync(world, world.Total));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>Контроль B103: аркуш поданий в УСІХ місяцях (агрегат Submitted) — помилка TOT не записується, як доти.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-475")]
    public async Task B103_контроль_поданий_у_всіх_місяцях_помилка_не_записується()
    {
        var world = await ArrangeAsync();
        world = world with { Metadata = Metadata(world, Erroring) };
        try
        {
            await PrepareAnchorRunAsync(world);
            for (var m = 1; m <= 12; m++)
            {
                await SubmitAsync(world, m);
            }

            await RecalculateAnchorWithErrorsAsync(world);

            Assert.False(await HasFormulaErrorAsync(world, world.Total));
            Assert.False(await HasFormulaErrorAsync(world, world.CalcMonth[Anchor]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }


    private async Task RecalculateAnchorWithErrorsAsync(World world)
    {
        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        await Formulas(db, world, store, router, Periods(world, States(closed: [])), new FormulaErrorStore(db))
            .RecalculateAllAsync(world.Doc.DocumentId, Key(Anchor), CancellationToken.None);
    }

    /// <summary>Збережена помилка TOT у якорі (стан «до» прогону).</summary>
    private static async Task SeedTotalErrorAsync(World world)
    {
        await using var db = world.Builder.CreateContext();
        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == world.Doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();
        await new FormulaErrorStore(db).ApplyAsync(
            [
                new FormulaErrorRow(
                    world.Doc.DocumentId, Period(Anchor), world.Doc.TableInstanceId, world.Doc.TableDefId, null,
                    world.Doc.RowIds[0], rowKey, world.Total, TotalFormulaId, FormulaError.ErrorKind, "#DIV/0",
                    null, null, null, null, null, DateTime.UtcNow.AddDays(-1)),
            ],
            [],
            CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private async Task<bool> HasFormulaErrorAsync(World world, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM calc.FormulaError WHERE DocumentId = {world.Doc.DocumentId} " +
            $"AND PeriodKey = {Period(Anchor)} AND ColumnDefId = {columnDefId}";
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }
}
