// tests/Ecr.Application.Tests/Reporting/ReportRowRulesTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Reporting;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// R5 (<c>D-52a</c>): правила «умова → значення / приховати рядок» — відмова при
/// СТВОРЕННІ версії опису і застосування до одного рядка.
/// </summary>
public sealed class ReportRowRulesTests
{
    private static readonly ReportColumnCommand[] Columns = [new("OutputCode", "text"), new("Value", "number")];

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Правила_пишуться_схемою_2_а_схема_1_правил_не_приймає()
    {
        var rules = new ReportRuleCommand[]
        {
            new("[Value] = 1", new(Set: new("Value", "0"))),
            new("[Value] = NULL", new(HideRow: true)),
        };

        Assert.Equal(
            """{"rowSource":"CalculationResults","schema":2,"rules":["""
            + """{"when":"[Value] = 1","then":{"set":{"column":"Value","value":"0"}}},"""
            + """{"when":"[Value] = NULL","then":{"hideRow":true}}]}""",
            ReportDefinitionSpec.RulesJson(new("CalculationResults", Rules: rules), Columns));

        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.RulesJson(new("CalculationResults", 1, rules), Columns));
        Assert.Equal("schema", error.Details!["part"]);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("[Value] + 1", null, null, "when")] // умова не Boolean
    [InlineData("[Value] >", null, null, "when")] // не розбирається
    [InlineData("[Nope] = 1", null, null, "when")] // колонки джерело не має
    [InlineData("[Value] > 0", "UnitCode", "'t'", "column")] // колонка не описана у версії
    [InlineData("[Value] > 0", "Value", "'багато'", "value")] // текст у числову колонку
    [InlineData("[Value] > 0", "Value", "@Limit", "value")] // параметрів у схемі 2 немає
    public void Зламане_правило_відмовляє_при_створенні_версії_з_номером_і_частиною(
        string when, string? column, string? value, string part)
    {
        var broken = new ReportRuleCommand(
            when, column is null ? new(HideRow: true) : new(Set: new(column, value!)));

        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Rules: [new("[Value] = NULL", new(HideRow: true)), broken]), Columns));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.rule", error.Details!["messageKey"]);
        Assert.Equal("2", error.Details["ruleNo"]);
        Assert.Equal(part, error.Details["part"]);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Дія_правила_рівно_одна(bool set, bool hide)
    {
        var then = new ReportRuleThenCommand(set ? new("Value", "0") : null, hide ? true : null);

        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Rules: [new("[Value] > 0", then)]), Columns));

        Assert.Equal("then", error.Details!["part"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Присвоєння_бачить_наступне_правило()
    {
        var rules = Compile(
            new("[Value] < 0", new(Set: new("Value", "0"))),
            new("[Value] = 0", new(Set: new("OutputCode", "[OutputCode] & '*'"))),
            new("[OutputCode] = 'E_CO2*'", new(HideRow: true)));

        var touched = Row("E_NOX", -3m);
        Assert.True(rules.Apply(touched));
        Assert.Equal(0m, touched["Value"]);
        Assert.Equal("E_NOX*", touched["OutputCode"]);

        Assert.False(rules.Apply(Row("E_CO2", -1m)));

        var untouched = Row("E_CO2", 7m);
        Assert.True(rules.Apply(untouched));
        Assert.Equal(7m, untouched["Value"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Умова_null_правила_не_запускає()
    {
        // `[Value] > 1` над порожньою колонкою — null (02b §6.2), а не «так».
        var rules = Compile(
            new("[Value] > 1", new(HideRow: true)),
            new("[Value] > 1", new(Set: new("OutputCode", "'x'"))));

        var row = Row("E_CO2", null);

        Assert.True(rules.Apply(row));
        Assert.Equal("E_CO2", row["OutputCode"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Помилка_обчислення_на_рядку_падає_гучно_з_номером_правила()
    {
        var rules = Compile(
            new("[Value] = NULL", new(HideRow: true)),
            new("[Value] > 0", new(Set: new("Value", "1 / ([Value] - 5)"))));

        Assert.Equal(0.2m, Applied(rules, 10m));

        var error = Assert.Throws<BusinessRuleException>(() => rules.Apply(Row("E_CO2", 5m)));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("2", error.Details!["ruleNo"]);
        Assert.Equal("value", error.Details["part"]);
    }

    private static object? Applied(ReportRowRules rules, decimal value)
    {
        var row = Row("E_CO2", value);
        Assert.True(rules.Apply(row));
        return row["Value"];
    }

    private static ReportRowRules Compile(params ReportRuleCommand[] rules)
        => ReportRowRules.Compile(2, "CalculationResults", rules, ["OutputCode", "Value"]);

    private static Dictionary<string, object?> Row(string output, decimal? value)
        => new(StringComparer.Ordinal) { ["OutputCode"] = output, ["Value"] = value, ["UnitCode"] = "t" };
}
