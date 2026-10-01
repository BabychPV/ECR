using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>Чиста функція «правило → стиль» (ФВ-2.6/2.7), спільна для сітки й Excel.</summary>
public sealed class ConditionalFormatEvaluatorTests
{
    private static ConditionalFormatRule Rule(
        string op, string? value = null, string? to = null, int ordinal = 1,
        string? bg = "#ff0000", string? fg = null, bool bold = false, string column = "A")
        => new(1, column, ordinal, op, value, to, bg, fg, bold);

    // Конструктор правила валідує оператор і операнди (ECR-CFG-0422), тож «зіпсоване»
    // правило (старі дані, обхід валідації) збирається в обхід нього: оцінювач
    // мусить відмовляти сам, а не покладатися на валідатор.
    private static ConditionalFormatRule RawRule(string op, string? value = null, string? to = null)
    {
        var rule = (ConditionalFormatRule)Activator.CreateInstance(typeof(ConditionalFormatRule), nonPublic: true)!;
        void Set(string name, object? v) =>
            typeof(ConditionalFormatRule).GetProperty(name)!.SetValue(rule, v);
        Set(nameof(ConditionalFormatRule.ColumnCode), "A");
        Set(nameof(ConditionalFormatRule.Ordinal), 1);
        Set(nameof(ConditionalFormatRule.Operator), op);
        Set(nameof(ConditionalFormatRule.Value), value);
        Set(nameof(ConditionalFormatRule.ValueTo), to);
        Set(nameof(ConditionalFormatRule.BackgroundHex), "#ff0000");
        return rule;
    }
    private static CellFormatDto? Eval(ConditionalFormatRule rule, object? value)
        => ConditionalFormatEvaluator.Evaluate([rule], value);

    [Theory]
    [InlineData("gt", "5", "6", true)]
    [InlineData("gt", "5", "5", false)]
    [InlineData("ge", "5", "5", true)]
    [InlineData("lt", "5", "4", true)]
    [InlineData("lt", "5", "5", false)]
    [InlineData("le", "5", "5", true)]
    [InlineData("eq", "5,5", "5.5", true)]
    [InlineData("eq", "5", "6", false)]
    [InlineData("ne", "5", "6", true)]
    [InlineData("ne", "5", "5", false)]
    // Значення ПО ІНШИЙ бік межі: без них `gt`/`lt` → `!=` і `ge`/`le` → `true`
    // проходили б увесь набір (мутаційна перевірка 2026-10-01).
    [InlineData("gt", "5", "4", false)]
    [InlineData("ge", "5", "4", false)]
    [InlineData("ge", "5", "6", true)]
    [InlineData("lt", "5", "6", false)]
    [InlineData("le", "5", "6", false)]
    [InlineData("le", "5", "4", true)]
    public void Comparison_operators_are_numeric(string op, string operand, string value, bool expected)
    {
        var number = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, Eval(Rule(op, operand), number) is not null);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(11, false)]
    public void Between_is_inclusive_and_order_independent(int value, bool expected)
    {
        Assert.Equal(expected, Eval(Rule("between", "10", "1"), (decimal)value) is not null);
    }

    [Fact]
    public void Empty_and_notEmpty_follow_null_and_blank()
    {
        Assert.NotNull(Eval(Rule("empty"), null));
        Assert.NotNull(Eval(Rule("empty"), "  "));
        Assert.Null(Eval(Rule("empty"), 0m));
        Assert.NotNull(Eval(Rule("notEmpty"), 0m));
        Assert.Null(Eval(Rule("notEmpty"), null));
    }

    [Fact]
    public void Non_numeric_value_never_matches_numeric_operators()
    {
        foreach (var op in new[] { "gt", "ge", "lt", "le", "eq", "ne" })
        {
            Assert.Null(Eval(Rule(op, "5"), "abc"));
            Assert.Null(Eval(Rule(op, "5"), "5"));
            Assert.Null(Eval(Rule(op, "5"), true));
            Assert.Null(Eval(Rule(op, "5"), null));
        }
    }

    [Fact]
    public void First_matching_rule_by_ordinal_wins_and_carries_style()
    {
        var rules = ConditionalFormatEvaluator.ByColumn(
        [
            Rule("gt", "0", ordinal: 2, bg: "#00ff00"),
            Rule("gt", "100", ordinal: 1, bg: "#0000ff", fg: "#ffffff", bold: true),
            Rule("gt", "0", ordinal: 1, bg: "#ff00ff", column: "B"),
        ])["A"];

        var big = ConditionalFormatEvaluator.Evaluate(rules, 500m);
        var small = ConditionalFormatEvaluator.Evaluate(rules, 5m);

        Assert.Equal(new CellFormatDto("#0000ff", "#ffffff", true), big);
        Assert.Equal(new CellFormatDto("#00ff00", null, false), small);
        Assert.Null(ConditionalFormatEvaluator.Evaluate(rules, -1m));
    }

    [Fact]
    public void Double_is_a_number_but_registry_and_unit_ids_are_not()
    {
        Assert.NotNull(Eval(Rule("eq", "2.5"), 2.5d));

        // `long` — елемент довідника, `int` — одиниця (`CellValueMapping.ToRuleValue`):
        // ідентифікатор, а не число в комірці.
        Assert.Null(Eval(Rule("eq", "7"), 7L));
        Assert.Null(Eval(Rule("ne", "100"), 7L));
        Assert.Null(Eval(Rule("eq", "2"), 2));
        Assert.NotNull(Eval(Rule("notEmpty"), 7L));
    }

    [Fact]
    public void Between_without_numeric_upper_bound_never_matches()
    {
        // Без перевірки `ValueTo` верхня межа мовчки ставала б 0, і «між 5 і (нічим)»
        // фарбувало б 3.
        Assert.Null(Eval(RawRule("between", "5", null), 3m));
        Assert.Null(Eval(RawRule("between", "5", "abc"), 3m));
        Assert.Null(Eval(RawRule("between", "5", "  "), 0m));
    }

    [Fact]
    public void Missing_or_non_numeric_operand_never_matches()
    {
        foreach (var op in new[] { "gt", "ge", "lt", "le", "eq", "ne" })
        {
            Assert.Null(Eval(RawRule(op, null), 5m));
            Assert.Null(Eval(RawRule(op, "abc"), 5m));
        }
    }

    [Fact]
    public void Unknown_operator_never_matches()
    {
        Assert.Null(Eval(RawRule("contains", "5"), 5m));
        Assert.Null(Eval(RawRule("contains", "5"), null));
    }

    [Fact]
    public void Double_outside_decimal_range_is_not_a_number()
    {
        // `Convert.ToDecimal` кидає `OverflowException`: значення не число для правила,
        // а не «0» і не падіння сітки.
        Assert.Null(Eval(Rule("ne", "0"), double.MaxValue));
        Assert.Null(Eval(Rule("ne", "0"), double.NaN));
        Assert.Null(Eval(Rule("ne", "0"), double.PositiveInfinity));
        Assert.Null(Eval(Rule("eq", "0"), double.MaxValue));
    }

    [Fact]
    public void Float_is_a_number()
    {
        Assert.NotNull(Eval(Rule("eq", "2.5"), 2.5f));
        Assert.Null(Eval(Rule("gt", "2.5"), 2.5f));
    }

    [Fact]
    public void NotEmpty_is_false_for_blank_text()
    {
        Assert.Null(Eval(Rule("notEmpty"), "   "));
        Assert.NotNull(Eval(Rule("empty"), string.Empty));
        Assert.NotNull(Eval(Rule("notEmpty"), "x"));
    }
}
