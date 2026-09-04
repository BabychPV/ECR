using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// <c>Project.CurrentPeriod</c> — **наша конфігурація** (D-77), а не значення
/// з зовнішньої системи.
/// </summary>
public sealed class ProjectCurrentPeriodTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Новий_проєкт_має_режим_Auto()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Фіксація_періоду_без_причини_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_режимі_Pinned_автоматичне_оновлення_не_змінює_поточний_період()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зняття_фіксації_повертає_режим_Auto()
        => Assert.Fail("not implemented");
}
