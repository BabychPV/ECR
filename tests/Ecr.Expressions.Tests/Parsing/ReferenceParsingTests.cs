// tests/Ecr.Expressions.Tests/Parsing/ReferenceParsingTests.cs
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>Чотири скорочені форми посилання, плейсхолдер місяця і крос-період.</summary>
public sealed class ReferenceParsingTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("[Jan]")]
    [InlineData("[7001001].[Jan]")]
    [InlineData("[Main].[7001001].[Jan]")]
    [InlineData("[Water_07].[Main].[7001001].[Jan]")]
    public void Чотири_форми_посилання_розбираються(string expression)
    {
        var result = Expr.Parse(expression);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var reference = Assert.IsType<CellReferenceNode>(result.Expression!.Root);
        Assert.Equal("Jan", reference.ColumnSelector);

        // Скорочення не вигадує кодів: чого в тексті немає, того немає і в
        // дереві. Доповнює їх РЕЗОЛВЕР із контексту формули, а не парсер.
        var parts = expression.Split('.').Length;
        Assert.Equal(parts >= 4 ? "Water_07" : null, reference.SheetCode);
        Assert.Equal(parts >= 3 ? "Main" : null, reference.TableCode);

        if (parts >= 2)
        {
            Assert.Equal("7001001", Assert.IsType<RowSelector.Single>(reference.Row).RowKey);
        }
        else
        {
            Assert.IsType<RowSelector.Current>(reference.Row);
        }
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Плейсхолдер_Month_підставляє_колонку_місяця()
    {
        var context = new TestEvaluationContext { CurrentMonthColumn = "Feb" };
        context.SetCell("S", "T", "R1", "Jan", ExpressionValue.Number(10m));
        context.SetCell("S", "T", "R1", "Feb", ExpressionValue.Number(20m));
        context.CurrentRow = "R1";

        // Той самий текст формули дає різні комірки залежно від колонки, у якій
        // він обчислюється — інакше довелося б писати дванадцять формул.
        Assert.Equal(20m, Expr.Number("[{Month}]", context));

        context.CurrentMonthColumn = "Jan";
        Assert.Equal(10m, Expr.Number("[{Month}]", context));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_період_розбирається_з_від_ємним_і_додатним_зсувом()
    {
        var back = Assert.IsType<CellReferenceNode>(
            Expr.Parse("[Period:-1].[Main].[7001001].[Total]").Expression!.Root);
        var forward = Assert.IsType<CellReferenceNode>(
            Expr.Parse("[Period:+1].[Main].[7001001].[Total]").Expression!.Root);
        var current = Assert.IsType<CellReferenceNode>(
            Expr.Parse("[Period].[Main].[7001001].[Total]").Expression!.Root);

        Assert.Equal(-1, back.PeriodOffset);
        Assert.Equal(1, forward.PeriodOffset);
        Assert.Equal(0, current.PeriodOffset);
        Assert.Equal("Main", back.TableCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вихід_за_межі_проєкту_дає_null_а_не_помилку()
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "Main", "7001001", "Total", ExpressionValue.Number(5m));

        // Січень не має попереднього місяця. Це нормальна ситуація, а не збій:
        // помилка тут змусила б автора формули писати IFERROR довкола кожного
        // крос-періодного посилання.
        var value = Expr.Eval("[Period:-1].[Main].[7001001].[Total]", context);

        Assert.True(value.IsNull);
        Assert.False(value.IsError);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діапазон_рядків_розбирається_у_список_ключів()
    {
        var reference = Assert.IsType<CellReferenceNode>(
            Expr.Parse("SUM([Main].[7001001:7001003].[Jan])").Expression!.Root
                is FunctionNode f ? f.Arguments[0] : null);

        var range = Assert.IsType<RowSelector.Range>(reference.Row);
        Assert.Equal("7001001", range.FromRowKey);
        Assert.Equal("7001003", range.ToRowKey);
    }
}
