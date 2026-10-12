using System.Text.Json;
using Ecr.MethodologyImport.Export;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

public sealed class PackageExportTests
{
    private static MethodologyPackage Package(string xml)
    {
        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml));
        return MethodologyPackage.From(model, report, "Common");
    }

    private static AfXmlBuilder Sample()
        => new AfXmlBuilder()
            .Formula(" Common ", "V1", "Common_A", " CST.k1_ ; @Date ", "CST.k1_ * 2")
            .Constant("Common", "V1", "k1_", "0.5")
            .Constant("Common", "V1", "k1_", "0.7", category: "Loc_B")
            .Formula("Flert", "V1", "Uses", "!Common_A;CST.k1_", "!Common_A");

    [Fact]
    public void Пакет_групує_методологія_версія_формули_константи_з_Trim()
    {
        var package = Package(Sample().Build());

        Assert.Equal("ecr-methodology-package", package.Format);
        Assert.Equal(1, package.Version);
        Assert.Empty(package.Blockers);
        Assert.Equal(["Common", "Flert"], package.Methodologies.Select(m => m.Name));

        var common = Assert.Single(package.Methodologies[0].Versions);
        Assert.Equal("V1", common.Version);
        var formula = Assert.Single(common.Formulas);
        Assert.Equal("Common_A", formula.Name);
        Assert.Equal("CST.k1_ ; @Date", formula.Arguments); // зовнішні пробіли знято, внутрішні — як у AF
        Assert.True(formula.IsAvailable);

        var constant = Assert.Single(common.Constants);
        Assert.Equal("k1_", constant.Name);
        Assert.Equal(["Common", "Loc_B"], constant.Values.Select(v => v.Category));
        Assert.Equal(["0.5", "0.7"], constant.Values.Select(v => v.Value));
    }

    [Fact]
    public void Пакет_детермінований_і_не_залежить_від_порядку_елементів()
    {
        var builder = Sample();

        var a = Package(builder.Build()).ToJson();
        var b = Package(builder.Build(reverse: true)).ToJson();

        Assert.Equal(a, b);
        using var doc = JsonDocument.Parse(a);
        Assert.Equal("ecr-methodology-package", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("blockers").GetArrayLength());
    }

    [Fact]
    public void Блокери_потрапляють_у_пакет_щоб_імпортер_міг_відмовити()
    {
        var package = Package(Sample().Formula("Flert", "V1", "Broken", "!Nope", "1").Build());

        Assert.Contains(package.Blockers, b => b.StartsWith("UNRESOLVED_REFERENCES", StringComparison.Ordinal));
    }

    [Fact]
    public void Категорійні_формули_Land_мають_scope_Row_і_версія_отримує_categoryRule()
    {
        var xml = new AfXmlBuilder()
            .Formula("ECW_C05_01", "V1", "ECW_RepairStatus", "", "'Repair'")
            .Formula("ECW_C05_01", "V1", "ECW_Category", "", "'Cat1'")
            .Formula("ECW_C05_01", "V1", "ECW_Location", "!ECW_RepairStatus;!ECW_Category", "!ECW_RepairStatus + '_' + !ECW_Category")
            .Formula("ECW_C05_01", "V1", "Other", "", "1")
            .Formula("Plain", "V1", "ECW_Other", "", "2")
            .Build();

        var package = Package(xml);

        var land = package.Methodologies.Single(m => m.Name == "ECW_C05_01").Versions.Single();
        Assert.All(land.Formulas.Where(f => f.Name.StartsWith("ECW_", StringComparison.Ordinal)),
            f => Assert.Equal("Row", f.Scope));
        Assert.Null(land.Formulas.Single(f => f.Name == "Other").Scope);
        Assert.Equal("!ECW_Location", land.CategoryRule?.Expression);

        var plain = package.Methodologies.Single(m => m.Name == "Plain").Versions.Single();
        Assert.Null(plain.CategoryRule);
        Assert.Null(plain.Formulas.Single().Scope);

        using var doc = JsonDocument.Parse(package.ToJson());
        var versions = doc.RootElement.GetProperty("methodologies").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "ECW_C05_01").GetProperty("versions")[0];
        Assert.Equal("!ECW_Location", versions.GetProperty("categoryRule").GetProperty("expression").GetString());
        var plainJson = doc.RootElement.GetProperty("methodologies").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "Plain").GetProperty("versions")[0];
        Assert.False(plainJson.TryGetProperty("categoryRule", out _));
        Assert.False(plainJson.GetProperty("formulas")[0].TryGetProperty("scope", out _));
    }

    /// <remarks>
    /// G12 (Land, 7.16): методологія з формулою <c>ECW_Category</c> без <c>ECW_Location</c> отримує правило
    /// <c>!ECW_Category</c>. Мутація: прибрати гілку <c>CategoryFormula</c> в <c>CategoryRuleOf</c> - тест червоний.
    /// </remarks>
    [Fact]
    public void Формула_ECW_Category_без_Location_дає_categoryRule_за_нею()
    {
        var xml = new AfXmlBuilder()
            .Formula("ECW_C07_16", "V1", "ECW_Category", "", "'Summer'")
            .Formula("ECW_C07_16", "V1", "ECW_EC_tons_333", "", "1")
            .Constant("ECW_C07_16", "V1", "k", "1", category: "Summer")
            .Constant("ECW_C07_16", "V1", "k", "2", category: "Winter")
            .Formula("ECW_C07_98", "V1", "ECW_Category", "", "'Summer'")
            .Constant("ECW_C07_98", "V1", "k", "1")
            .Formula("ECW_C07_99", "V1", "ECW_EC_tons_333", "", "1")
            .Build();

        var package = Package(xml);

        Assert.Equal(
            "!ECW_Category",
            package.Methodologies.Single(m => m.Name == "ECW_C07_16").Versions.Single().CategoryRule?.Expression);

        // G12: формула ECW_Category без категорійних констант (лише спільна категорія) правила не дає.
        Assert.Null(package.Methodologies.Single(m => m.Name == "ECW_C07_98").Versions.Single().CategoryRule);

        // Без жодної категорійної формули правила немає.
        Assert.Null(package.Methodologies.Single(m => m.Name == "ECW_C07_99").Versions.Single().CategoryRule);
    }
}
