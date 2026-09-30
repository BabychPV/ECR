using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// У діалектах <c>Template</c> і <c>Report</c> впорядкування (<c>&lt;</c>, <c>&lt;=</c>,
/// <c>&gt;</c>, <c>&gt;=</c>) рахується в тій самій числовій семантиці, що й рівність, —
/// <see cref="decimal"/> (продовження аудиту A6).
/// </summary>
/// <remarks>
/// ⛔ Що було. Після A6 діалект методологій порівнював усе через
/// <see cref="IEvaluationArithmetic.CompareNumbers"/>, а шаблони і звіти — рівність у
/// <see cref="decimal"/>, впорядкування в <see cref="double"/>. Для
/// <c>1.0000000000000001</c> проти <c>1</c> одночасно виходило <c>&gt;=</c>, <c>&lt;=</c>
/// і <c>&lt;&gt;</c> при <c>NOT &gt;</c>. Рішення координатора: сервер — джерело
/// істини, тож і тут <c>decimal</c>; клієнтська підказка (<c>evaluate.ts</c>, JS
/// <c>number</c>) — наближення, розбіжність названа винятками у
/// <c>expression-equivalence.json</c>.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class TemplateComparisonIsDecimalTests
{
    private static bool Bool(ExpressionValue value)
    {
        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        return (bool)value.Value!;
    }

    private static bool Template(string expression) => Bool(Expr.Eval(expression));

    private static bool Report(string expression, decimal value, decimal threshold)
    {
        var parsed = Expr.Parse(expression, ExpressionDialect.Report);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var row = new ReportRowContext(
            [new KeyValuePair<string, object?>("Value", value)],
            [new KeyValuePair<string, object?>("Threshold", threshold)]);

        return Bool(ReportEvaluation.Evaluate(parsed.Expression!, row));
    }

    [Fact]
    [Trait("Requirement", "ФВ-9.9")]
    public void Template_число_за_межею_double_більше_а_не_рівне()
    {
        // У `double` обидва літерали — рівно 1, у `decimal` — ні.
        Assert.True(Template("1.0000000000000001 > 1"));
        Assert.True(Template("1.0000000000000001 >= 1"));
        Assert.False(Template("1.0000000000000001 <= 1"));
        Assert.False(Template("1.0000000000000001 < 1"));
        Assert.False(Template("1.0000000000000001 = 1"));
        Assert.True(Template("1.0000000000000001 <> 1"));
    }

    [Theory]
    [Trait("Requirement", "ФВ-9.9")]
    [InlineData("1.0000000000000001", "1")]
    [InlineData("123456.0000000000001", "123456")]
    [InlineData("0.30000000000000001", "0.3")]
    [InlineData("-5.00000000000000001", "-5")]
    [InlineData("1", "1.0000000000000001")]
    [InlineData("7", "7.000")] // рівні з різним масштабом
    [InlineData("2.5", "10")]
    public void Template_оператори_порівняння_не_суперечать_один_одному(string left, string right)
    {
        // ⚠ Трихотомія: рівно одне з `<`, `=`, `>` істинне, а `<=`, `>=`, `<>`
        // узгоджені з ними — і відповідь та сама, що в `decimal.CompareTo`.
        var less = Template($"{left} < {right}");
        var equal = Template($"{left} = {right}");
        var greater = Template($"{left} > {right}");

        Assert.Equal(1, (less ? 1 : 0) + (equal ? 1 : 0) + (greater ? 1 : 0));
        Assert.Equal(less || equal, Template($"{left} <= {right}"));
        Assert.Equal(greater || equal, Template($"{left} >= {right}"));
        Assert.Equal(!equal, Template($"{left} <> {right}"));

        var order = decimal.Parse(left, CultureInfo.InvariantCulture)
            .CompareTo(decimal.Parse(right, CultureInfo.InvariantCulture));
        Assert.Equal(order < 0, less);
        Assert.Equal(order > 0, greater);
    }

    [Theory]
    [Trait("Requirement", "ФВ-9.9")]
    [InlineData("123456.0000000000001", "123456")] // поріг звіту на межі double
    [InlineData("0.30000000000000001", "0.3")]
    [InlineData("-5", "-5.00000000000000001")]
    [InlineData("7", "7.000")]
    public void Report_колонка_проти_параметра_впорядковується_в_decimal(string value, string threshold)
    {
        // Дані рядка зрізу й параметр звіту — не літерали: саме так звичайне
        // правило `[Value] >= @Threshold` доходить до межі точності.
        var a = decimal.Parse(value, CultureInfo.InvariantCulture);
        var b = decimal.Parse(threshold, CultureInfo.InvariantCulture);

        var less = Report("[Value] < @Threshold", a, b);
        var equal = Report("[Value] = @Threshold", a, b);
        var greater = Report("[Value] > @Threshold", a, b);

        Assert.Equal(1, (less ? 1 : 0) + (equal ? 1 : 0) + (greater ? 1 : 0));
        Assert.Equal(less || equal, Report("[Value] <= @Threshold", a, b));
        Assert.Equal(greater || equal, Report("[Value] >= @Threshold", a, b));
        Assert.Equal(!equal, Report("[Value] <> @Threshold", a, b));

        Assert.Equal(a.CompareTo(b) < 0, less);
        Assert.Equal(a.CompareTo(b) > 0, greater);
    }
}
