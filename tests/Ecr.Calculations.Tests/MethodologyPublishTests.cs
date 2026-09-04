using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Публікація версії методології — **найнебезпечніша операція в системі**:
/// вона тихо змінює числа у вже поданих формах (ФВ-9.6).
/// </summary>
public sealed class MethodologyPublishTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_автором_останньої_правки_відхиляється_ECR_CALC_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_причини_зміни_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_дати_набуття_чинності_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_формує_diff_РЕЗУЛЬТАТІВ_а_не_diff_коду()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Закриті_періоди_після_публікації_НЕ_перераховуються_автоматично()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перерахунок_закритого_періоду_потребує_окремого_погодження()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Топологічний_порядок_формул_обчислюється_при_публікації()
        => Assert.Fail("not implemented");
}
