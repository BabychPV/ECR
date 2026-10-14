using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// LAND-5094 DIAG-A (C12_03): колонка-міст Formula над Lookup зберігає в комірці id запису довідника
/// (<c>ValueNumeric</c> 189/190), тож у формулу аргумент приходить як <c>Number(id)</c>. Порівняння з текстом
/// запису (<c>@X = 'No - Нет'</c>) тоді ніколи не істинне, а <c>REGFIELD(@X, 'NAME') = 'No - Нет'</c> - істинне.
/// Тест фіксує обидві поведінки, щоб правка виразу V3 не розійшлася з рушієм.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути у виразі V3 прямий <c>@X = 'No - Нет'</c> - червоніє
/// <see cref="REGFIELD_NAME_дає_гілку_нуль_для_No_і_ненульове_для_Yes"/>.
/// </remarks>
public sealed class LookupBridgeTextCompareTests
{
    private const string Yes = "Yes - Да";
    private const string No = "No - Нет";

    private static ExpressionValue Eval(string expression, long entryId, NumericMode mode)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var context = new TestEvaluationContext();
        context.Arguments["Land_Measure_ReleaseColdVent"] = ExpressionValue.Number(entryId);
        context.SetRegistryField(189, "NAME", ExpressionValue.Text(Yes));
        context.SetRegistryField(190, "NAME", ExpressionValue.Text(No));

        IEvaluationArithmetic arithmetic = mode == NumericMode.Strict
            ? new StrictDecimalArithmetic()
            : new LegacyDoubleArithmetic();
        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Methodology);
    }

    [Theory]
    [InlineData(NumericMode.Legacy)]
    [InlineData(NumericMode.Strict)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_NAME_дає_гілку_нуль_для_No_і_ненульове_для_Yes(NumericMode mode)
    {
        const string fixedExpr = "if (REGFIELD(@Land_Measure_ReleaseColdVent, 'NAME') = 'No - Нет', 0, 5)";

        Assert.Equal(0m, Eval(fixedExpr, 190, mode).AsNumber());
        Assert.Equal(5m, Eval(fixedExpr, 189, mode).AsNumber());
    }

    [Theory]
    [InlineData(NumericMode.Legacy)]
    [InlineData(NumericMode.Strict)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Пряме_порівняння_id_запису_з_текстом_не_бере_гілку_нуль(NumericMode mode)
    {
        // Це і є дефект V1/V2: Number(190) = 'No - Нет' - FALSE, рахується гілка «else».
        const string legacyExpr = "if (@Land_Measure_ReleaseColdVent = 'No - Нет', 0, 5)";

        Assert.Equal(5m, Eval(legacyExpr, 190, mode).AsNumber());
    }
}
