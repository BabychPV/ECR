// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.B79Quiz.cs
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

// ✎ B79, відповіді людини на квіз 16.11: (1) річний якір (не-місячний підсумок) перераховується, доки рік не затверджено
// ЦІЛКОМ, а затверджені місячні значення не змінюються ніколи; (3) правка погоджувача знімає позначку лише своєму місяцю.
public sealed partial class YearLayoutTailSqlTests
{
    private static readonly int[] ApproverOtherMonths = [2, 3];
    private static readonly int[] AllSubmittedMonths = [Anchor, 2, 3];

    /// <summary>
    /// Квіз 16.11 (1): відкритий якір. Січень…листопад ЗАТВЕРДЖЕНО, грудень поданий — рік затверджено не цілком: підсумок
    /// TOT перераховано, позначку отримує поданий грудень, а затверджена місячна CM1 січня не змінилась. Негатив: усі
    /// дванадцять місяців затверджено — якір пропущено: ні запису TOT, ні CM1, ні позначки.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у <c>EnterSheetsAsync</c> (гілка відкритого якоря) вважати агрегат року завжди придатним до
    /// запису — рядок <c>allApproved: true</c> червоніє (TOT записано); вважати його завжди непридатним — рядок
    /// <c>false</c> червоніє (TOT лишається 0).
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B79_якір_року_перераховується_доки_рік_не_затверджено_цілком_а_повністю_затверджений_пропущено(bool allApproved)
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            for (var m = 1; m <= 11; m++)
            {
                await ApproveAsync(world, m);
            }

            if (allApproved)
            {
                await ApproveAsync(world, 12);
            }
            else
            {
                await SubmitAsync(world, 12);
            }

            var (sheets, months) = await RecalculateAnchorWithSinksAsync(world);

            // Затверджене місячне значення січня не змінюється ніколи.
            Assert.Equal(-1m, await PhysicalAsync(world, Anchor, world.CalcMonth[Anchor]));
            Assert.Empty(sheets);

            if (allApproved)
            {
                Assert.Equal(0m, await PhysicalAsync(world, Anchor, world.Total));
                Assert.Empty(months);
                return;
            }

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            Assert.Equal(new[] { (Period(12), world.Doc.SheetDefId) }, months);
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    /// <summary>
    /// Квіз 16.11 (3), дефект: погоджувач править січень (D-442), подані також лютий і березень. Не-місячний підсумок
    /// змінився — позначку отримують лютий і березень; січень (місяць правки погоджувача) — ні. Контроль: та сама правка не
    /// погоджувачем — позначено всі три.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у <c>NoteSubmittedMonth</c> прибрати умову <c>month == periodKey.Value</c> (як було) — рядок
    /// <c>approverEdit: true</c> червоніє: набір порожній, лютий і березень позначку загубили.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task B79_правка_погоджувача_знімає_позначку_лише_своєму_місяцю_інші_подані_позначено(bool approverEdit)
    {
        var world = await ArrangeAsync();
        try
        {
            await PrepareAnchorRunAsync(world);
            await SubmitAsync(world, Anchor);
            await SubmitAsync(world, 2);
            await SubmitAsync(world, 3);

            var months = new HashSet<(int PeriodKey, int SheetDefId)>();
            await using (var db = world.Builder.CreateContext())
            {
                var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
                var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
                var dirty = new DirtySet();
                dirty.Add(new CellAddress(Key(Anchor), world.Doc.RowIds[0], world.Doc.ColumnDefIds[1]));

                await Formulas(db, world, store, router, Periods(world, States(closed: [])))
                    .RecalculateAsync(
                        world.Doc.TableInstanceId, dirty, CancellationToken.None,
                        approverEditSheets: approverEdit ? new HashSet<int> { world.Doc.SheetDefId } : null,
                        submittedYearWritten: months);
            }

            Assert.Equal(9 * Input, await PhysicalAsync(world, Anchor, world.Total));
            var expected = (approverEdit ? ApproverOtherMonths : AllSubmittedMonths)
                .Select(m => (Period(m), world.Doc.SheetDefId));
            Assert.Equal(expected, months.OrderBy(m => m.PeriodKey));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }
}
