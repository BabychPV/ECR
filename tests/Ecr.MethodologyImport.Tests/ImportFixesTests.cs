using System.Text.Json;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Export;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>Правки за проходом «Land»: тип Text, одиниці констант, потреба колонок (міні-фікстури, не 200-МБ файл).</summary>
public sealed class ImportFixesTests
{
    private static (MethodologyPackage Package, AnalysisReport Report) Run(AfXmlBuilder builder)
    {
        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(builder.Build()));
        return (MethodologyPackage.From(model, report, "Common"), report);
    }

    private static PackageFormula Formula(MethodologyPackage p, string methodology, string name)
        => p.Methodologies.Single(m => m.Name == methodology).Versions.Single().Formulas.Single(f => f.Name == name);

    [Theory]
    [InlineData("'Норматив'", "Text")]
    [InlineData("'a' + 'b'", "Text")]
    [InlineData("'a' & @X", "Text")]
    [InlineData("if(@X > 1, 'Перевищення', 'Норма')", "Text")]
    [InlineData("if(@X > 1, 'Перевищення', 2)", "Number")]
    [InlineData("@X * 2 + 1", "Number")]
    [InlineData("Round(@X + 1, 2)", "Number")]
    [InlineData("'it''s' + @X", "Text")]
    public void Тип_результату_виводиться_з_тексту_формули(string text, string expected)
    {
        var (package, _) = Run(new AfXmlBuilder().Formula("M", "V1", "F", "@X", text));

        Assert.Equal(expected, Formula(package, "M", "F").ResultType);
    }

    [Fact]
    public void Формула_що_посилається_на_текстову_формулу_або_константу_теж_Text()
    {
        var builder = new AfXmlBuilder()
            .Formula("M", "V1", "Base", "", "'abc'")
            .Formula("M", "V1", "Chain", "!Base", "!Base")
            .Formula("M", "V1", "ByConst", "CST.label", "CST.label")
            .Formula("M", "V1", "NumConst", "CST.k", "CST.k")
            .Constant("M", "V1", "label", "Низька")
            .Constant("M", "V1", "k", "0.5");

        var (package, _) = Run(builder);

        Assert.Equal("Text", Formula(package, "M", "Chain").ResultType);
        Assert.Equal("Text", Formula(package, "M", "ByConst").ResultType);
        Assert.Equal("Number", Formula(package, "M", "NumConst").ResultType);
    }

    [Fact]
    public void Звіт_рахує_текстові_формули_і_плюс_над_текстом_та_не_переписує_його()
    {
        var builder = new AfXmlBuilder()
            .Formula("M", "V1", "Concat", "@A", "@A + ' т'")
            .Formula("M", "V1", "Plain", "@A", "@A + 1");

        var (package, report) = Run(builder);

        Assert.NotNull(report.FormulaTypes);
        Assert.Equal(1, report.FormulaTypes!.Text);
        Assert.Equal(1, report.FormulaTypes.Number);
        Assert.Equal(1, report.FormulaTypes.TextPlusFormulas);
        Assert.Equal(["M/V1/Concat/V1"], report.FormulaTypes.TextPlusSamples);
        Assert.Equal("@A + ' т'", Formula(package, "M", "Concat").Text); // «+» не переписується мовчки
    }

    [Theory]
    [InlineData("kg", "kg")]
    [InlineData(" KG ", "kg")]
    [InlineData("кг", "kg")]
    [InlineData("kg/t", "kg_per_t")]
    [InlineData("kg / t", "kg_per_t")]
    [InlineData("mg/m³", "mg_per_m3")]
    [InlineData("Sm3", "Sm3")]
    [InlineData("nm3", "Nm3")]
    [InlineData("m3", "m3")]
    public void Одиниця_AF_зводиться_до_коду_каталогу(string raw, string expected)
    {
        var resolution = UnitCanonicalizer.Resolve(raw);

        Assert.Equal(expected, resolution.Code);
        Assert.Null(resolution.Reason);
    }

    [Theory]
    [InlineData("", UnitCanonicalizer.ReasonEmpty)]
    [InlineData("   ", UnitCanonicalizer.ReasonEmpty)]
    [InlineData("furlong", UnitCanonicalizer.ReasonUnknown)]
    public void Нерезолвна_одиниця_не_вгадується_а_має_причину(string raw, string reason)
    {
        var resolution = UnitCanonicalizer.Resolve(raw);

        Assert.Null(resolution.Code);
        Assert.Equal(reason, resolution.Reason);
    }

    [Fact]
    public void Пакет_несе_код_каталогу_для_зведеної_одиниці_і_сирий_рядок_для_нерезолвної()
    {
        var builder = new AfXmlBuilder()
            .Constant("M", "V1", "k1", "0.5", unit: "кг")
            .Constant("M", "V1", "k2", "3", unit: "furlong")
            .Constant("M", "V1", "k3", "7");

        var (package, report) = Run(builder);

        var units = package.Methodologies.Single().Versions.Single().Constants.ToDictionary(c => c.Name, c => c.Unit);
        Assert.Equal("kg", units["k1"]);
        Assert.Equal("furlong", units["k2"]);
        Assert.Equal(string.Empty, units["k3"]);

        Assert.NotNull(report.Units);
        Assert.Equal(3, report.Units!.Constants);
        Assert.Equal(1, report.Units.Resolved);
        Assert.Equal(1, report.Units.ResolvedByAlias);
        Assert.Equal(1, report.Units.WithoutUnit);
        var issue = Assert.Single(report.Units.Issues);
        Assert.Equal(("k2", "furlong", UnitCanonicalizer.ReasonUnknown), (issue.Constant, issue.RawUnit, issue.Reason));
    }

    [Fact]
    public void Аргументи_календаря_і_HSE400_виходять_у_звіт_як_потреба_колонок()
    {
        var builder = new AfXmlBuilder()
            .Formula("M", "V1", "ByMonth", "@Month;@X", "@X * @Month")
            .Formula("M", "V1", "ByDays", "@Days", "@Days")
            .Formula("M", "V1", "InText", "@X", "@X / @Days")
            .Formula("HSE400_Air", "V1", "Hse", "@X", "@X")
            .Formula("M", "V1", "None", "@X", "@X");

        var (_, report) = Run(builder);

        var needs = report.ColumnNeeds!.ToDictionary(n => (n.Kind, n.Methodology));
        Assert.Equal(1, needs[(ImportDiagnostics.CalendarMonth, "M")].Formulas);
        Assert.Equal(2, needs[(ImportDiagnostics.CalendarDays, "M")].Formulas);
        Assert.Equal(1, needs[(ImportDiagnostics.Hse400, "HSE400_Air")].Formulas);
        Assert.DoesNotContain(report.ColumnNeeds!, n => n.Samples.Contains("M/V1/None/V1"));
    }

    [Fact]
    public void Нові_поля_звіту_детерміновані_і_потрапляють_у_JSON()
    {
        var builder = new AfXmlBuilder()
            .Formula("M", "V1", "F", "@Month", "'a' + 'b'")
            .Constant("M", "V1", "k", "1", unit: "furlong");
        var xml = builder.Build();

        string Json() => Reporting.ReportWriter.ToJson(AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml)).Report);

        Assert.Equal(Json(), Json());
        using var doc = JsonDocument.Parse(Json());
        Assert.Equal(1, doc.RootElement.GetProperty("formulaTypes").GetProperty("text").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("units").GetProperty("unresolved").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("columnNeeds").GetArrayLength());
    }
}
