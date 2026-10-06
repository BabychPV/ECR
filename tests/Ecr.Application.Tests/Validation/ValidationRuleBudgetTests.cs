using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// RC5: правило, що впирається в межу обчислення (крок або глибина), дає
/// Warning із людським ключем <c>validation.rule.budget</c>, а не голе
/// «did not return a logical answer: #BUDGET».
/// </summary>
/// <remarks>
/// ⚠ Формулу, яку вже збережено й яка не вміщається в одне обчислення, автор
/// інакше побачив би як «#BUDGET» без підказки, що з цим робити. Глибину в
/// тесті дає ланцюг унарних мінусів (парсер пускає ~190 підряд, обчислювач —
/// 96 рівнів): так вона відтворюється без даних таблиці.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class ValidationRuleBudgetTests
{
    private static readonly IReadOnlyDictionary<string, Ecr.Expressions.Evaluation.ExpressionValue> NoHeaders =
        new Dictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>();

    private static ValidationRule Rule(string code, string expression)
        => new(tableDefId: 3, EcrCode.Create(code), ValidationSeverity.Error, scope: 1, expression,
               new LocalizedText(new Dictionary<string, string> { ["en"] = $"Порушено {code}" }));

    private static ValidationMessage Evaluate(string expression, string language)
        => Assert.Single(new ValidationEngine(new RealFormulaEngine()).ValidateScope(
            1, [Rule("DEEP", expression)], new Values { ["Mass"] = 5m }, NoHeaders, language));

    [Fact]
    public void Бюджетна_відмова_дає_Warning_validation_rule_budget_а_не_notLogical()
    {
        var message = Evaluate(string.Concat(Enumerable.Repeat("- ", 100)) + "[Mass] > 0", "en");

        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.False(message.BlocksSave);
        Assert.Equal(ValidationEngine.BrokenRuleCode, message.RuleCode);
        Assert.Equal(ValidationMessageTemplates.RuleBudget, message.MessageKey);
        Assert.Equal("DEEP", message.Params!["rule"]);
        Assert.DoesNotContain("#BUDGET", message.Message, StringComparison.Ordinal);
        Assert.Contains("too large", message.Message, StringComparison.Ordinal);
        Assert.Contains("calculated columns", message.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ru", "слишком велика")]
    [InlineData("kz", "тым үлкен")]
    public void Текст_бюджетної_відмови_мовою_запиту(string language, string fragment)
    {
        var message = Evaluate(string.Concat(Enumerable.Repeat("- ", 100)) + "[Mass] > 0", language);

        Assert.Contains(fragment, message.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Звичайна_помилка_обчислення_лишається_notLogical()
    {
        // Контроль у протилежний бік: не кожна помилка — бюджетна.
        var message = Evaluate("[Mass] / 0 > 1", "en");

        Assert.Equal(ValidationMessageTemplates.RuleNotLogical, message.MessageKey);
        Assert.Contains("#DIV/0", message.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Пласка_сума_зі_100_доданків_у_правилі_рахується_а_не_дає_бюджетної_відмови()
    {
        var sum = string.Join(" + ", Enumerable.Repeat("[Mass]", 100));

        Assert.Empty(new ValidationEngine(new RealFormulaEngine()).ValidateScope(
            1, [Rule("FLAT", sum + " > 0")], new Values { ["Mass"] = 5m }, NoHeaders, "en"));
    }

    private sealed class Values : Dictionary<string, object?>, IValidationContext
    {
        public object? GetCell(string columnCode) => this.GetValueOrDefault(columnCode);

        public object? GetCell(string rowKey, string columnCode) => GetCell(columnCode);
    }
}
