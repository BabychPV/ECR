using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Звірка розрахунків із очікуваними числами фікстури — **до останнього
/// знака** (ФВ-9.9).
/// </summary>
/// <remarks>
/// Це прообраз задачі <c>golden-compare</c> Етапу 5, тільки на синтетичних
/// даних. Якщо тут числа не сходяться, на реальному еталоні вони не зійдуться
/// й поготів.
/// </remarks>
public sealed class GoldenCalculationTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_для_ХСК_збігаються_з_очікуваним_значенням_фікстури()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тонни_для_завислих_речовин_збігаються_з_очікуваним()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Грами_за_секунду_у_режимі_Actual_збігаються_з_очікуваним()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Грами_за_секунду_у_режимі_Fixed360_відрізняються_на_три_відсотки()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розрахунок_виконується_для_кожної_речовини_методології()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Константа_резолвиться_за_речовиною_а_не_береться_перша_ліпша()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Результати_пишуться_в_calc_а_не_в_doc_CellValue()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Трейс_у_режимі_Off_не_пишеться_взагалі()
        => Assert.Fail("not implemented");
}
