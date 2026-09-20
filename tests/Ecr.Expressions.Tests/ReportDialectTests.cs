// tests/Ecr.Expressions.Tests/ReportDialectTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Третій діалект — <c>Report</c> (<c>02b</c> §8a, <c>D-52a</c>): та сама мова, але
/// видно з неї лише колонки свого рядка і параметри звіту.
/// </summary>
/// <remarks>
/// ⚠ Доказ, що граматика решти діалектів ціла, — НЕ тут: це чинні тести
/// <c>Template</c>/<c>Methodology</c>, які лишились зеленими без жодної правки.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class ReportDialectTests
{
    private static readonly ReportExpressionScope Scope = new(
        [
            new("Value", ExpressionValueType.Number),
            new("UnitCode", ExpressionValueType.Text),
        ],
        [new("Threshold", ExpressionValueType.Number)]);

    [Theory]
    [InlineData("IF([Value] < 0, 0, [Value])", ExpressionValueType.Number)]
    [InlineData("[UnitCode] = 't' AND @Threshold > 0", ExpressionValueType.Boolean)]
    [InlineData("[Value] > @threshold ? ROUND([Value], 2) : NULL", ExpressionValueType.Number)]
    [InlineData("in([UnitCode], 't', 'kg')", ExpressionValueType.Boolean)]
    [InlineData("[UnitCode] & ' / ' & MAX(ABS([Value]), 1)", ExpressionValueType.Text)]
    public void Колонка_рядка_і_параметр_звіту_прив_язуються_і_дають_тип(string text, ExpressionValueType expected)
    {
        var diagnostics = new List<ExpressionDiagnostic>();

        var actual = ReportExpressionChecker.Check(Parsed(text), Scope, expected, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("[Nope] > 1", "Nope")]
    [InlineData("[value] > 1", "value")]          // коди колонок — з урахуванням регістру
    public void Невідома_колонка_це_помилка_прив_язки(string text, string column)
    {
        var diagnostic = Assert.Single(Check(text));

        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
        Assert.Contains($"«{column}»", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Невідомий_параметр_це_помилка_прив_язки()
    {
        var diagnostic = Assert.Single(Check("[Value] > @Limit"));

        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
        Assert.Contains("«@Limit»", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Main].[7001001].[Jan]")]
    [InlineData("[Water_07].[Main].[7001001:7001005].[Jan] > 0")]
    [InlineData("[Period:-1].[Main].[7001001].[Total]")]
    [InlineData("[Period].Days")]
    public void Посилання_на_комірку_документа_і_на_період_заборонені(string text)
    {
        // ⛔ Інакше правило звіту почало б рахувати показник (`ФВ-10.3`).
        var result = Expr.Parse(text, ExpressionDialect.Report);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == ExpressionErrors.Unresolved && d.MessageKey == "expr.referenceForbiddenInReport");
    }

    [Fact]
    public void Рядок_у_подвійних_лапках_не_приймається_мова_та_сама()
    {
        // Нового синтаксису діалект не заводить: літерал — в одинарних лапках (02b §1).
        var result = Expr.Parse("[UnitCode] = \"t\"", ExpressionDialect.Report);

        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.lex.invalidCharacter");
    }

    [Theory]
    [InlineData("HDR.Train = 'A'")]
    [InlineData("CST.EF_CO2 * [Value]")]
    [InlineData("1 + !BaseEmission")]
    public void Шапка_константи_і_формули_методології_заборонені(string text)
    {
        var result = Expr.Parse(text, ExpressionDialect.Report);

        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.referenceForbiddenInReport");
    }

    [Theory]
    [InlineData("SUM([Value])")]
    [InlineData("AVERAGE([Value], 1)")]
    [InlineData("COUNT([Value])")]
    [InlineData("PRODUCT([Value], 2)")]
    [InlineData("SUMIF([Value], [Value] > 0)")]
    [InlineData("CONVERT([Value], 't', 'kg')")]
    [InlineData("NOW()")]
    [InlineData("Sqrt([Value])")]
    public void Агрегати_CONVERT_і_час_поза_набором_діалекту(string text)
    {
        var result = Expr.Parse(text, ExpressionDialect.Report);

        Assert.False(result.IsSuccess);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ExpressionErrors.Syntax, diagnostic.Code);
        Assert.Equal("expr.unknownFunction", diagnostic.MessageKey);
    }

    [Theory]
    [InlineData("[Value]", ExpressionValueType.Boolean)]              // умова `when` мусить бути булевою
    [InlineData("[UnitCode] > 1", ExpressionValueType.Boolean)]       // порівняння різних типів
    [InlineData("[UnitCode] + 1", null)]                              // текст в арифметиці
    public void Невідповідність_типів_це_помилка_а_не_здогад(string text, ExpressionValueType? expected)
    {
        var diagnostic = Assert.Single(Check(text, expected));

        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
    }

    [Theory]
    [InlineData("[UnitCode] = 't' AND [Value] > @Threshold", true)]
    [InlineData("[UnitCode] = 'kg' OR [Value] < 0", false)]
    [InlineData("in([UnitCode], 'kg', 't')", true)]
    [InlineData("0.1 + 0.2 = 0.3", true)]                             // decimal: у double це FALSE
    public void Умова_над_текстовою_і_числовою_колонкою(string text, bool expected)
    {
        var value = Evaluate(text, Row(value: 12.5m, unit: "t"));

        Assert.Equal(ExpressionValue.Boolean(expected), value);
    }

    [Fact]
    public void Значення_правила_буває_числом_і_текстом()
    {
        const string Rule = "IF([Value] < 0, 'нижче нуля', [UnitCode])";

        Assert.Equal(ExpressionValue.Text("нижче нуля"), Evaluate(Rule, Row(value: -1m, unit: "t")));
        Assert.Equal(ExpressionValue.Text("t"), Evaluate(Rule, Row(value: 1m, unit: "t")));

        // Strict: округлення ВІД нуля, не банківське (у Legacy було б 2).
        Assert.Equal(ExpressionValue.Number(3m), Evaluate("ROUND([Value], 0)", Row(value: 2.5m, unit: "t")));
    }

    [Fact]
    public void Порожня_колонка_поводиться_за_правилами_null_мови()
    {
        var row = Row(value: null, unit: null);

        // 02b §6.2 — жодного власного правила в діалекту немає.
        Assert.Equal(ExpressionValue.Boolean(true), Evaluate("[Value] = NULL", row));
        Assert.Equal(ExpressionValue.Boolean(false), Evaluate("[Value] = 0", row));
        Assert.Equal(ExpressionValue.Null, Evaluate("[Value] > 1", row));
        Assert.Equal(ExpressionValue.Null, Evaluate("[Value] * 0", row));
        Assert.Equal(ExpressionValue.Text("x"), Evaluate("[UnitCode] & 'x'", row));
    }

    [Fact]
    public void Чого_в_рядку_немає_те_помилка_значення_а_не_null()
    {
        var row = new ReportRowContext([new("Value", 1m)], []);

        Assert.Equal(ExpressionErrors.BadReference, Evaluate("[UnitCode]", row).ErrorCode);
        Assert.Equal(ExpressionErrors.ArgumentNotFound, Evaluate("@Threshold", row).ErrorCode);
    }

    [Fact]
    public void Бюджет_той_самий_і_IFERROR_його_не_ловить()
    {
        // 20 001 аргумент — на один більше за межу кроків (02b §6.5).
        var heavy = $"MAX({string.Join(',', Enumerable.Repeat("1", 20_001))})";
        var row = Row(value: 1m, unit: "t");

        Assert.Equal("#BUDGET", Evaluate(heavy, row).ErrorCode);
        Assert.Equal("#BUDGET", Evaluate($"IFERROR({heavy}, 0)", row).ErrorCode);

        // Та сама IFERROR звичайну помилку ловить — отже, виняток саме для бюджету.
        Assert.Equal(ExpressionValue.Number(0m), Evaluate("IFERROR([Value] / 0, 0)", row));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static ParsedExpression Parsed(string text)
    {
        var result = Expr.Parse(text, ExpressionDialect.Report);
        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.Expression!;
    }

    private static List<ExpressionDiagnostic> Check(string text, ExpressionValueType? expected = null)
    {
        var diagnostics = new List<ExpressionDiagnostic>();
        ReportExpressionChecker.Check(Parsed(text), Scope, expected, diagnostics);
        return diagnostics;
    }

    private static ExpressionValue Evaluate(string text, ReportRowContext row)
        => ReportEvaluation.Evaluate(Parsed(text), row);

    private static ReportRowContext Row(decimal? value, string? unit)
        => new(
            [new("Value", value), new("UnitCode", unit)],
            [new("Threshold", 10m)]);
}
