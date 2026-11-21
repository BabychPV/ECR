using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>UI-тест 20.11: ключ періоду <c>202601</c> у тексті відмови — назва місяця мовою читача.</summary>
public sealed class PeriodParameterLocalizationTests
{
    private static UiStringCatalog Catalog(string lang, string pattern, string month1)
        => new(lang, 1, new Dictionary<string, string> { ["periods.monthOf"] = pattern, ["periods.month.1"] = month1 });

    private static Dictionary<string, string> Params(string value)
        => new() { ["sourcePeriodKey"] = value, ["other"] = "202601" };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("en", "{month} {year}", "January", "January 2026")]
    [InlineData("ru", "{month} {year}", "Январь", "Январь 2026")]
    [InlineData("kz", "{month} {year}", "Қаңтар", "Қаңтар 2026")]
    public void Ключ_місяця_джерела_стає_назвою_місяця(string lang, string pattern, string month, string expected)
    {
        var result = UiStringResolver.WithLocalizedPeriods(Catalog(lang, pattern, month), Params("202601"));

        Assert.Equal(expected, result["sourcePeriodKey"]);
        Assert.Equal("202601", result["other"]); // чужі підстановки не чіпає
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("202613")] // місяця 13 немає
    [InlineData("202600")]
    [InlineData("abc")]
    [InlineData("")]
    public void Не_місяць_лишається_як_є(string raw)
    {
        var result = UiStringResolver.WithLocalizedPeriods(Catalog("en", "{month} {year}", "January"), Params(raw));

        Assert.Equal(raw, result["sourcePeriodKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Немає_ключів_місяця_в_каталозі_лишається_сирий_ключ()
    {
        var result = UiStringResolver.WithLocalizedPeriods(
            new UiStringCatalog("en", 1, new Dictionary<string, string>()), Params("202601"));

        Assert.Equal("202601", result["sourcePeriodKey"]);
    }
}
