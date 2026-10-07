using System.Text.Json;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Export;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// A2: вузол <c>categoryRule</c> пакета для методологій Land (мініфікстури XML, не 201-МБ файл). Експортер правило
/// не виводить з AF (його там немає — категорію ставить C#), а дописує з таблиці або файла; тут перевіряється, що
/// воно потрапляє у версію рівно коли виконано умови, і ніколи — у чужі методології.
/// </summary>
public sealed class CategoryRuleExportTests
{
    private const string Nested51 = "EmissionCalculationWork\\ECW_C05_Stationary\\ECW_C05_01";

    private static (MethodologyPackage Package, AnalysisReport Report) Run(
        AfXmlBuilder builder, IReadOnlyDictionary<string, string>? rules = null)
    {
        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(builder.Build()), categoryRules: rules);
        return (MethodologyPackage.From(model, report, "Common", rules), report);
    }

    private static AfXmlBuilder Methodology51()
        => new AfXmlBuilder()
            .Formula(Nested51, "V1", "ECW_RepairStatus", "@Land_OverhauledEngines", "'Loc_BeforeMR'")
            .Formula(Nested51, "V1", "ECW_Category", "@Power", "'B'")
            .Formula(Nested51, "V1", "ECW_Location", "!ECW_RepairStatus;!ECW_Category", "!ECW_RepairStatus + '_' + !ECW_Category")
            .Constant(Nested51, "V1", "EF_tons_NOx_", "40", category: "Loc_BeforeMR_B");

    private static PackageMethodology Find(MethodologyPackage p, string name) => p.Methodologies.Single(m => m.Name == name);

    [Fact]
    public void Методологія_5_1_отримує_правило_ECW_Location_з_вкладеного_шляху_AF()
    {
        var (package, _) = Run(Methodology51());

        var version = Assert.Single(Find(package, "ECW_C05_01").Versions);
        Assert.Equal("!ECW_Location", Assert.IsType<PackageCategoryRule>(version.CategoryRule).Expression);
    }

    [Fact]
    public void Пакет_пише_categoryRule_camelCase_а_методологія_поза_таблицею_його_не_має()
    {
        var builder = Methodology51().Formula("Common", "V1", "Shared", "@X", "@X");

        var (package, _) = Run(builder);

        using var doc = JsonDocument.Parse(package.ToJson());
        var methodologies = doc.RootElement.GetProperty("methodologies").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);
        var land = methodologies["ECW_C05_01"].GetProperty("versions")[0];
        Assert.Equal("!ECW_Location", land.GetProperty("categoryRule").GetProperty("expression").GetString());
        Assert.False(methodologies["Common"].GetProperty("versions")[0].TryGetProperty("categoryRule", out _));
    }

    [Fact]
    public void Правило_з_відсутньою_формулою_не_експортується_а_виходить_у_звіт()
    {
        var builder = new AfXmlBuilder().Formula(Nested51, "V1", "ECW_Category", "@Power", "'B'"); // ECW_Location немає

        var (package, report) = Run(builder);

        Assert.Null(Assert.Single(Find(package, "ECW_C05_01").Versions).CategoryRule);
        var status = Assert.Single(report.CategoryRules!, s => s.Methodology == "ECW_C05_01");
        Assert.Contains("ECW_Location", status.Issue);
    }

    [Fact]
    public void Методологія_з_таблиці_якої_немає_в_AF_звітується_з_порожньою_версією()
    {
        var (_, report) = Run(new AfXmlBuilder().Formula("Common", "V1", "Shared", "@X", "@X"));

        var missing = report.CategoryRules!.Where(s => s.Version.Length == 0).Select(s => s.Methodology).ToList();
        Assert.Contains("ECW_C06_01", missing);
        Assert.Contains("ECW_C05_01", missing);
        Assert.All(report.CategoryRules!, s => Assert.NotNull(s.Issue));
    }

    [Fact]
    public void Правило_6_1_за_паливом_іде_у_пакет_як_є_без_формул_категорії()
    {
        var builder = new AfXmlBuilder().Formula("EmissionCalculationWork\\ECW_C06_Mobile\\ECW_C06_01", "V1", "ECW_EC_tons_301", "@Land_TypeFuel", "1");

        var (package, _) = Run(builder);

        var expression = Find(package, "ECW_C06_01").Versions.Single().CategoryRule!.Expression;
        Assert.Equal(
            "if(@Land_TypeFuel = 'Diesel - Дизель', 'Diesel', if(@Land_TypeFuel = 'Gasoline - Бензин', 'Gasoline', ''))",
            expression);
    }

    [Fact]
    public void Порожній_словник_вимикає_правила_і_пакет_без_вузла()
    {
        var (package, _) = Run(Methodology51(), new Dictionary<string, string>());

        Assert.Null(Find(package, "ECW_C05_01").Versions.Single().CategoryRule);
        Assert.DoesNotContain("categoryRule", package.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Файл_правил_замінює_таблицю_і_не_чутливий_до_регістру_імені()
    {
        var rules = CategoryRules.ParseFile("""{ "ecw_c05_01": "!ECW_Category" }""");

        var (package, _) = Run(Methodology51(), rules);

        Assert.Equal("!ECW_Category", Find(package, "ECW_C05_01").Versions.Single().CategoryRule!.Expression);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "ECW_C05_01": "" }""")]
    [InlineData("""{ "ECW_C05_01": 5 }""")]
    public void Некоректний_файл_правил_відхиляється(string json)
        => Assert.Throws<FormatException>(() => CategoryRules.ParseFile(json));

    [Fact]
    public void Вирази_таблиці_Land_збалансовані_і_без_функцій_яких_немає_в_діалекті()
    {
        Assert.Equal(
            ["ECW_C05_01", "ECW_C05_02", "ECW_C05_04", "ECW_C06_01", "ECW_C07_07", "ECW_C07_07_01", "ECW_C07_08", "ECW_C07_16", "ECW_C07_21"],
            CategoryRules.LandDefaults.Keys.Order(StringComparer.Ordinal));

        foreach (var (methodology, expression) in CategoryRules.LandDefaults)
        {
            Assert.Equal(expression.Count(c => c == '('), expression.Count(c => c == ')'));
            Assert.Equal(0, expression.Count(c => c == '\'') % 2);
            foreach (var banned in new[] { "Contains(", "StartsWith(", "Concat(", "switch(" })
            {
                Assert.False(expression.Contains(banned, StringComparison.OrdinalIgnoreCase), $"{methodology}: {banned}");
            }
        }
    }
}
