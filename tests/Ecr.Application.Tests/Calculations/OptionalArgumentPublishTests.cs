// tests/Ecr.Application.Tests/Calculations/OptionalArgumentPublishTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// D-OA: маркер <c>Name?</c> у <c>ArgumentsCsv</c> знімає вимогу колонки (<c>ECR-CALC-0438</c>) для
/// ВСІЄЇ версії; не-optional аргумент лишається під перевіркою.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати віднімання <c>optionalKeys</c> у
/// <see cref="MethodologyPublishChecks.CheckArgumentTableColumns"/> — тести 1 і 2 червоніють.
/// </remarks>
public sealed class OptionalArgumentPublishTests
{
    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Необовязковий_аргумент_без_колонки_не_дає_0438_а_без_маркера_дає()
    {
        var tables = new Dictionary<int, IReadOnlyList<string>> { [900] = ["Pr_Type"] };

        MethodologyPublishChecks.CheckArgumentTableColumns(Formulas(("@Op * 2", "Op?")), tables);

        var error = Assert.Throws<BusinessRuleException>(() =>
            MethodologyPublishChecks.CheckArgumentTableColumns(Formulas(("@Op * 2", "Op")), tables));
        Assert.Equal("ECR-CALC-0438", error.ErrorCode);
        Assert.Contains("@Op", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дві_таблиці_необовязковий_є_лише_в_одній_перелічується_тільки_обовязковий()
    {
        var tables = new Dictionary<int, IReadOnlyList<string>>
        {
            [900] = ["Pr_Type", "Op"],
            [901] = ["Pr_Type"],
        };

        // `Op?` відсутній у таблиці 901 — ок; `Req` відсутній в обох — 0438 лише про `Req`.
        var error = Assert.Throws<BusinessRuleException>(() =>
            MethodologyPublishChecks.CheckArgumentTableColumns(Formulas(("@Op * @Req", "Op?;Req")), tables));

        Assert.Contains("@Req", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@Op", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Маркер_в_одній_формулі_звільняє_аргумент_для_всієї_версії_і_регістр_ігнорується()
    {
        var tables = new Dictionary<int, IReadOnlyList<string>> { [900] = ["Pr_Type"] };

        // Q-OA-4: `Op?` в одній формулі, `op` (без маркера) в іншій — optional для всієї версії.
        MethodologyPublishChecks.CheckArgumentTableColumns(
            Formulas(("@Op * 2", "Op?"), ("@op + 1", "op")), tables);

        Assert.Equal(["Op"], MethodologyPublishChecks.OptionalArguments(
            Formulas(("@Op * 2", "Op?"), ("@op + 1", "op"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Правило_категорії_успадковує_необовязкові_аргументи_версії()
    {
        var tables = new Dictionary<int, IReadOnlyList<string>> { [900] = ["Pr_Type"] };
        var rule = new ParsedFormula(
            "CategoryRule",
            FormulaResultType.Text,
            _engine.Parse("@Fuel", ExpressionDialect.Methodology).Expression?.Root);

        // Без множини optional правило відхиляється кодом 0422 (регресія).
        var error = Assert.Throws<BusinessRuleException>(() =>
            MethodologyCategoryRuleChecks.CheckArgumentColumns(rule, tables));
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);

        // Q-OA-3: з множиною optional версії — проходить.
        MethodologyCategoryRuleChecks.CheckArgumentColumns(
            rule, tables, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Fuel" });
    }

    private IReadOnlyList<ParsedFormula> Formulas(params (string Expression, string Declared)[] items)
        => [.. items.Select((item, index) => new ParsedFormula(
            "F" + index,
            FormulaResultType.Number,
            _engine.Parse(item.Expression, ExpressionDialect.Methodology).Expression?.Root,
            ArgumentDeclarationChecker.Declared(item.Declared)))];
}
