using Ecr.Scripting;
using Ecr.Scripting.Lexing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

public sealed class LexerTests
{
    private static ScriptLexResult Lex(string text) => ScriptLexer.Tokenize(text);

    private static ScriptTokenKind[] Kinds(string text) => [.. Lex(text).Tokens.Select(t => t.Kind)];

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_вид_лексеми_розпізнається()
    {
        var all = Kinds("abc if 1 \"s\" + - * / % == != < <= > >= && || ! ? : ( ) [ ] { } , ; . =");

        Assert.Equal(
        [
            ScriptTokenKind.Identifier, ScriptTokenKind.Keyword, ScriptTokenKind.NumberLiteral, ScriptTokenKind.StringLiteral,
            ScriptTokenKind.Plus, ScriptTokenKind.Minus, ScriptTokenKind.Star, ScriptTokenKind.Slash, ScriptTokenKind.Percent,
            ScriptTokenKind.EqualEqual, ScriptTokenKind.NotEqual, ScriptTokenKind.Less, ScriptTokenKind.LessOrEqual,
            ScriptTokenKind.Greater, ScriptTokenKind.GreaterOrEqual, ScriptTokenKind.AndAnd, ScriptTokenKind.OrOr,
            ScriptTokenKind.Bang, ScriptTokenKind.Question, ScriptTokenKind.Colon, ScriptTokenKind.LParen, ScriptTokenKind.RParen,
            ScriptTokenKind.LBracket, ScriptTokenKind.RBracket, ScriptTokenKind.LBrace, ScriptTokenKind.RBrace,
            ScriptTokenKind.Comma, ScriptTokenKind.Semicolon, ScriptTokenKind.Dot, ScriptTokenKind.Assign,
            ScriptTokenKind.EndOfInput,
        ], all);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("import")] [InlineData("rows")] [InlineData("from")] [InlineData("where")] [InlineData("active")]
    [InlineData("when")] [InlineData("const")] [InlineData("var")] [InlineData("visible")] [InlineData("function")]
    [InlineData("output")] [InlineData("per")] [InlineData("if")] [InlineData("else")] [InlineData("foreach")]
    [InlineData("substance")] [InlineData("in")] [InlineData("reject")] [InlineData("skip")] [InlineData("true")]
    [InlineData("false")] [InlineData("null")] [InlineData("row")] [InlineData("period")] [InlineData("header")]
    [InlineData("key")] [InlineData("entry")] [InlineData("table")]
    public void Ключові_слова_зарезервовані(string word)
    {
        var token = Lex(word).Tokens[0];

        Assert.Equal(ScriptTokenKind.Keyword, token.Kind);
        Assert.Contains(word, ScriptLexer.Keywords);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ідентифікатор_чутливий_до_регістру_і_допускає_цифри_та_підкреслення()
    {
        var tokens = Lex("_a1 Land_Measure_X2 IF").Tokens;

        Assert.Equal(ScriptTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("Land_Measure_X2", tokens[1].Text);
        Assert.Equal(ScriptTokenKind.Identifier, tokens[2].Kind); // IF — не ключове слово
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Коментарі_пропускаються()
    {
        var kinds = Kinds("a // до кінця рядка\n b /* блок\n багаторядковий */ c");

        Assert.Equal(4, kinds.Length);
        Assert.Empty(Lex("a /* x */ // y").Diagnostics);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("123", 123)] [InlineData("1.5", 1.5)] [InlineData("1e6", 1000000)] [InlineData("2.5E-3", 0.0025)]
    [InlineData("0.25", 0.25)] [InlineData("1E+2", 100)]
    public void Числа_розбираються_у_decimal(string text, double expected)
    {
        var lex = Lex(text);

        Assert.Empty(lex.Diagnostics);
        Assert.Equal((decimal)expected, lex.Tokens[0].Value);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(".5")] [InlineData("1.")] [InlineData("1e30")]
    public void Хибні_числа_дають_invalidNumber(string text)
    {
        var lex = Lex(text);

        Assert.Equal(ScriptDiagnosticCodes.InvalidNumber, Assert.Single(lex.Diagnostics).Code);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Унарний_мінус_є_оператором_а_не_частиною_числа()
    {
        var kinds = Kinds("-5");

        Assert.Equal(ScriptTokenKind.Minus, kinds[0]);
        Assert.Equal(ScriptTokenKind.NumberLiteral, kinds[1]);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("\"abc\"", "abc")] [InlineData("'abc'", "abc")] [InlineData("\"a\\\"b\"", "a\"b")]
    [InlineData("'a\\'b'", "a'b")] [InlineData("\"a\\\\b\"", "a\\b")] [InlineData("\"a\\nb\"", "a\nb")]
    public void Рядки_з_екрануванням(string text, string expected)
    {
        var lex = Lex(text);

        Assert.Empty(lex.Diagnostics);
        Assert.Equal(expected, lex.Tokens[0].Value);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Незакритий_рядок_дає_діагностику_і_лексер_іде_далі()
    {
        var lex = Lex("\"abc\nvar");

        Assert.Equal(ScriptDiagnosticCodes.UnterminatedString, Assert.Single(lex.Diagnostics).Code);
        Assert.Contains(lex.Tokens, t => t is { Kind: ScriptTokenKind.Keyword, Text: "var" });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Незакритий_коментар_дає_діагностику()
    {
        var d = Assert.Single(Lex("a /* x").Diagnostics);

        Assert.Equal(ScriptDiagnosticCodes.UnterminatedComment, d.Code);
        Assert.Equal(1, d.Line);
        Assert.Equal(3, d.Column);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Степінь_одинарне_та_і_або_дають_поради()
    {
        var caret = Assert.Single(Lex("a ^ b").Diagnostics);
        var amp = Assert.Single(Lex("a & b").Diagnostics);
        var pipe = Assert.Single(Lex("a | b").Diagnostics);

        Assert.Equal(ScriptDiagnosticCodes.CaretNotPower, caret.Code);
        Assert.Contains("Pow", caret.Message, StringComparison.Ordinal);
        Assert.Equal(ScriptDiagnosticCodes.UnexpectedToken, amp.Code);
        Assert.Equal("&&", amp.Params["advice"]);
        Assert.Equal("||", pipe.Params["advice"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Позиції_рядок_колонка_довжина()
    {
        var tokens = Lex("var x\n  = 12;").Tokens;

        Assert.Equal((1, 1, 3), (tokens[0].Line, tokens[0].Column, tokens[0].Length));
        Assert.Equal((2, 3, 1), (tokens[2].Line, tokens[2].Column, tokens[2].Length));
        Assert.Equal((2, 5, 2), (tokens[3].Line, tokens[3].Column, tokens[3].Length));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Шаблонний_фрагмент_приймається_лише_всередині_ідентифікатора()
    {
        var inside = Lex("ECW_{S}_x").Tokens;
        var block = Lex("else {S}").Tokens;

        Assert.Equal("ECW_{S}_x", inside[0].Text);
        Assert.Equal(ScriptTokenKind.Identifier, inside[0].Kind);
        Assert.Equal(ScriptTokenKind.LBrace, block[1].Kind);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Невідомий_символ_дає_unexpectedToken_і_не_кидає_виняток()
    {
        var d = Assert.Single(Lex("a @ b").Diagnostics);

        Assert.Equal(ScriptDiagnosticCodes.UnexpectedToken, d.Code);
        Assert.Equal("script.unexpectedToken", d.MessageKey);
    }
}
