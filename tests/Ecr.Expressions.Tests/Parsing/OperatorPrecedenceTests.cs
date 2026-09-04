using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>Пріоритети операторів за таблицею 02b §2.</summary>
public sealed class OperatorPrecedenceTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("-2 ^ 2", -4)]          // унарний мінус слабший за степінь
    [InlineData("2 * 3 % 4", 2)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Арифметика_обчислюється_за_пріоритетами(string expr, int expected)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Степінь_правоасоціативний_2_у_3_у_2_дорівнює_512()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкатенація_слабша_за_додавання()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_слабше_за_конкатенацію()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AND_сильніший_за_OR()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Одинарне_і_подвійне_дорівнює_це_синоніми_порівняння()
        => Assert.Fail("not implemented");
}
