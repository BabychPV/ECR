using Ecr.Application.Common;
using Xunit;

// Не `Tests.Common`: такий простір імен затінює `Common.ICurrentUser` в інших тестах.
namespace Ecr.Application.Tests.Csv;

/// <summary>Спільний розбір CSV (RFC 4180): дзеркало <see cref="CsvFormat"/>.</summary>
public sealed class CsvReaderTests
{
    [Fact]
    public void Поле_в_лапках_зберігає_кому_і_подвоєні_лапки()
    {
        var rows = CsvReader.Parse("\"a,b\",\"say \"\"hi\"\"\"");

        Assert.Equal(["a,b", "say \"hi\""], Assert.Single(rows));
    }

    [Fact]
    public void Перенос_рядка_в_лапках_лишається_в_полі()
    {
        var rows = CsvReader.Parse("\"l1\r\nl2\",x\n\"l3\nl4\",y");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["l1\r\nl2", "x"], rows[0]);
        Assert.Equal(["l3\nl4", "y"], rows[1]);
    }

    [Theory]
    [InlineData("a,b\r\nc,d\r\n")]
    [InlineData("a,b\nc,d\n")]
    [InlineData("a,b\rc,d")]
    public void CRLF_LF_і_CR_однаково_розділяють_записи(string text)
    {
        var rows = CsvReader.Parse(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows[0]);
        Assert.Equal(["c", "d"], rows[1]);
    }

    [Fact]
    public void BOM_на_початку_не_потрапляє_в_перше_поле()
    {
        var rows = CsvReader.Parse("﻿key,value");

        Assert.Equal(["key", "value"], Assert.Single(rows));
    }

    [Fact]
    public void Порожні_поля_зберігаються_на_своїх_місцях()
    {
        var rows = CsvReader.Parse(",x,,\"\"");

        Assert.Equal(["", "x", "", ""], Assert.Single(rows));
    }

    [Fact]
    public void Порожній_текст_дає_нуль_записів()
        => Assert.Empty(CsvReader.Parse(string.Empty));

    [Theory]
    [InlineData("'=1+1", "=1+1")]
    [InlineData("'+1", "+1")]
    [InlineData("'-1", "-1")]
    [InlineData("'@SUM(A1)", "@SUM(A1)")]
    [InlineData("'\tx", "\tx")]
    [InlineData("'\rx", "\rx")]
    [InlineData("'abc", "'abc")]
    [InlineData("'", "'")]
    [InlineData("it's", "it's")]
    [InlineData("=1", "=1")]
    public void UnescapeFormula_знімає_апостроф_лише_перед_формулою(string raw, string expected)
        => Assert.Equal(expected, CsvReader.UnescapeFormula(raw));

    [Theory]
    [InlineData("=1+1")]
    [InlineData("say \"hi\", l1\r\nl2")]
    [InlineData("\tx")]
    public void Формат_і_розбір_взаємно_обернені(string raw)
    {
        var rows = CsvReader.Parse(CsvFormat.Field(raw) + ",z");

        Assert.Equal(raw, CsvReader.UnescapeFormula(Assert.Single(rows)[0]));
    }
}
