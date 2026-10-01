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
}
