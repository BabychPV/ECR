using System.Text.Json;
using Ecr.MethodologyImport.Export;
using Ecr.MethodologyImport.Reading;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Правила рядків із дзеркала <c>AF_EmissionCalculationWork_Rules</c>: СИНТЕТИЧНІ рядки (вигадані коди, таблиці й
/// значення), без БД і без реальних даних.
/// </summary>
public sealed class LegacyRulesTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById(LegacyMirrorReader.DefaultTimeZone);
    private static readonly DateTime Start = new(2024, 1, 1);

    private static LegacyFormulaRow Formula(string m, string name = "F1")
        => new(m, "V1", name, "V1", "CST.k1_", "CST.k1_", Start, new DateTime(9999, 2, 20), true, "", Guid.NewGuid().ToString());

    private static LegacyConstantRow Constant(string m)
        => new(m, "V1", "k1_", "Common", "V1", "1", "p", Start, null, null, Guid.NewGuid().ToString());

    private static LegacyRuleRow Rule(
        string m, string name, string table, string parameters, string values,
        string version = "V1", bool available = true, DateTime? end = null)
        => new(m, "V1", name, version, $"{m}_V1_{name}_{version}", available, Start, end ?? new DateTime(9999, 2, 20),
            table, parameters, values, Guid.NewGuid().ToString());

    private static MethodologyRulesPackage Package(IEnumerable<LegacyRuleRow> rules, string? only = null, params string[] methodologies)
    {
        var ms = methodologies.Length == 0 ? ["M_A"] : methodologies;
        var (model, readStats, _) = LegacyMirrorReader.Build(
            ms.Select(m => Formula(m)), ms.Select(Constant), Atyrau, only, rules);
        var (analysed, _) = AnalyzeCommand.Analyze(model, readStats);
        return MethodologyRulesPackage.From(analysed);
    }

    [Fact]
    public void Умови_розбираються_за_позицією_кінцевий_крапка_з_комою_порожнього_елемента_не_дає()
    {
        var package = Package([Rule("M_A", "Rule_001", "T_X", "ColA;ColB;", "alpha - альфа;beta - бета;")]);

        var rule = Assert.Single(package.Methodologies[0].Versions[0].Rules);
        Assert.Equal("Rule_001", rule.Code);
        Assert.Equal("T_X", rule.Table);
        Assert.False(rule.CatchAll);
        Assert.Equal(
            [new PackageRuleCondition("ColA", "alpha - альфа"), new PackageRuleCondition("ColB", "beta - бета")],
            rule.Conditions);
    }

    [Fact]
    public void Зірочка_у_параметрах_це_уся_таблиця_без_умов()
    {
        var package = Package([Rule("M_A", "Rule_001", "T_X", "*", "*")]);

        var rule = Assert.Single(package.Methodologies[0].Versions[0].Rules);
        Assert.True(rule.CatchAll);
        Assert.Empty(rule.Conditions);
        Assert.Equal(1, package.Totals.CatchAll);
        Assert.Equal(0, package.Totals.Conditional);
    }

    [Fact]
    public void Кількість_параметрів_і_значень_не_збігається_правило_не_потрапляє_в_пакет_і_звітується()
    {
        var package = Package(
        [
            Rule("M_A", "Rule_001", "T_X", "ColA;ColB;", "only-one;"),
            Rule("M_A", "Rule_002", "T_X", "ColA;", "ok;"),
        ]);

        Assert.Equal("Rule_002", Assert.Single(package.Methodologies[0].Versions[0].Rules).Code);
        var problem = Assert.Single(package.Problems);
        Assert.Equal("malformed", problem.Kind);
        Assert.Equal("Rule_001", problem.Rule);
        Assert.Equal("parameterValueCountMismatch:2/1", problem.Detail);
    }

    [Fact]
    public void Правило_без_таблиці_або_без_параметрів_є_проблемою()
    {
        var package = Package(
        [
            Rule("M_A", "Rule_001", "", "ColA;", "x;"),
            Rule("M_A", "Rule_002", "T_X", "", ""),
        ]);

        Assert.Empty(package.Methodologies);
        Assert.Equal(["noParameters", "noTable"], package.Problems.Select(p => p.Detail).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Правило_методології_якої_немає_серед_формул_не_потрапляє_в_пакет()
    {
        var package = Package([Rule("M_A", "Rule_001", "T_X", "*", "*"), Rule("M_Ghost", "Rule_001", "T_X", "*", "*")]);

        Assert.Equal("M_A", Assert.Single(package.Methodologies).Name);
        var problem = Assert.Single(package.Problems);
        Assert.Equal(("methodologyNotImported", "M_Ghost"), (problem.Kind, problem.Methodology));
    }

    [Fact]
    public void Версії_правила_мають_різні_коди_а_недоступні_лишаються_у_пакеті_з_прапорцем()
    {
        var package = Package(
        [
            Rule("M_A", "Rule_001", "T_X", "ColA;", "x;", "V1", available: false),
            Rule("M_A", "Rule_001", "T_X", "ColA;", "y;", "V2", available: false),
        ]);

        var rules = package.Methodologies[0].Versions[0].Rules;
        Assert.Equal(["Rule_001", "Rule_001_V2"], rules.Select(r => r.Code));
        Assert.All(rules, r => Assert.False(r.IsAvailable));
        Assert.Equal((2, 0, 2), (package.Totals.Rules, package.Totals.Available, package.Totals.Unavailable));
    }

    [Fact]
    public void Уся_таблиця_разом_з_умовними_правилами_версії_звітується_як_змішана_область()
    {
        var package = Package(
        [
            Rule("M_A", "Rule_001", "T_X", "ColA;", "x;"),
            Rule("M_A", "Rule_002", "T_Y", "*", "*"),
            Rule("M_B", "Rule_001", "T_X", "*", "*"),
            Rule("M_B", "Rule_002", "T_Y", "*", "*"),
        ], null, "M_A", "M_B");

        var problem = Assert.Single(package.Problems);
        Assert.Equal(("catchAllInMixedScope", "M_A"), (problem.Kind, problem.Methodology));
        Assert.Equal(["T_X", "T_Y"], package.Methodologies[1].Versions[0].Tables);
    }

    [Fact]
    public void Фільтр_методології_застосовується_і_до_правил()
    {
        var package = Package(
            [Rule("M_A", "Rule_001", "T_X", "*", "*"), Rule("M_B", "Rule_001", "T_X", "*", "*")],
            "M_B", "M_A", "M_B");

        Assert.Equal("M_B", Assert.Single(package.Methodologies).Name);
    }

    [Fact]
    public void Пакет_правил_детермінований_і_не_залежить_від_порядку_рядків()
    {
        var rows = new[]
        {
            Rule("M_A", "Rule_002", "T_X", "ColA;", "y;"),
            Rule("M_A", "Rule_001", "T_X", "ColA;", "x;"),
            Rule("M_A", "Rule_003", "T_Y", "*", "*"),
        };

        Assert.Equal(Package(rows).ToJson(), Package(rows.Reverse()).ToJson());
    }

    [Fact]
    public void Дати_правила_переводяться_в_UTC_рядки_AF_як_у_формул()
    {
        var package = Package([Rule("M_A", "Rule_001", "T_X", "*", "*", end: new DateTime(2025, 12, 31, 23, 59, 59))]);

        var rule = Assert.Single(package.Methodologies[0].Versions[0].Rules);
        Assert.Equal("2023-12-31T19:00:00Z", rule.StartDate);
        Assert.Equal("2025-12-31T18:59:59Z", rule.EndDate);
    }

    [Fact]
    public void Пакет_формул_і_констант_від_правил_не_змінюється()
    {
        var formulas = new[] { Formula("M_A") };
        var constants = new[] { Constant("M_A") };
        var without = LegacyMirrorReader.Build(formulas, constants, Atyrau);
        var with = LegacyMirrorReader.Build(formulas, constants, Atyrau, null, [Rule("M_A", "Rule_001", "T_X", "ColA;", "x;")]);

        var a = AnalyzeCommand.Analyze(without.Model, without.ReadStats);
        var b = AnalyzeCommand.Analyze(with.Model, with.ReadStats);

        Assert.Equal(
            MethodologyPackage.From(a.Model, a.Report, "Common").ToJson(),
            MethodologyPackage.From(b.Model, b.Report, "Common").ToJson());
        Assert.Single(b.Model.Rules);   // нормалізація посилань правила не губить
    }

    [Fact]
    public void Пакет_правил_серіалізується_у_камелкейс_з_форматом_і_версією()
    {
        var json = Package([Rule("M_A", "Rule_001", "T_X", "ColA;", "x;")]).ToJson();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("ecr-methodology-rules", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("totals").GetProperty("rules").GetInt32());
    }

    [Fact]
    public void Рівень_Module_ставиться_лише_методології_HSE400_решта_без_поля_рівня()
    {
        var (model, readStats, _) = LegacyMirrorReader.Build(
            [Formula("HSE400"), Formula("M_A")], [Constant("HSE400"), Constant("M_A")], Atyrau);
        var (analysed, report) = AnalyzeCommand.Analyze(model, readStats);
        var package = MethodologyPackage.From(analysed, report, "Common");

        Assert.Equal("Module", package.Methodologies.Single(m => m.Name == "HSE400").Versions[0].Level);
        Assert.Null(package.Methodologies.Single(m => m.Name == "M_A").Versions[0].Level);
        Assert.DoesNotContain("\"level\": null", package.ToJson());
    }
}
