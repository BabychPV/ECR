using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Режим округлення. **Банківське округлення дало б інші числа у звіті**,
/// тому скрізь <c>MidpointRounding.AwayFromZero</c> (02b §7, 02c E12–E13).
/// </summary>
public sealed class RoundingTests
{
    [Theory]
    [InlineData("2.5", 0, "3")]
    [InlineData("3.5", 0, "4")]     // банківське дало б 4 — збіг, тому потрібен наступний випадок
    [InlineData("0.5", 0, "1")]     // банківське дало б 0 — тут різниця видна
    [InlineData("-2.5", 0, "-3")]
    [InlineData("1.2345", 2, "1.23")]
    [InlineData("1.2350", 2, "1.24")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROUND_округлює_від_нуля_а_не_до_парного(string value, int digits, string expected)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Обчислення_ведеться_в_decimal_і_не_втрачає_точності()
        => Assert.Fail("not implemented");
}
