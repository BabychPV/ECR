// tests/Ecr.Application.Tests/Calculations/IntegerMaxMinPublishChecksTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит 2026-10-09c, C1-01 (HU-14 Q1): попередження публікації <c>Legacy</c> про
/// <c>Max</c>/<c>Min</c> із цілим першим аргументом.
/// </summary>
/// <remarks>
/// ⛔ Що було. NCalc 1.3.8 при цілому першому аргументі округлює другий до цілого
/// (<c>Max(0, 12.7) = 13</c>), ECR — ні (<c>12.7</c>). Публікація мовчала, і
/// розбіжність із поданими формами на цілі одиниці ніхто не бачив до звірки.
/// Семантика не змінюється (рішення за замовчуванням) — лише попередження.
/// </remarks>
public sealed class IntegerMaxMinPublishChecksTests
{
    private readonly RealFormulaEngine _engine = new();

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("Max(0, @Measured - CST.Limit)", "Max")]
    [InlineData("Min(1, @X)", "Min")]
    [InlineData("Max(-1, 2.5)", "Max")]
    [InlineData("Max(Sign(@X), @Y)", "Max")]
    [InlineData("Max(Max(0, @X), 0.5) + 1", "Max")]
    [InlineData("Max(0, @X) + Min(0, @Y)", "Max, Min")]
    public void Ціле_першим_і_можливо_дробове_другим_дає_попередження(string expression, string functions)
    {
        var warnings = Warn(expression, NumericMode.Legacy);

        var warning = Assert.Single(warnings);
        Assert.Contains("\"F\"", warning, StringComparison.Ordinal);
        Assert.Contains(functions, warning, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("Max(0.0, @X)")]
    [InlineData("Max(@X, 0)")]
    [InlineData("Max(0, 5)")]
    [InlineData("Abs(@X) + Sign(@Y)")]
    public void Без_округлення_в_NCalc_попередження_немає(string expression)
        => Assert.Empty(Warn(expression, NumericMode.Legacy));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_не_попереджає()
        => Assert.Empty(Warn("Max(0, @X)", NumericMode.Strict));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Текст_з_каталогу_мовою_того_хто_публікує()
    {
        var strings = new UiStringCatalog("ru", 1, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IntegerMaxMinPublishChecks.WarningKey] = "Формула {formula}: {functions}",
        });
        var warnings = new List<string>();

        IntegerMaxMinPublishChecks.Warn(Parsed("Max(0, @X)"), NumericMode.Legacy, warnings, strings);

        Assert.Equal("Формула F: Max", Assert.Single(warnings));
    }

    private List<string> Warn(string expression, NumericMode mode)
    {
        var warnings = new List<string>();
        IntegerMaxMinPublishChecks.Warn(Parsed(expression), mode, warnings, strings: null);
        return warnings;
    }

    private ParsedFormula[] Parsed(string expression)
    {
        var parse = _engine.Parse(expression, ExpressionDialect.Methodology);
        Assert.NotNull(parse.Expression);
        return [new ParsedFormula("F", FormulaResultType.Number, parse.Expression!.Root)];
    }
}
