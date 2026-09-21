using Ecr.Application.Audit;
using Ecr.Application.Common;
using Xunit;

namespace Ecr.Application.Tests.Audit;

/// <summary>`BE-16`: поле CSV за RFC 4180 і захист від формул.</summary>
public sealed class CsvFormatTests
{
    [Theory]
    [Trait("Directive", "BE-16")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("\rx", "\"'\rx\"")]
    [InlineData("a=b", "a=b")]
    public void Значення_що_починається_з_формули_стає_текстом(string raw, string expected)
        => Assert.Equal(expected, CsvFormat.Field(raw));

    [Theory]
    [Trait("Directive", "BE-16")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("l1\r\nl2", "\"l1\r\nl2\"")]
    [InlineData("l1\nl2", "\"l1\nl2\"")]
    [InlineData("plain", "plain")]
    [InlineData(null, "")]
    public void Кома_лапки_й_переноси_беруться_в_лапки(string? raw, string expected)
        => Assert.Equal(expected, CsvFormat.Field(raw));

    [Fact]
    [Trait("Directive", "BE-16")]
    public void Рядок_закінчується_CRLF_а_стеля_за_замовчуванням_сто_тисяч()
    {
        Assert.Equal("a,,\"b,c\"\r\n", CsvFormat.Row("a", null, "b,c"));

        // Число — вимога, не похідна від константи.
        Assert.Equal(100_000, ExportStructureChangesHandler.DefaultMaxRows);
    }
}
