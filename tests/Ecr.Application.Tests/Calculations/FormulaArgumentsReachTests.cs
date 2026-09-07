using Ecr.Application.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Список аргументів доходить до публікації (<c>I.8</c>, пастка 2).
/// </summary>
/// <remarks>
/// ⛔ Сама звірка була написана й покрита дванадцятьма мутаціями — і **мовчала
/// у продуктиві**: <c>calc.MethodologyFormula</c> не мала колонки під
/// <c>FInfo_Arguments</c>, тож <c>ParsedFormula.DeclaredArguments</c> завжди
/// приходив <c>null</c>, а на <c>null</c> звірка за домовленістю мовчить.
///
/// ⚠ Тобто перевірка існувала рівно в тому вигляді, який цей пакет закриває
/// всю дорогу: механізм оголошений, протестований і недосяжний (<c>Q-082</c>,
/// <c>Q-086</c>, <c>Q-088</c>, <c>Q-091</c>). Ці тести стережуть **ланку**, а
/// не саму звірку: чи дійшов список від сутності до перевірки.
/// </remarks>
public sealed class FormulaArgumentsReachTests
{
    /// <summary>Формула, яка вживає токен, і оголошений список.</summary>
    private static ParsedFormula Parsed(string? declared)
        => new(
            "Total",
            FormulaResultType.Number,
            new SymbolReferenceNode(SymbolKind.Argument, "FuelConsumption"),
            Ecr.Expressions.Binding.ArgumentDeclarationChecker.Declared(declared));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public void Токен_поза_оголошеним_списком_відхиляє_публікацію()
    {
        // ⛔ Головне твердження ланки. Список оголошено, і потрібного токена в
        // ньому немає — отже чинна збірка підставила б у вираз не все, і
        // формула порахувалася б із невизначеним параметром без ознаки збою.
        var error = Assert.Throws<Ecr.Application.Errors.BusinessRuleException>(
            () => MethodologyPublishChecks.Check([Parsed("Duration")], [], []));

        Assert.Equal("ECR-CALC-0432", error.ErrorCode);
        Assert.Contains("FuelConsumption", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Оголошений_і_вжитий_токен_проходить()
    {
        var problems = MethodologyPublishChecks.Check([Parsed("FuelConsumption")], [], []);

        Assert.Empty(problems);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Списку_немає_звірка_мовчить()
    {
        // ⚠ `null` — це «списку не оголошено», і мовчання тут навмисне: у
        // корпусі є формули без `FInfo_Arguments`, і відхиляти їх означало б
        // зупинити міграцію на тому, що чинна система дозволяла.
        //
        // ⛔ Саме через цю домовленість дефект і був невидимий: доки колонки не
        // існувало, `null` приходив ЗАВЖДИ, і мовчання виглядало як робота.
        var problems = MethodologyPublishChecks.Check([Parsed(null)], [], []);

        Assert.Empty(problems);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Порожній_список_це_не_відсутність_списку()
    {
        // ⚠ Порожній рядок означає «оголошено нуль аргументів», і тоді
        // будь-який токен у виразі — порушення. Зрівняти його з `null` було б
        // зручно і неправильно: різницю між «не сказано» і «сказано, що
        // жодного» видно лише тут.
        var error = Assert.Throws<Ecr.Application.Errors.BusinessRuleException>(
            () => MethodologyPublishChecks.Check([Parsed(string.Empty)], [], []));

        Assert.Equal("ECR-CALC-0432", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Оголошений_і_невжитий_аргумент_дає_попередження_а_не_відмову()
    {
        // ⛔ Публікація ПРОХОДИТЬ. Чинна система це допускала, і ламати через
        // це міграцію не можна (№05 §7): «оголошено, не вжито» — 359 випадків
        // у 186 формулах корпусу.
        var warnings = new List<string>();

        var problems = MethodologyPublishChecks.Check(
            [Parsed("FuelConsumption;Duration")], [], [], contextualArguments: [], warnings: warnings);

        Assert.Empty(problems);
        Assert.Contains(warnings, w => w.Contains("Duration", StringComparison.Ordinal));
    }
}
