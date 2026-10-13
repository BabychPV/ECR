// tests/Ecr.Expressions.Tests/Parsing/AbsolutePeriodParsingTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// WW-03: форма <c>[Period:#k]</c> — АБСОЛЮТНИЙ період року (колонка місяця k, як в Excel-файлі).
/// </summary>
/// <remarks>
/// ⛔ Що було: <c>#</c> лексер не знав (<c>expr.lex.invalidCharacter</c>), а відносні зсуви не годяться —
/// колонка січня в періоді m мала б зсув <c>1−m</c>, тобто різна формула в кожному з 12 періодів.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class AbsolutePeriodParsingTests
{
    [Theory]
    [InlineData("[Period:#1].[Main].[7001001].[Total]", 1)]
    [InlineData("[Period:#3].[Main].[7001001].[Total]", 3)]
    [InlineData("[Period:#12].[Main].[7001001].[Total]", 12)]
    [InlineData("[Period:#99].[Total]", 99)]
    public void Абсолютний_період_розбирається_в_посилання_без_зсуву(string text, int expected)
    {
        var result = Expr.Parse(text);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var reference = Assert.IsType<CellReferenceNode>(result.Expression!.Root);
        Assert.Equal(expected, reference.AbsolutePeriod);
        Assert.Equal(0, reference.PeriodOffset);
        Assert.True(reference.IsCrossPeriod);
    }

    [Fact]
    public void Відносний_зсув_не_має_абсолютного_номера()
    {
        var reference = Assert.IsType<CellReferenceNode>(
            Expr.Parse("[Period:-2].[Main].[7001001].[Total]").Expression!.Root);

        Assert.Null(reference.AbsolutePeriod);
        Assert.Equal(-2, reference.PeriodOffset);
        Assert.True(reference.IsCrossPeriod);
    }

    [Fact]
    public void Друк_дерева_повертає_ту_саму_форму()
    {
        var root = Expr.Parse("SUM([Period:#3].[Main].[7001001:7001003].[Total]) / 1000").Expression!.Root;

        var printed = AstPrinter.Print(root);

        Assert.Contains("[Period:#3]", printed, StringComparison.Ordinal);
        Assert.Empty(Expr.Parse(printed).Diagnostics);
    }

    [Theory]
    [InlineData("[Period:#0].[Total]")]
    [InlineData("[Period:#100].[Total]")]
    public void Номер_поза_межами_року_відхиляється(string text)
    {
        var result = Expr.Parse(text);

        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.periodAbsoluteRange");
    }

    [Fact]
    public void Календар_абсолютного_періоду_відхиляється_як_і_календар_зсуву()
    {
        // ⛔ C1-03: календар іншого періоду обчислювач не вміє (`IEvaluationContext.Period` один).
        var result = Expr.Parse("[Period:#3].Days");

        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.calendarContextOffset");
    }

    [Fact]
    public void Діалект_звітів_забороняє_абсолютний_період()
    {
        // G5: звіт не читає інших періодів — як і з відносним зсувом.
        var result = Expr.Parse("[Period:#3].[Value]", ExpressionDialect.Report);

        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Діалект_методології_забороняє_посилання_на_комірки_разом_з_абсолютним_періодом()
    {
        var result = Expr.Parse("[Period:#3].[Main].[7001001].[Total]", ExpressionDialect.Methodology);

        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.cellReferencesForbiddenInMethodology");
    }

    [Fact]
    public void Решітка_поза_Period_лишається_недопустимою()
    {
        var result = Expr.Parse("[Main].[#3].[Total]");

        // Це звичайний (нерезолвний) код рядка, а не період: граматика #k живе лише в `[Period:` .
        var reference = Assert.IsType<CellReferenceNode>(result.Expression!.Root);
        Assert.Null(reference.AbsolutePeriod);
    }
}
