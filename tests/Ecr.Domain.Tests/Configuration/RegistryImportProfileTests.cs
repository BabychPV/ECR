// tests/Ecr.Domain.Tests/Configuration/RegistryImportProfileTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Профіль імпорту довідника в домені (RT-06, FEATURE-REGISTRY-TABLES §3.3, §4.6).
/// </summary>
/// <remarks>
/// Той самий інваріант у базі (<c>CK_RegImpProfile_Json</c>) і <c>RowVersion</c>
/// перевіряє <c>RegistryImportProfileSchemaTests</c> (Ecr.Infrastructure.Tests) —
/// вставкою повз домен.
/// </remarks>
public sealed class RegistryImportProfileTests
{
    private const string Spec = """{"orientation":"Columns","headerRows":2,"duplicateSources":{"CO":"Carbon_Monoxide"}}""";

    private static readonly DateTime Created = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Directive", "RT-06")]
    public void Профіль_зберігає_довідник_код_назву_специфікацію_й_автора()
    {
        var profile = new RegistryImportProfile(5, EcrCode.Create("HYSYS_OFFSHORE"), Name("HYSYS offshore export"), Spec, 7, Created);

        Assert.Equal((5, "HYSYS_OFFSHORE", Spec, 7, Created),
            (profile.RegistryDefId, profile.Code, profile.SpecJson, profile.UpdatedByUserId, profile.UpdatedAt));
        Assert.Equal("HYSYS offshore export", profile.NameL10n.Values["en"]);
    }

    [Fact]
    [Trait("Directive", "RT-06")]
    public void Оновлення_замінює_назву_специфікацію_автора_й_момент_але_не_код_і_довідник()
    {
        var profile = new RegistryImportProfile(5, EcrCode.Create("HYSYS_OFFSHORE"), Name("Old"), Spec, 7, Created);
        const string newSpec = """{"orientation":"Rows"}""";

        profile.Update(Name("New"), newSpec, 9, Created.AddHours(1));

        Assert.Equal((5, "HYSYS_OFFSHORE", newSpec, 9, Created.AddHours(1), "New"),
            (profile.RegistryDefId, profile.Code, profile.SpecJson, profile.UpdatedByUserId, profile.UpdatedAt,
             profile.NameL10n.Values["en"]));
    }

    [Theory]
    [Trait("Directive", "RT-06")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("""{"sheet": "a",}""")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Специфікація_не_JSON_обєкт_відхиляється_при_створенні(string spec)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new RegistryImportProfile(5, EcrCode.Create("P1"), Name("P"), spec, 7, Created));
    }

    [Theory]
    [Trait("Directive", "RT-06")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Порожня_специфікація_відхиляється(string? spec)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new RegistryImportProfile(5, EcrCode.Create("P1"), Name("P"), spec!, 7, Created));
    }

    [Fact]
    [Trait("Directive", "RT-06")]
    public void Невалідна_специфікація_при_оновленні_не_змінює_профіль()
    {
        var profile = new RegistryImportProfile(5, EcrCode.Create("P1"), Name("Old"), Spec, 7, Created);

        Assert.ThrowsAny<ArgumentException>(() => profile.Update(Name("New"), "{", 9, Created.AddHours(1)));

        // Перевірка йде ДО присвоєння: половинчастої зміни (нова назва + стара
        // специфікація) бути не може.
        Assert.Equal((Spec, "Old", 7, Created),
            (profile.SpecJson, profile.NameL10n.Values["en"], profile.UpdatedByUserId, profile.UpdatedAt));
    }

    [Theory]
    [Trait("Directive", "RT-06")]
    [InlineData(0)]
    [InlineData(-3)]
    public void Профіль_без_довідника_відхиляється(int registryDefId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RegistryImportProfile(registryDefId, EcrCode.Create("P1"), Name("P"), Spec, 7, Created));
    }

    [Fact]
    [Trait("Directive", "RT-06")]
    public void Профіль_без_назви_відхиляється()
    {
        Assert.Throws<ArgumentNullException>(
            () => new RegistryImportProfile(5, EcrCode.Create("P1"), null!, Spec, 7, Created));
    }

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
