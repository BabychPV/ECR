using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Явна обробка порожнього у виразі правила категорії (L-2, <c>.sync-local/DESIGN-category-rule.md</c>):
/// <c>NULL + 'a'</c> = Null, тож порожній операнд захищається гілкою <c>if(!X = NULL, '', !X)</c>.
/// </summary>
/// <remarks>
/// Діалект Methodology: <c>if</c> — малими літерами (регістр значущий), <c>NULL</c> — літерал,
/// рівність із ним дозволена (<c>null = null</c> — TRUE), <c>&amp;</c> відхиляється. <c>COALESCE</c>,
/// <c>IFNULL</c>, <c>ISBLANK</c> у каталозі немає.
/// </remarks>
public sealed class TextPlusNullHandlingTests
{
    private const ExpressionDialect Dialect = ExpressionDialect.Methodology;

    /// <summary>Формула 5.1 з явним захистом кожного операнда: порожнє → ''.</summary>
    private const string Guarded =
        "if(!ECW_RepairStatus = NULL, '', !ECW_RepairStatus) + '_' + if(!ECW_Category = NULL, '', !ECW_Category)";

    /// <summary>Те саме, але порожнім вважається і Null, і ''.</summary>
    private const string GuardedNullOrEmpty =
        "if(!ECW_RepairStatus = NULL or !ECW_RepairStatus = '', 'NA', !ECW_RepairStatus) + '_' "
        + "+ if(!ECW_Category = NULL or !ECW_Category = '', 'NA', !ECW_Category)";

    private static TestEvaluationContext Fields(string? status, string? category)
    {
        var context = new TestEvaluationContext();
        context.FormulaResults["ECW_RepairStatus"] = status is null ? ExpressionValue.Null : ExpressionValue.Text(status);
        context.FormulaResults["ECW_Category"] = category is null ? ExpressionValue.Null : ExpressionValue.Text(category);
        return context;
    }

    [Theory]
    [InlineData("Repair", "Cat1", "Repair_Cat1")]
    [InlineData(null, "Cat1", "_Cat1")]
    [InlineData("Repair", null, "Repair_")]
    [InlineData(null, null, "_")]
    public void Захищений_вираз_5_1_не_поглинається_Null(string? status, string? category, string expected)
    {
        var parsed = Expr.Parse(Guarded, Dialect);
        Assert.True(parsed.IsSuccess);

        // ⚠ Статичний TypeChecker бачить Text; груба оцінка ParsedExpression.ResultType (InferShape) дає Number —
        // див. DESIGN-category-rule.md (categoryRuleNotText).
        var diagnostics = new List<ExpressionDiagnostic>();
        var type = new TypeChecker().Check(parsed.Expression!.Root, new TestBindingContext(), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(ExpressionValueType.Text, type);

        var value = Expr.Eval(Guarded, Fields(status, category), Dialect);

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal(expected, value.Value);
    }

    [Theory]
    [InlineData("Repair", "Cat1", "Repair_Cat1")]
    [InlineData(null, "Cat1", "NA_Cat1")]
    [InlineData("", "Cat1", "NA_Cat1")]
    [InlineData("Repair", "", "Repair_NA")]
    [InlineData(null, null, "NA_NA")]
    public void Захист_покриває_і_Null_і_порожній_рядок(string? status, string? category, string expected)
    {
        var value = Expr.Eval(GuardedNullOrEmpty, Fields(status, category), Dialect);

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal(expected, value.Value);
    }

[Fact]
    public void Без_захисту_Null_поглинає_вираз()
    {
        Assert.True(Expr.Eval("!ECW_RepairStatus + '_' + !ECW_Category", Fields(null, "Cat1"), Dialect).IsNull);
    }

    [Theory]
    [InlineData("!A & !B")]
    [InlineData("COALESCE(!A, '')")]
    [InlineData("IFNULL(!A, '')")]
    [InlineData("ISBLANK(!A)")]
    public void Інших_засобів_у_діалекті_немає(string expression)
    {
        Assert.False(Expr.Parse(expression, Dialect).IsSuccess);
    }
}
