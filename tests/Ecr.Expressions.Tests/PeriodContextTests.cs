using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Календарна конвенція (D-78).
/// </summary>
/// <remarks>
/// Різниця між режимами на тих самих даних — **3.3 %** (фікстура 02c §7), і
/// виглядає вона як помилка формули, а не як різниця конвенції. Тому тест на
/// обидва режими обов'язковий.
/// </remarks>
public sealed class PeriodContextTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Actual_дає_фактичну_кількість_днів_у_січні()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed360_дає_тридцять_днів_у_будь_якому_місяці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed365_дає_рік_у_365_днів_навіть_у_високосному()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Секунди_періоду_це_дні_помножені_на_86400()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перерахунок_у_грами_за_секунду_відрізняється_між_Actual_і_Fixed360()
        => Assert.Fail("not implemented");
}
