using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ліцензійна дисципліна (D-12).
/// </summary>
/// <remarks>
/// Заборонені пакети перелічені явно, бо кожен із них уже одного разу
/// потрапив у чернетку архітектури і був знятий після перевірки першоджерела:
/// <c>FluentAssertions</c> 8+ (комерційна Xceed), <c>EFCore.BulkExtensions</c>
/// (платна), <c>EPPlus</c> 5+ (noncommercial), <c>HyperFormula</c> (GPLv3),
/// <c>NBomber</c> 5+ (комерційна), Redis-сервер (AGPL/SSPL).
/// </remarks>
public sealed class LicenseComplianceTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_проєкт_не_посилається_на_заборонений_пакет()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Версії_пакетів_оголошені_централізовано_а_не_в_csproj()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_прямий_пакет_має_запис_у_реєстрі_стека()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Домен_не_має_жодного_PackageReference()
        => Assert.Fail("not implemented");
}
