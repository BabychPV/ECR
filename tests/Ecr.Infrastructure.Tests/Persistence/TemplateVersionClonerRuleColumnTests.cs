// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionClonerRuleColumnTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ⛔ A1-03 (аудит 09.10c): правило валідації з <c>ColumnDefId</c> поза своєю таблицею (до A1-03 API таке
/// приймало) клон перетворював на ТАБЛИЧНЕ: <c>Prepare</c> не знаходив коду колонки, лінку не створював, а
/// скидання ключів обнуляло <c>ColumnDefId</c> — правило починало діяти на всі колонки таблиці.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати гілку з відмовою в <c>TemplateVersionCloner.Prepare</c> — перший тест червоніє
/// (<c>Prepare</c> проходить, а правило клону має <c>ColumnDefId == null</c>).
/// </remarks>
public sealed class TemplateVersionClonerRuleColumnTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "A1-03")]
    public void Правило_з_колонкою_іншої_таблиці_не_стає_табличним_клон_відмовляє()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Volume");
        var foreign = builder.Column(builder.Table(sheet, "Other"), "Flow");
        var rule = Rule(table, "R1", foreign.Id);
        var source = builder.Version();

        var error = Assert.Throws<BusinessRuleException>(
            () => TemplateVersionCloner.Prepare(source, "2.0.0.0", 7, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("err.ECR-TMPL-0422.cloneValidationRuleForeignColumn", error.Details?["messageKey"]);
        Assert.Equal("R1", error.Details?["ruleCode"]);

        // У жодному разі не табличне: правило джерела не зачеплене.
        Assert.Equal(foreign.Id, rule.ColumnDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "A1-03")]
    public void Контроль_правило_своєї_колонки_і_табличне_правило_клонуються()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var volume = builder.Column(table, "Volume");
        Rule(table, "R1", volume.Id);
        Rule(table, "R2", columnDefId: null);
        var source = builder.Version();

        var (_, links) = TemplateVersionCloner.Prepare(
            source, "2.0.0.0", 7, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));

        var link = Assert.Single(links.Rules);
        Assert.Equal("R1", link.Rule.Code);
        Assert.Equal("Volume", link.ColumnCode);
    }

    private static ValidationRule Rule(TableDef table, string code, int? columnDefId)
    {
        var rule = new ValidationRule(
            table.Id, EcrCode.Create(code), ValidationSeverity.Error, 0, "[VALUE] >= 0",
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Broken" }), columnDefId);
        table.AddValidationRule(rule);
        return rule;
    }
}
