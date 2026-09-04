// tests/Ecr.Expressions.Tests/Lexing/LexerTests.cs
using System.Globalization;
using Ecr.Expressions.Lexing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Lexing;

/// <summary>
/// Лексер. Тут вирішується, чи буде `1.5` числом на будь-якій машині: формат
/// **інваріантний**, кома десятковим роздільником не є ніколи.
/// </summary>
public sealed class LexerTests
{
    private static readonly Lexer Lexer = new();

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ідентифікатор_не_починається_з_цифри_поза_дужками()
    {
        // R-B6: EcrCode не може починатися з цифри. Мовчки розбити `7Total` на
        // число і слово означало б прочитати помилку як конкатенацію.
        var error = Assert.Throws<LexicalException>(() => Lexer.Tokenize("7Total + 1"));

        Assert.Equal(0, error.Position);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цифровий_RowKey_допустимий_усередині_квадратних_дужок()
    {
        var tokens = Lexer.Tokenize("[7001001].[Jan]");

        Assert.Collection(
            tokens,
            t => Assert.Equal(TokenType.LBracket, t.Type),
            t =>
            {
                Assert.Equal(TokenType.Identifier, t.Type);
                Assert.Equal("7001001", t.Text);
            },
            t => Assert.Equal(TokenType.RBracket, t.Type),
            t => Assert.Equal(TokenType.Dot, t.Type),
            t => Assert.Equal(TokenType.LBracket, t.Type),
            t => Assert.Equal("Jan", t.Text),
            t => Assert.Equal(TokenType.RBracket, t.Type),
            t => Assert.Equal(TokenType.EndOfInput, t.Type));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Подвоєний_апостроф_дає_один_символ_у_рядку()
    {
        var tokens = Lexer.Tokenize("'it''s'");

        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("it's", tokens[0].Text);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("1.5")]
    [InlineData("0.001")]
    [InlineData("1000000")]
    public void Число_читається_в_інваріантному_форматі(string literal)
    {
        var tokens = Lexer.Tokenize(literal);

        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(literal, tokens[0].Text);
        Assert.Equal(
            decimal.Parse(literal, CultureInfo.InvariantCulture),
            Expr.Number(literal));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кома_не_є_десятковим_роздільником_у_жодній_культурі()
    {
        // ⚠ Перевіряється саме під українською культурою, де десятковий
        // роздільник — кома. Якби лексер спирався на поточну культуру, `1,5`
        // прочиталося б числом «півтора», і те саме число на іншій машині
        // стало б двома аргументами. Тут воно ЗАВЖДИ два.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            var tokens = Lexer.Tokenize("1,5");

            Assert.Equal(TokenType.Number, tokens[0].Type);
            Assert.Equal("1", tokens[0].Text);
            Assert.Equal(TokenType.Comma, tokens[1].Type);
            Assert.Equal("5", tokens[2].Text);

            Assert.Equal(6m, Expr.Number("SUM(1,5)"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
