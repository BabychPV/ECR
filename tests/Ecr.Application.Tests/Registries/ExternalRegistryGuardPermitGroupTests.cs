using Ecr.Application.Errors;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// PRMG: <see cref="ExternalRegistryGuard.EnsureManualEditAllowed"/> - єдина точка ручної зміни записів (upsert, пакет, CSV,
/// видалення, вікно чинності) - відмовляє для прихованого реєстру групи типів дозволів і не чіпає решту.
/// </summary>
/// <remarks>Мутація: прибрати виклик <c>RequireNotManaged</c> у <c>ExternalRegistryGuard</c> - червоніє перший тест.</remarks>
public sealed class ExternalRegistryGuardPermitGroupTests
{
    private static RegistryDef Registry(string code)
        => new(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), false);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Прихований_реєстр_групи_не_редагується_вручну()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => ExternalRegistryGuard.EnsureManualEditAllowed(Registry("PRMG_WATER")));

        Assert.Equal("err.ECR-PRM-0422.permitGroupRegistryManaged", ex.Details!["messageKey"]);
    }

    [Theory]
    [InlineData("PRM_WATER")]
    [InlineData("LAND_UNITS")]
    [InlineData("PRMGX")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Звичайний_і_шапковий_реєстр_проходить(string code)
        => ExternalRegistryGuard.EnsureManualEditAllowed(Registry(code));
}
