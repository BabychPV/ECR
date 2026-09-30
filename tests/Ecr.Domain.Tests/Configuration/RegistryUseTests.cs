// tests/Ecr.Domain.Tests/Configuration/RegistryUseTests.cs
using Ecr.Domain.Entities.Configuration;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Ребро «хто використовує довідник» у домені (RT-05, FEATURE-REGISTRY-TABLES §3.2).
/// </summary>
/// <remarks>
/// Закритість виду в базі (<c>CK_RegUse_Kind</c>) перевіряє
/// <c>RegistryUseSchemaTests</c> (Ecr.Infrastructure.Tests) — вставкою повз домен.
/// </remarks>
public sealed class RegistryUseTests
{
    [Fact]
    [Trait("Directive", "RT-05")]
    public void Кожна_фабрика_задає_свій_вид_і_лише_свої_посилання()
    {
        var template = RegistryUse.ForTemplateFormula(formulaDefId: 11, registryDefId: 5, fieldPath: "COMPONENT.MW");
        var methodology = RegistryUse.ForMethodologyFormula(
            methodologyVersionId: 22, formulaCode: "EF_CO2", registryDefId: 5, fieldPath: null);
        var rule = RegistryUse.ForRegistryRule(registryRuleDefId: 33, registryDefId: 6, fieldPath: "MW");

        Assert.Equal((RegistryUse.TemplateFormulaSource, 11, (string?)null, 5, (string?)"COMPONENT.MW"),
            (template.SourceKind, template.SourceId, template.FormulaCode, template.RegistryDefId, template.FieldPath));
        Assert.Equal((RegistryUse.MethodologyVersionSource, 22, (string?)"EF_CO2", 5, (string?)null),
            (methodology.SourceKind, methodology.SourceId, methodology.FormulaCode, methodology.RegistryDefId, methodology.FieldPath));
        Assert.Equal((RegistryUse.RegistryRuleSource, 33, (string?)null, 6, (string?)"MW"),
            (rule.SourceKind, rule.SourceId, rule.FormulaCode, rule.RegistryDefId, rule.FieldPath));
    }

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Види_різні_й_лежать_у_межах_CHECK()
    {
        byte[] kinds = [RegistryUse.TemplateFormulaSource, RegistryUse.MethodologyVersionSource, RegistryUse.RegistryRuleSource];

        Assert.Equal(kinds.Length, kinds.Distinct().Count());
        Assert.Equal([0, 1, 2], kinds.Select(k => (int)k));
    }

    [Theory]
    [Trait("Directive", "RT-05")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Порожній_шлях_поля_означає_довідник_цілком(string? fieldPath)
    {
        var use = RegistryUse.ForTemplateFormula(11, 5, fieldPath);

        Assert.Null(use.FieldPath);
    }

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Шлях_і_код_обрізаються_від_пробілів()
    {
        var use = RegistryUse.ForMethodologyFormula(22, "  EF_CO2 ", 5, " COMPONENT.MW ");

        Assert.Equal("EF_CO2", use.FormulaCode);
        Assert.Equal("COMPONENT.MW", use.FieldPath);
    }

    [Theory]
    [Trait("Directive", "RT-05")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Формула_методології_без_коду_відхиляється(string? formulaCode)
    {
        Assert.ThrowsAny<ArgumentException>(() => RegistryUse.ForMethodologyFormula(22, formulaCode!, 5, null));
    }

    [Fact]
    [Trait("Directive", "RT-05")]
    public void Задовгі_код_і_шлях_відхиляються_до_бази()
    {
        var code = new string('C', RegistryUse.FormulaCodeMaxLength + 1);
        var path = new string('P', RegistryUse.FieldPathMaxLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryUse.ForMethodologyFormula(22, code, 5, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryUse.ForRegistryRule(33, 5, path));

        // Рівно на межі — дозволено.
        var edge = RegistryUse.ForMethodologyFormula(
            22, code[..RegistryUse.FormulaCodeMaxLength], 5, path[..RegistryUse.FieldPathMaxLength]);
        Assert.Equal(RegistryUse.FieldPathMaxLength, edge.FieldPath!.Length);
    }

    [Theory]
    [Trait("Directive", "RT-05")]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(11, 0)]
    [InlineData(11, -4)]
    public void Неіснуючі_джерело_чи_довідник_відхиляються(int sourceId, int registryDefId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryUse.ForTemplateFormula(sourceId, registryDefId, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistryUse.ForRegistryRule(sourceId, registryDefId, null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RegistryUse.ForMethodologyFormula(sourceId, "EF", registryDefId, null));
    }
}
