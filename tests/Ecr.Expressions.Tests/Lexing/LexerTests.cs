// tests/Ecr.Expressions.Tests/Lexing/LexerTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Lexing;

/// <summary>
/// Лексер. Тут вирішується, чи буде `1.5` числом на будь-якій машині: формат
/// **інваріантний**, кома десятковим роздільником не є ніколи.
/// </summary>
public sealed class LexerTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ідентифікатор_не_починається_з_цифри_поза_дужками()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цифровий_RowKey_допустимий_усередині_квадратних_дужок()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Подвоєний_апостроф_дає_один_символ_у_рядку()
        => Assert.Fail("not implemented");

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("1.5")]
    [InlineData("0.001")]
    [InlineData("1000000")]
    public void Число_читається_в_інваріантному_форматі(string literal)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кома_не_є_десятковим_роздільником_у_жодній_культурі()
        => Assert.Fail("not implemented");
}
