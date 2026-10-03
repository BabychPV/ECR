using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Аудит L7-05: <c>AcceptsRange: false</c> у сигнатурі нарешті перевіряється — і
/// публікацією, і рушієм.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>ROUND([T].[r1:r3].[X], 2)</c> проходив публікацію, а рушій
/// фленив групи аргументів у спільний список і рахував <c>ROUND(v1, v2)</c> —
/// тихо хибне число; <c>ABS</c> над порожнім предикатом звертався до
/// <c>args[0]</c> порожнього списку → ArgumentOutOfRangeException і впалий
/// перерахунок.
/// </remarks>
public sealed class RangeNotAcceptedTests
{
    private static List<ExpressionDiagnostic> Check(string expression)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        new TypeChecker().Check(parsed.Expression!.Root, new TestBindingContext(), diagnostics);
        return diagnostics;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("ROUND([Main].[r1:r3].[Amount], 2)")]
    [InlineData("ABS([Main].[r1:r3].[Amount])")]
    [InlineData("IF(TRUE, [Main].[r1:r3].[Amount], 0)")]
    [InlineData("ROUND([Main].[WHERE [Flag] = 1].[Amount], 2)")]
    public void Діапазон_у_функції_без_AcceptsRange_відхиляється_публікацією(string expression)
    {
        var diagnostics = Check(expression);

        var diagnostic = Assert.Single(diagnostics, d => d.MessageKey == "expr.rangeNotAccepted");
        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("SUM([Main].[r1:r3].[Amount])")]
    [InlineData("ROUND(SUM([Main].[r1:r3].[Amount]), 2)")]
    [InlineData("ROUND([Main].[r1].[Amount], 2)")]
    public void Діапазон_в_агрегаті_й_одиничне_посилання_не_відхиляються(string expression)
        => Assert.DoesNotContain(Check(expression), d => d.MessageKey == "expr.rangeNotAccepted");

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("ABS", 0)]
    [InlineData("ABS", 2)]
    [InlineData("ROUND", 3)]
    [InlineData("IF", 0)]
    public void Рушій_дає_VALUE_для_групи_не_з_одного_значення(string function, int firstGroupSize)
    {
        var first = Enumerable.Repeat(ExpressionValue.Number(1.5m), firstGroupSize).ToList();
        IReadOnlyList<ExpressionValue> one = [ExpressionValue.Number(2m)];
        var groups = function switch
        {
            "ABS" => new List<IReadOnlyList<ExpressionValue>> { first },
            "ROUND" => [first, one],
            _ => [first, one, one],
        };

        var value = new FunctionRegistry().Invoke(function, groups, new TestEvaluationContext());

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рушій_рахує_скалярні_аргументи_як_раніше()
    {
        IReadOnlyList<ExpressionValue> value = [ExpressionValue.Number(1.25m)];
        IReadOnlyList<ExpressionValue> digits = [ExpressionValue.Number(1m)];

        Assert.Equal(1.3m, new FunctionRegistry().Invoke("ROUND", [value, digits], new TestEvaluationContext()).AsNumber());
    }
}
