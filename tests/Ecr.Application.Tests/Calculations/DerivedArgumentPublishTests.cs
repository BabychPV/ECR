// tests/Ecr.Application.Tests/Calculations/DerivedArgumentPublishTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// AN-5: публікація не вимагає колонку для похідних аргументів події (<c>Total</c>, <c>Duration</c>,
/// <c>FlareUnitMode</c>, <c>IsPilot</c>, <c>Category</c>), але лишає <c>ECR-CALC-0438</c> для решти.
/// </summary>
public sealed class DerivedArgumentPublishTests
{
    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Похідні_аргументи_не_потребують_колонок_у_таблиці_прив_язки()
    {
        var formulas = Formulas("if(@FlareUnitMode = 1, 0, @Total * 10 / @Duration)");

        // Таблиця без Total/Duration/FlareUnitMode — публікація не падає.
        MethodologyPublishChecks.CheckArgumentTableColumns(
            formulas, new Dictionary<int, IReadOnlyList<string>> { [900] = ["Pr_Type", "Value"] });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Не_похідний_аргумент_без_колонки_лишається_ECR_CALC_0438()
    {
        var formulas = Formulas("@Total * @Ghost");

        var error = Assert.Throws<BusinessRuleException>(() =>
            MethodologyPublishChecks.CheckArgumentTableColumns(
                formulas, new Dictionary<int, IReadOnlyList<string>> { [900] = ["Pr_Type"] }));

        Assert.Equal("ECR-CALC-0438", error.ErrorCode);
        Assert.Contains("@Ghost", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@Total", error.Message, StringComparison.Ordinal);
    }

    private IReadOnlyList<ParsedFormula> Formulas(string expression)
        =>
        [
            new ParsedFormula(
                "Out",
                FormulaResultType.Number,
                _engine.Parse(expression, ExpressionDialect.Methodology).Expression?.Root),
        ];
}
