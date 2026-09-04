using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Переходи станів періоду і, окремо, **пояс майданчика**: межі рахуються не
/// в UTC, інакше «останній день періоду» настає для користувача в інший час
/// (D-68).
/// </summary>
public sealed class PeriodStateCalculatorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void До_дати_відкриття_період_у_стані_Scheduled()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Усередині_періоду_стан_Open()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_завершення_періоду_і_до_HardClose_стан_Grace()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_HardClose_стан_Closed()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_повертає_період_у_Grace_до_вказаного_моменту()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Межі_рахуються_у_поясі_майданчика_а_не_в_UTC()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Опівночі_за_поясом_майданчика_період_ще_відкритий_хоча_в_UTC_уже_наступна_доба()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_у_режимі_Auto_це_найраніший_Open()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Якщо_відкритих_немає_поточним_стає_найпізніший_Grace()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Якщо_немає_ні_Open_ні_Grace_поточного_періоду_немає()
        => Assert.Fail("not implemented");
}
