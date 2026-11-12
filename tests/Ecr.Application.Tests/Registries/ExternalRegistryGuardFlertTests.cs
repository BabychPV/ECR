using Ecr.Application.Errors;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// <c>D-476</c>: ручна правка дзеркала складу газу FLERT відхиляється з порадою «змінюйте у FLERT», а не «в AF»; решта
/// External-довідників — як і раніше, AF.
/// </summary>
/// <remarks>Мутація: прибрати гілку <c>IsFlertMirror</c> у <c>ExternalRegistryGuard</c> — червоніє перший тест.</remarks>
public sealed class ExternalRegistryGuardFlertTests
{
    private static RegistryDef External(string code)
    {
        var def = new RegistryDef(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), false);
        def.SwitchSource(RegistrySourceKind.External);
        return def;
    }

    [Theory]
    [InlineData("STREAM")]
    [InlineData("STREAM_CASE")]
    [InlineData("GAS_COMPOSITION")]
    [Trait("Requirement", "D-476")]
    public void Дзеркало_FLERT_відхиляється_з_ключем_FLERT(string code)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => ExternalRegistryGuard.EnsureManualEditAllowed(External(code)));

        Assert.Equal(ExternalRegistryGuard.FlertMessageKey, ex.Details!["messageKey"]);
        Assert.Equal("ECR-REG-0409", ex.ErrorCode);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Інший_External_довідник_лишається_з_ключем_AF()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => ExternalRegistryGuard.EnsureManualEditAllowed(External("AF_STACKS")));

        Assert.Equal(ExternalRegistryGuard.MessageKey, ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void COMPONENT_за_ECR_редагується()
    {
        var def = new RegistryDef(EcrCode.Create("COMPONENT"), new LocalizedText(new Dictionary<string, string> { ["en"] = "c" }), false);
        ExternalRegistryGuard.EnsureManualEditAllowed(def);
    }
}
