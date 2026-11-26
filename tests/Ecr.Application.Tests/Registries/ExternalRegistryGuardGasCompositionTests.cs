using Ecr.Application.Errors;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// D-NEW: довідники складу газу пише лише сторінка «Склад газу» — загальні редактори відмовляють з порадою «сторінка Склад
/// газу» за будь-якого джерела (і колишнє дзеркало <c>External</c>, і <c>Local</c>); решта External-довідників — як і раніше, AF.
/// </summary>
/// <remarks>
/// Мутація: прибрати гілку <c>IsGasCompositionRegistry</c> у <c>ExternalRegistryGuard</c> — червоніють обидві теорії (Local
/// пропускається, External дає ключ AF).
/// </remarks>
public sealed class ExternalRegistryGuardGasCompositionTests
{
    private static RegistryDef Registry(string code, RegistrySourceKind kind)
    {
        var def = new RegistryDef(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), false);
        def.SwitchSource(kind);
        return def;
    }

    [Theory]
    [InlineData("STREAM")]
    [InlineData("STREAM_CASE")]
    [InlineData("GAS_COMPOSITION")]
    [Trait("Requirement", "D-476")]
    public void Склад_газу_Local_відхиляється_з_ключем_сторінки(string code)
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => ExternalRegistryGuard.EnsureManualEditAllowed(Registry(code, RegistrySourceKind.Local)));

        Assert.Equal(ExternalRegistryGuard.GasCompositionMessageKey, ex.Details!["messageKey"]);
        Assert.Equal("ECR-REG-0409", ex.ErrorCode);
    }

    [Theory]
    [InlineData("STREAM")]
    [InlineData("GAS_COMPOSITION")]
    [Trait("Requirement", "D-476")]
    public void Колишнє_дзеркало_External_теж_відхиляється_з_ключем_сторінки(string code)
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => ExternalRegistryGuard.EnsureManualEditAllowed(Registry(code, RegistrySourceKind.External)));

        Assert.Equal(ExternalRegistryGuard.GasCompositionMessageKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Інший_External_довідник_лишається_з_ключем_AF()
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => ExternalRegistryGuard.EnsureManualEditAllowed(Registry("AF_STACKS", RegistrySourceKind.External)));

        Assert.Equal(ExternalRegistryGuard.MessageKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void COMPONENT_за_ECR_редагується()
        => ExternalRegistryGuard.EnsureManualEditAllowed(Registry("COMPONENT", RegistrySourceKind.Local));
}
