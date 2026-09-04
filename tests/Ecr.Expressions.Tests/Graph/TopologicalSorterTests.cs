using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Graph;

/// <summary>
/// Цикл — помилка **публікації**, а не тихо неправильне число в проді
/// (ФВ-9.4).
/// </summary>
public sealed class TopologicalSorterTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Залежності_обчислюються_раніше_за_залежні_формули()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_із_двох_формул_виявляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_із_трьох_формул_виявляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Результат_містить_шлях_циклу_а_не_лише_прапорець()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Самопосилання_формули_це_цикл()
        => Assert.Fail("not implemented");
}
