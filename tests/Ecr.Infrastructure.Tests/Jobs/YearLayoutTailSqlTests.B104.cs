// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.B104.cs
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

// ✎ B104: «пізня» позначка прогону ВІДКРИТОГО якоря — на запис: не-місячна ціль за річним правилом, місячна — за своїм періодом.
public sealed partial class YearLayoutTailSqlTests
{
    /// <summary>
    /// B104: січень (якір) у Grace. Якщо інший місяць року ще Open — не-місячний підсумок TOT НЕ пізній (річне правило:
    /// є Open), а місячна CM1 січня — пізня (січень у Grace). Контроль: решта року теж Grace — TOT пізній.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-70")]
    public async Task B104_відкритий_якір_у_Grace_пізня_позначка_на_запис(bool anotherMonthOpen)
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            var states = States(closed: []);
            for (var m = 1; m <= 12; m++)
            {
                if (m == Anchor || !anotherMonthOpen || m != 6)
                {
                    await GraceAsync(world, m);
                    states[Period(m)] = PeriodState.Grace;
                }
            }

            await using (var db = world.Builder.CreateContext())
            {
                var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
                var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
                await Formulas(db, world, store, router, Periods(world, states))
                    .RecalculateAllAsync(world.Doc.DocumentId, Key(Anchor), CancellationToken.None);
            }

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(1 * Input, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
            Assert.Equal(!anotherMonthOpen, await LastIsLateEditAsync(world, Anchor, world.Total));
            Assert.True(await LastIsLateEditAsync(world, Anchor, world.CalcMonth[Anchor]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }
}
