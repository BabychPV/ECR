// tests/Ecr.Application.Tests/Reporting/ReportLayoutTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// R8 (<c>D-52a</c>, <c>02b</c> §8a): макет з ОДНІЄЮ групою й підсумками —
/// відмова при створенні версії і розкладка на видачі.
/// </summary>
public sealed class ReportLayoutTests
{
    private static readonly ReportColumnCommand[] Columns =
        [new("OutputCode", "text"), new("Value", "number"), new("UnitCode", "text")];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Макет_пишеться_схемою_2_а_схема_1_його_не_приймає()
    {
        Assert.Equal(
            """{"rowSource":"CalculationResults","schema":2,"layout":"""
            + """{"groupBy":"UnitCode","totals":[{"column":"Value","fn":"sum"}],"showGroupHeader":true}}""",
            ReportDefinitionSpec.RulesJson(
                new(
                    "CalculationResults",
                    Layout: new("UnitCode", [new("Value", "sum")], ShowGroupHeader: true)),
                Columns));

        // ⛔ Опис без макета лишається побайтно тим самим, що й до R8.
        Assert.Equal(
            """{"rowSource":"CalculationResults","schema":1}""",
            ReportDefinitionSpec.RulesJson(new("CalculationResults"), Columns));

        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", 1, Layout: new("UnitCode")), Columns));

        Assert.Equal("schema", error.Details!["part"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("Nope", null, null, "groupBy")] // колонки групування в описі немає
    [InlineData(null, "Nope", "sum", "column")] // колонки підсумку в описі немає
    [InlineData(null, "Value", "median", "fn")] // функції немає в переліку
    [InlineData(null, "OutputCode", "sum", "fn")] // сума над текстовою колонкою
    [InlineData(null, "UnitCode", "avg", "fn")] // середнє над текстовою колонкою
    public void Зламаний_макет_відмовляє_при_створенні_версії_з_частиною(
        string? groupBy, string? column, string? fn, string part)
    {
        IReadOnlyList<ReportTotalCommand>? totals = column is null ? null : [new(column, fn!)];

        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Layout: new(groupBy, totals)), Columns));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.layout", error.Details!["messageKey"]);
        Assert.Equal(part, error.Details["part"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Підсумків_більше_за_стелю_відмовляють()
    {
        // ⚠ Число літералом, а не `MaxTotals + 1`: твердження проти константи
        // того самого модуля рухається разом із нею й нічого не тримає.
        Assert.Equal(20, ReportLayout.MaxTotals);

        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.RulesJson(
            new(
                "CalculationResults",
                Layout: new(Totals: [.. Enumerable.Repeat(new ReportTotalCommand("Value", "sum"), 21)])),
            Columns));

        Assert.Equal("totals", error.Details!["part"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Рядки_йдуть_за_групою_а_порожня_група_остання()
    {
        var view = Compile("UnitCode", ("Value", "sum")).Apply(Rows());

        // «kg» < «t» < порожнє: номери рядків показують саме перестановку.
        Assert.Equal([2, 4, 1, 3, 5], view.Rows.Select(r => r.RowNo));
        Assert.Equal(["kg", "t", null], view.Groups.Select(g => g.Value as string));
        Assert.Equal([2, 2, 1], view.Groups.Select(g => g.RowCount));
        Assert.All(view.Groups, g => Assert.Equal("UnitCode", g.Column));

        // Підсумок групи — по її рядках, підсумок зрізу — по всіх.
        Assert.Equal([3m, 15m, 1m], view.Groups.Select(g => Assert.Single(g.Totals).Value));
        Assert.Equal(19m, Assert.Single(view.Totals).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Макет_без_групування_дає_плаский_перелік_і_один_підсумок()
    {
        var view = Compile(null, ("Value", "count")).Apply(Rows());

        Assert.Equal([1, 2, 3, 4, 5], view.Rows.Select(r => r.RowNo));
        Assert.Empty(view.Groups);
        Assert.Equal(4m, Assert.Single(view.Totals).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожня_комірка_не_рахується_ні_сумою_ні_кількістю_ні_знаменником_середнього()
    {
        // ⛔ Мутація, якою перевірено цей тест: у `ReportLayout.Fold` рахувати
        // `avg` як `numbers.Sum() / cells.Count` (тобто брати `null` у
        // знаменник). Падає РІВНО цей тест: середнє стає 19/5 = 3.8.
        //
        // ⚠ `null` — «не вимірювали», а не нуль. Середнє з нулями замість
        // порожніх колонок менше за справжнє рівно настільки, наскільки звіт
        // неповний, і виглядає як нормальне число.
        var view = Compile(null, ("Value", "sum"), ("Value", "count"), ("Value", "avg")).Apply(Rows());

        Assert.Equal([19m, 4m, 4.75m], view.Totals.Select(t => t.Value));

        // Колонка, у якій порожньо все: сума й середнє — порожньо, кількість — нуль.
        var empty = Compile(null, ("Value", "sum"), ("Value", "count"), ("Value", "avg"))
            .Apply([Row(1, "E_CO2", null, "t")]);

        Assert.Equal([null, 0m, null], empty.Totals.Select(t => t.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Min_і_max_працюють_і_над_текстом_і_над_числом()
    {
        var view = Compile(null, ("Value", "min"), ("Value", "max"), ("OutputCode", "min"), ("UnitCode", "max"))
            .Apply(Rows());

        Assert.Equal([1m, 10m, "E_CO2", "t"], view.Totals.Select(t => t.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Макет_читається_зі_збереженої_версії_а_схема_1_його_не_має()
    {
        var layout = ReportLayout.Of(
            ReportDefinitionSpec.RulesJson(
                new("CalculationResults", Layout: new("UnitCode", [new("Value", "SUM")], ShowGroupHeader: true)),
                Columns),
            Columns);

        Assert.False(layout.IsEmpty);
        Assert.Equal("UnitCode", layout.GroupBy);
        Assert.True(layout.ShowGroupHeader);

        // Ім'я функції регістру не розрізняє, а зберігається нормалізованим.
        Assert.Equal("sum", Assert.Single(layout.Apply(Rows()).Totals).Fn);

        Assert.True(ReportLayout.Of("""{"rowSource":"CalculationResults","schema":1}""", Columns).IsEmpty);
        Assert.True(ReportLayout.Of(null, Columns).IsEmpty);

        // Схема 2 без секції `layout` — теж «макету немає», а не порожній макет.
        Assert.True(ReportLayout.Of(
            """{"rowSource":"CalculationResults","schema":2}""", Columns).IsEmpty);
    }

    private static ReportLayout Compile(string? groupBy, params (string Column, string Fn)[] totals)
        => ReportLayout.Compile(
            ReportRowRules.Schema,
            new ReportLayoutCommand(groupBy, [.. totals.Select(t => new ReportTotalCommand(t.Column, t.Fn))]),
            Columns);

    /// <summary>П'ять рядків: дві групи, порожня група і порожнє число.</summary>
    private static IReadOnlyList<SnapshotRow> Rows() =>
    [
        Row(1, "E_CO2", 10m, "t"),
        Row(2, "E_NOX", null, "kg"),
        Row(3, "E_SO2", 5m, "t"),
        Row(4, "E_CO2", 3m, "kg"),
        Row(5, "E_NOX", 1m, null),
    ];

    private static SnapshotRow Row(int rowNo, string output, decimal? value, string? unit)
        => new(
            rowNo,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["OutputCode"] = output,
                ["Value"] = value,
                ["UnitCode"] = unit,
            });
}
