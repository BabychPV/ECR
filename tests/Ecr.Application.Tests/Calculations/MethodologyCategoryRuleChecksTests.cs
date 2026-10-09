// tests/Ecr.Application.Tests/Calculations/MethodologyCategoryRuleChecksTests.cs
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Перевірки правила категорії (<see cref="MethodologyCategoryRuleChecks"/>): межа вкладеності.
/// </summary>
/// <remarks>
/// ⛔ N2-01 (AN-72): правило не проходило <c>ExpressionNesting.Diagnose</c> (його кликала лише перевірка
/// формул публікації). Правило глибше за <see cref="EvaluationBudget.MaxNestingDepth"/> проходило і
/// збереження, і публікацію, а в роботі давало <c>#BUDGET</c> → <c>categoryRuleFailed</c>, і рядки
/// мовчки відхилялися. Мутаційний доказ: прибрати виклик <c>Diagnose</c> з <c>Check</c> — червоні обидва
/// тести нижче.
/// </remarks>
public sealed class MethodologyCategoryRuleChecksTests
{
    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правило_глибше_96_відхиляється()
    {
        // Унарні мінуси — рівно те ребро, що дає глибину 1 на кожен знак (дужки, виклики й гілки
        // впиралися б у межу парсера раніше за 96). 100 знаків → глибина 101.
        var rule = string.Concat(Enumerable.Repeat("- ", 100)) + "'a'";

        var outcome = MethodologyCategoryRuleChecks.Check(rule, _engine, [], [], [], null, null);

        var problem = Assert.Single(outcome.Problems, p => p.MessageKey == "publish.problem.formulaTooDeep");
        Assert.Equal(MethodologyCategoryRuleChecks.RuleCode, problem.Args["formula"]);
        Assert.Equal("101", problem.Args["depth"]);
        Assert.Equal(EvaluationBudget.MaxNestingDepth.ToString(System.Globalization.CultureInfo.InvariantCulture), problem.Args["max"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правило_на_межі_глибини_не_дає_проблеми_глибини()
    {
        var rule = string.Concat(Enumerable.Repeat("- ", EvaluationBudget.MaxNestingDepth - 1)) + "'a'";

        var outcome = MethodologyCategoryRuleChecks.Check(rule, _engine, [], [], [], null, null);

        Assert.DoesNotContain(outcome.Problems, p => p.MessageKey == "publish.problem.formulaTooDeep");
    }
}
