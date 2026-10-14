// tests/Ecr.Architecture.Tests/LandC0707CategoryRuleSourcesTests.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LAND-5094, діагноз Е: правило категорії ECW_C07_07 живе у двох місцях - файл
/// <c>af-land-category-rules.json</c> (етап LandParity нової версії) і <c>$CAT</c> у <c>Build-LandTemplate.ps1</c>
/// (перший імпорт). Обидва мають гілки Kerosene (Jet fuel/Avgas/Kerosene, Solvent/Kerosene) за старим
/// <c>ECW_C07_07.cs:104-114</c>, а імена записів у файлі дослівно збігаються з довідником LAND_OTHER_TANKFARM моделі.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): прибрати у файлі правил порівняння 'Solvent/Kerosene - …' - червоніє
/// <see cref="Правило_у_файлі_має_гілку_Kerosene_для_обох_записів_і_імена_є_у_довіднику"/>; прибрати 'Jet fuel'
/// зі списку <c>$sm</c> у Build-LandTemplate.ps1 - червоніє
/// <see cref="Build_LandTemplate_має_гілку_Kerosene_для_C07_07"/>.
/// </remarks>
public sealed class LandC0707CategoryRuleSourcesTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static string RuleExpression()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "af-land-category-rules.json")));

        return doc.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("methodology").GetString() == "ECW_C07_07")
            .GetProperty("expression").GetString()!;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_у_файлі_має_гілку_Kerosene_для_обох_записів_і_імена_є_у_довіднику()
    {
        var expression = RuleExpression();
        var literals = Regex.Matches(expression, @"'NAME'\) = '([^']+)'").Select(m => m.Groups[1].Value).ToList();

        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));
        var tankFarm = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_OTHER_TANKFARM")
            .GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("code").GetString()!, e => e.GetProperty("name").GetString()!);

        var jet = tankFarm["JETFUELAVGASKEROSENE"];
        var solvent = tankFarm["SOLVENTKEROSENE"];

        Assert.Contains(jet, literals);
        Assert.Contains(solvent, literals);
        Assert.Contains("'Kerosene'", expression, StringComparison.Ordinal);
        // Усі літерали, крім дормантного 'Fuel oil' (запису в довіднику немає), - записи довідника.
        var names = tankFarm.Values.ToHashSet(StringComparer.Ordinal);
        Assert.All(literals.Where(l => !l.StartsWith("Fuel oil - ", StringComparison.Ordinal)),
            l => Assert.Contains(l, names));
        Assert.Contains("'Fuel'", expression, StringComparison.Ordinal);
        Assert.Equal(["Land_TypeStorMaterial"],
            Regex.Matches(expression, "@([A-Za-z_][A-Za-z0-9_]*)").Select(m => m.Groups[1].Value).Distinct().ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_LandTemplate_має_гілку_Kerosene_для_C07_07()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1"));
        var line = script.Split('\n').Single(l => l.TrimStart().StartsWith("ECW_C07_07 = \"if(REGFIELD", StringComparison.Ordinal));

        Assert.Contains("$($sm['Jet fuel'])", line, StringComparison.Ordinal);
        Assert.Contains("$($sm.Solvent)", line, StringComparison.Ordinal);
        Assert.Contains("'Kerosene'", line, StringComparison.Ordinal);
        Assert.Contains("$smFuelTail", line, StringComparison.Ordinal);
        Assert.Contains(
            "foreach ($n in 'Diesel', 'Gasoline', 'Bilge water', 'Lubricant', 'Recovered oil', 'Jet fuel', 'Solvent')",
            script,
            StringComparison.Ordinal);
    }
}
