using System.Globalization;
using Ecr.Scripting.Lexing;
using Ecr.Scripting.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>Закриття відкритого після рецензії мови: тиха втрата точності літерала й однозначне повідомлення про межу foreach.</summary>
public sealed class LanguageGapTests
{
    private static string Zeros(int n) => new('0', n);

    private static ScriptDiagnostic? NumberDiagnostic(string literal)
        => ScriptLexer.Tokenize(literal).Diagnostics.SingleOrDefault(d => d.Code == ScriptDiagnosticCodes.InvalidNumber);

    // ───── втрата точності числового літерала ─────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("0.000000000000000000000000000001")] // 1e-30: раніше мовчки 0
    [InlineData("1e-30")]
    [InlineData("5e-29")]
    [InlineData("1.5e-28")] // 15e-29: дев'ята-двадцята позиція після коми, decimal округлив би до 2e-28
    [InlineData("123e-30")]
    [InlineData("1.23456789012345678901234567891")] // 30 значущих цифр
    [InlineData("0.10000000000000000000000000001")] // нуль-блок і одиниця на 29-й позиції
    [InlineData("7.9228162514264337593543950336")] // на одиницю більше найбільшого decimal за тієї ж шкали
    public void Літерал_що_втратив_би_ненульову_цифру_дає_invalidNumber(string literal)
    {
        var d = NumberDiagnostic(literal);

        Assert.NotNull(d);
        Assert.Equal(literal, d!.Params["text"]);
        Assert.Equal("script.invalidNumber", d.MessageKey);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("1.5", "1.5")]
    [InlineData("1.5000000000000000000000000000000000", "1.5")] // хвостові нулі — не втрата
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")] // 1e-28: найменший ненульовий
    [InlineData("1e-28", "0.0000000000000000000000000001")]
    [InlineData("1.2345678901234567890123456789", "1.2345678901234567890123456789")] // 29 цифр, що вміщаються
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("100000000000000000000000000000e-2", "1000000000000000000000000000")] // порядок змістив хвостові нулі
    [InlineData("0.000", "0")]
    [InlineData("0e-40", "0")] // нуль у будь-якому записі — нуль точно
    [InlineData("0.00000000000000000000000000000000000000000", "0")]
    [InlineData("12.5e1", "125")]
    public void Літерал_без_втрати_точності_приймається_із_точним_значенням(string literal, string expected)
    {
        var lex = ScriptLexer.Tokenize(literal);

        Assert.Empty(lex.Diagnostics);
        var token = lex.Tokens.First(t => t.Kind == ScriptTokenKind.NumberLiteral);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), (decimal)token.Value!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Тиха_втрата_цифр_у_коефіцієнті_методології_повідомляється_при_компіляції()
    {
        var c = T.WithRows($"const K = 0.{Zeros(29)}1; var a = K;");

        var d = Assert.Single(c.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.InvalidNumber, d.Code);
        Assert.Null(c.Script);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Хвостові_нулі_після_30_знаків_компілюються_без_діагностик()
    {
        var c = T.WithRows($"var a = 1.5{Zeros(40)};");

        Assert.Empty(c.Diagnostics);
    }

    // ───── межа foreach: яка саме ─────

    private static ScriptDiagnostic Single(string text)
        => Assert.Single(T.Compile(text).Diagnostics, d => d.Code == ScriptDiagnosticCodes.ForeachTooLarge);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перевищення_глибини_foreach_має_ключ_depth_і_свою_межу()
    {
        var d = Single(
            "rows from T1; foreach A in (1) { foreach B in (1) { foreach C in (1) { foreach D in (1) { var v{A}{B}{C}{D} = 1; } } } }");

        Assert.Equal("script.foreachTooLarge.depth", d.MessageKey);
        Assert.Equal("depth", d.Params["kind"]);
        Assert.Equal(ScriptParser.MaxForeachDepth.ToString(CultureInfo.InvariantCulture), d.Params["limit"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перевищення_числа_операторів_має_ключ_statements_і_межу_2000()
    {
        var values = string.Join(",", Enumerable.Range(1, ScriptParser.MaxExpandedStatements + 1));
        var d = Single($"rows from T1; foreach N in ({values}) {{ var v{{N}} = N; }}");

        Assert.Equal("script.foreachTooLarge.statements", d.MessageKey);
        Assert.Equal("statements", d.Params["kind"]);
        Assert.Equal("2000", d.Params["limit"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перевищення_числа_лексем_має_ключ_tokens_і_межу_200000()
    {
        var values = string.Join(",", Enumerable.Range(1, 100));
        var d = Single("rows from T1; foreach X in (" + values + ") { " + string.Concat(Enumerable.Repeat("a ", 5_000)) + " }");

        Assert.Equal("script.foreachTooLarge.tokens", d.MessageKey);
        Assert.Equal("tokens", d.Params["kind"]);
        Assert.Equal("200000", d.Params["limit"]);
    }

    public static TheoryData<string, string> Languages() => new()
    {
        { "ru", "foreachTooLarge.depth" }, { "kz", "foreachTooLarge.depth" }, { "en", "foreachTooLarge.depth" },
        { "ru", "foreachTooLarge.statements" }, { "kz", "foreachTooLarge.statements" }, { "en", "foreachTooLarge.statements" },
        { "ru", "foreachTooLarge.tokens" }, { "kz", "foreachTooLarge.tokens" }, { "en", "foreachTooLarge.tokens" },
    };

    [Theory]
    [MemberData(nameof(Languages))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сід_має_текст_кожної_межі_foreach_трьома_мовами_з_підстановкою_межі(string lang, string suffix)
    {
        var catalog = lang == "en" ? SeedSql.EnCatalog : SeedSql.Text;
        var marker = $"(N'script.{suffix}', N'{lang}', N'";
        var start = catalog.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, $"немає {marker} у сіді");
        var lineEnd = catalog.IndexOf((char)10, start);
        var text = catalog[start..(lineEnd < 0 ? catalog.Length : lineEnd)];
        Assert.Contains("{limit}", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Старий_неоднозначний_ключ_foreachTooLarge_у_сіді_замінено()
    {
        Assert.DoesNotContain("N'script.foreachTooLarge'", SeedSql.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("N'script.foreachTooLarge'", SeedSql.EnCatalog, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ключ_без_виду_межі_лишається_базовим()
    {
        var d = ScriptDiagnostic.At(ScriptDiagnosticCodes.ForeachTooLarge, new ScriptSpan(0, 1, 1, 1), "x");

        Assert.Equal("script.foreachTooLarge", d.MessageKey);
    }
}
