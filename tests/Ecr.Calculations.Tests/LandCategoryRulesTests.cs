// tests/Ecr.Calculations.Tests/LandCategoryRulesTests.cs
using System.Text.Json;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// LP-07: правила категорії константи методологій C11/C12 Land (відтворені з C#-класів старого HSE-сервісу,
/// файл <c>af-land-category-rules.json</c>) обирають ту саму категорію, що й старий код, а
/// <see cref="ConstantResolver"/> зіставляє NAE без урахування пробілів, як <c>Trim().Replace(" ", "")</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати крок «без пробілів» у <c>ConstantResolver</c> — червоні
/// <see cref="Ключ_з_пробілом_знаходить_категорію_без_пробілу"/> і passthrough-випадки; змінити елемент групи
/// у правилі — червоний відповідний <see cref="Правило_дає_категорію_старого_коду"/>.
/// </remarks>
public sealed class LandCategoryRulesTests
{
    private const string RulesFile = "docs/delivery/reference-data/land-template/af-land-category-rules.json";

    private static MethodologyConstant Number(string category, decimal value)
        => RuleStand.Number("EF", value, category);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Ключ_з_пробілом_знаходить_категорію_без_пробілу()
    {
        var resolver = new ConstantResolver(NSubstitute.Substitute.For<Ecr.Application.Ports.IConstantStore>());
        MethodologyConstant[] candidates = [Number("6482(003)", 1m), Number("6490", 2m)];

        var found = resolver.Resolve(candidates, 1, "EF", "6482 (003)", null, new DateOnly(2026, 1, 31));

        Assert.Equal(1m, found?.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Точний_збіг_виграє_над_збігом_без_пробілів()
    {
        var resolver = new ConstantResolver(NSubstitute.Substitute.For<Ecr.Application.Ports.IConstantStore>());
        MethodologyConstant[] candidates = [Number("6585 (001)", 1m), Number("6585(001)", 2m)];

        var found = resolver.Resolve(candidates, 1, "EF", "6585(001)", null, new DateOnly(2026, 1, 31));

        Assert.Equal(2m, found?.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Два_збіги_без_пробілів_лишаються_неоднозначними()
    {
        var resolver = new ConstantResolver(NSubstitute.Substitute.For<Ecr.Application.Ports.IConstantStore>());
        MethodologyConstant[] candidates = [Number("6585 (001)", 1m), Number("6585( 001)", 2m)];

        var error = Assert.Throws<DomainException>(
            () => resolver.Resolve(candidates, 1, "EF", "6585(001)", null, new DateOnly(2026, 1, 31)));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    public static TheoryData<string, string, string> Cases() => new()
    {
        { "ECW_C11_03", "0801", "Loc_BeforeMR_A" },
        { "ECW_C11_03", "0116", "Loc_BeforeMR_B" },
        { "ECW_C11_03", "0673", "Loc_BeforeMR_D" },
        { "ECW_C11_07", "0901(002)", "0900" },
        { "ECW_C11_07", "0901 (002)", "0900" },
        { "ECW_C11_07", "0904(001)", "0904" },
        { "ECW_C11_07", "0907(002)", "0906" },
        { "ECW_C11_08", "0787(003)", "0786" },
        { "ECW_C11_08", "0118", "0117" },
        { "ECW_C11_08", "0900(001)", "0900" },
        { "ECW_C11_08", "0907 (001)", "0906" },
        { "ECW_C11_11_01", "0668", "0667" },
        { "ECW_C11_11_01", "0678", "0674" },
        { "ECW_C11_11_01", "0600", "0600" },
        { "ECW_C12_05", "0061(002)", "Loc_BeforeMR_B" },
        { "ECW_C12_05", "0050 (004)", "Loc_BeforeMR_D" },
        { "ECW_C12_05", "0079", "Loc_BeforeMR_D" },
        { "ECW_C11_14", "6482(003)", "6482(003)" },
        { "ECW_C11_14", "6490", "6490" },
        { "ECW_C11_14", "6482 (003)", "6482(003)" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [MemberData(nameof(Cases))]
    public async Task Правило_дає_категорію_старого_коду(string methodology, string nae, string expectedCategory)
    {
        var rule = LoadRules()[methodology];

        // Усі категорії випадків цієї методології + сусідні: кожній свій коефіцієнт.
        var categories = Cases().Select(c => ((object[])c)).Where(c => (string)c[0] == methodology)
            .Select(c => (string)c[2]).Distinct().Order().ToList();
        var stand = new RuleStand(rule)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants([.. categories.Select((c, i) => Number(c, i + 1))]);

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_NAE", nae));

        var expected = categories.IndexOf(expectedCategory) + 1;
        var totals = output.Values.Where(v => v.OutputCode == "Total").ToList();
        Assert.NotEmpty(totals);
        Assert.All(totals, v => Assert.Equal(expected, v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task NAE_поза_групами_відмовляє_рядку_як_старий_код()
    {
        // Старий C11_03: «No correct category» -> continue (рядок пропущено).
        var stand = new RuleStand(LoadRules()["ECW_C11_03"])
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(Number("Loc_BeforeMR_A", 1m), Number("Loc_BeforeMR_B", 2m));

        var error = await Assert.ThrowsAsync<DomainException>(
            () => stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_NAE", "9999")));

        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", error.Details?["messageKey"]);
    }

    private static Dictionary<string, string> LoadRules()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, RulesFile)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, RulesFile)));

        return doc.RootElement.GetProperty("rules").EnumerateArray().ToDictionary(
            r => r.GetProperty("methodology").GetString()!,
            r => r.GetProperty("expression").GetString()!,
            StringComparer.Ordinal);
    }
}
