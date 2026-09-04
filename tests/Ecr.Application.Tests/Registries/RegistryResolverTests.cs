using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Темпоральний резолвінг «станом на дату **періоду**», а не «на сьогодні» —
/// різниця стає видимою, коли звіт за минулий рік перераховують цього року.
/// </summary>
public sealed class RegistryResolverTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_чинний_на_дату_періоду_потрапляє_у_список()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_що_втратив_чинність_до_періоду_не_потрапляє()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дозвіл_що_набуде_чинності_пізніше_не_потрапляє()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Резолвінг_робиться_на_дату_періоду_а_не_на_поточну_дату()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Каскад_звужує_список_водних_обєктів_за_обраним_дозволом()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Запис_на_який_посилаються_дані_не_видаляється_ECR_REG_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Зміна_записів_інкрементує_ревізію_даних_реєстру()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перемикання_master_у_відкритому_періоді_відхиляється_ECR_REG_0422()
        => Assert.Fail("not implemented");
}
