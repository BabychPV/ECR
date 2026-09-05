using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Дві перевірки публікації, названі вимогами і не написані нікому:
/// суперечливі рівні правил (<c>ФВ-5.10</c>) і обов'язкові колонки без жодного
/// покриття (<c>ФВ-5.11</c>).
/// </summary>
/// <remarks>
/// ⛔ Знайдено матрицею трасування: обидві вимоги лишалися непокритими, і
/// спроба знайти для них тест показала, що перевіряти нічого — <c>PublishChecks</c>
/// дивилися лише на формули. Наслідок точно той, від якого вимоги й
/// застерігають: суперечність між правилами виявляється в рантаймі, коли
/// оператор уже не може зберегти рядок і не розуміє чому.
/// </remarks>
public sealed class PublishRuleCoverageTests
{
    /// <summary>Версія з однією таблицею; колонки й правила додаються тестом.</summary>
    private static (TemplateVersion Version, TableDef Table) Structure()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");

        var version = new TemplateVersion(1, "1.0.0.0", 7, DateTime.UnixEpoch);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(version, 1);
        version.GetType()
            .GetField("_sheets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(version, new List<SheetDef> { sheet });

        return (version, table);
    }

    /// <summary>Правило валідації з заданим рівнем на задану колонку.</summary>
    private static ValidationRule Rule(
        TableDef table, string code, ValidationSeverity severity, byte scope, int? columnDefId)
    {
        var rule = new ValidationRule(
            table.Id,
            EcrCode.Create(code),
            severity,
            scope,
            "1 = 1",
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            columnDefId);

        table.AddValidationRule(rule);

        return rule;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.10")]
    public void Правила_однієї_області_з_різними_рівнями_виявляються_при_публікації()
    {
        var (version, table) = Structure();
        var column = new TemplateBuilder { TemplateVersionId = 1 }.Column(table, "Jan");

        // ⛔ Дві дії на ту саму комірку з різними рівнями: одна каже «зберегти
        // не можна», друга — «можна, але зверни увагу». Оператор бачить обидві
        // й не може виконати жодної поради: `Error` рівня комірки блокує запис
        // (R-B3), тобто `Warning` поруч із ним не має сенсу ніколи.
        Rule(table, "R1", ValidationSeverity.Error, scope: 0, column.Id);
        Rule(table, "R2", ValidationSeverity.Warning, scope: 0, column.Id);

        var diagnostics = PublishChecks.CheckRules(version);

        var conflict = Assert.Single(diagnostics, d => d.Code == "ECR-TMPL-4224");
        Assert.Contains("R1", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("R2", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.10")]
    public void Правила_різних_областей_з_різними_рівнями_суперечності_не_дають()
    {
        var (version, table) = Structure();
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var jan = builder.Column(table, "Jan");
        var feb = builder.Column(table, "Feb");

        // ⚠ Різні колонки — різні області дії. Рівні можуть відрізнятися
        // скільки завгодно: сувора перевірка одного показника і м'яка іншого
        // — норма, а не помилка конфігурації.
        Rule(table, "R1", ValidationSeverity.Error, scope: 0, jan.Id);
        Rule(table, "R2", ValidationSeverity.Warning, scope: 0, feb.Id);

        // ⚠ І та сама колонка на РІЗНИХ рівнях області (комірка проти
        // документа) — теж не суперечність: документне правило не блокує
        // запис за побудовою (ФВ-5.18).
        Rule(table, "R3", ValidationSeverity.Error, scope: 3, jan.Id);

        Assert.DoesNotContain(PublishChecks.CheckRules(version), d => d.Code == "ECR-TMPL-4224");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.11")]
    public void Обовязкова_колонка_без_правила_і_без_формули_виявляється_при_публікації()
    {
        var (version, table) = Structure();
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var required = builder.Column(table, "Jan");
        required.SetRequired(true);

        // ⛔ Обов'язкова колонка, яку ніхто не перевіряє і не рахує, — це
        // обіцянка без виконавця: система оголошує значення необхідним і не
        // має жодного способу помітити його відсутність.
        var diagnostics = PublishChecks.CheckRules(version);

        var uncovered = Assert.Single(diagnostics, d => d.Code == "ECR-TMPL-4225");
        Assert.Contains("Jan", uncovered.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.11")]
    public void Обовязкова_колонка_з_формулою_покрита()
    {
        var (version, table) = Structure();
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var required = builder.Column(table, "Total");
        required.SetRequired(true);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "1", ExpressionDialect.Template);
        typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!.SetValue(formula, required.Id);
        table.AddFormula(formula);

        // ⚠ Обчислювана колонка не потребує правила: значення в ній з'являється
        // саме, і «не заповнено» для неї означало б помилку розрахунку, а не
        // недогляд оператора.
        Assert.DoesNotContain(PublishChecks.CheckRules(version), d => d.Code == "ECR-TMPL-4225");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.11")]
    public void Необовязкова_колонка_покриття_не_потребує()
    {
        var (version, table) = Structure();
        new TemplateBuilder { TemplateVersionId = 1 }.Column(table, "Note", CellDataType.String);

        Assert.DoesNotContain(PublishChecks.CheckRules(version), d => d.Code == "ECR-TMPL-4225");
    }
}
