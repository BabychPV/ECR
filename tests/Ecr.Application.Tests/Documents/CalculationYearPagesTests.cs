using Ecr.Application.Documents.CalculationYear;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Зведення й сторінки огляду року (<see cref="CalculationYearPages"/>) і фільтри (<see cref="CalculationYearFilter"/>):
/// лічильники таблиць, «проблеми», пошук будь-якою мовою, сторінка з довідниками лише своїх рядків, ключ таблиці.
/// Мутація (локально): у <c>IsProblem</c> прибрати перевірку застарілого місяця — червоніє «застаріле значення».
/// </summary>
public sealed class CalculationYearPagesTests
{
    private static readonly string[] Nothing = [];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Зведення_рахує_виходи_й_проблеми_по_таблицях_у_порядку_огляду_без_чисел()
    {
        var view = View();
        var summary = CalculationYearPages.Summary(view, CalculationYearFilter.None);

        Assert.Equal(["10", "20~4", "0"], summary.Tables.Select(t => t.Key));
        Assert.Equal([2, 1, 1], summary.Tables.Select(t => t.Rows));
        Assert.Equal([2, 1, 1], summary.Tables.Select(t => t.Matched));
        Assert.Equal([1, 1, 0], summary.Tables.Select(t => t.Problems));
        Assert.Equal((4, 4, 2), (summary.TotalRows, summary.MatchedRows, summary.ProblemRows));
        Assert.True(summary.HasFormulas);
        Assert.Equal(["M-01"], summary.Methodologies.Select(m => m.Code));
        Assert.Equal(["CO2"], summary.Substances.Select(s => s.Code));

        var problems = CalculationYearPages.Summary(view, new CalculationYearFilter(ProblemsOnly: true));
        Assert.Equal([1, 1, 0], problems.Tables.Select(t => t.Matched));
        Assert.Equal([2, 1, 1], problems.Tables.Select(t => t.Rows));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Застаріле_значення_й_пропуск_усередині_року_проблема_пропуск_до_першого_ні()
    {
        var months = View().Months;
        CalculationYearRowDto R(string states) => new("x", 0, 0, "E", null, null, null, new decimal?[12], states, null, 0);

        Assert.True(CalculationYearFilter.IsProblem(months, R("vvnnnnnnnnnn"))); // лютий застарілий
        Assert.False(CalculationYearFilter.IsProblem(months, R("vannnnnnnnnn")));
        Assert.True(CalculationYearFilter.IsProblem(months, R("vavnnnnnnnnn")));
        Assert.False(CalculationYearFilter.IsProblem(months, R("aannnnnnnnnn")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Фільтри_пошук_будь_якою_мовою_аркуш_методологія_формула_речовина()
    {
        var view = View();
        string[] Ids(CalculationYearFilter f) => [.. view.Rows.Where(r => f.Matches(view, r)).Select(r => r.Id)];

        Assert.Equal(["r-flare"], Ids(new CalculationYearFilter(Query: "es-002")));
        Assert.Equal(["r-ghg"], Ids(new CalculationYearFilter(Query: "ИТОГ")));
        Assert.Equal(["r-ghg"], Ids(new CalculationYearFilter(Sheet: 2)));
        Assert.Equal(["r-ghg"], Ids(new CalculationYearFilter(Methodology: CalculationYearFilter.FormulaMethodology)));
        Assert.Equal(["r-co2", "r-flare"], Ids(new CalculationYearFilter(Methodology: "M-01", Substance: "CO2")));
        Assert.Equal(Nothing, Ids(new CalculationYearFilter(Methodology: "m-01")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Сторінка_лише_своєї_таблиці_зсув_і_довідники_лише_своїх_рядків()
    {
        var view = View();
        var first = CalculationYearPages.Page(view, "10", CalculationYearFilter.None, 0, 1);
        Assert.Equal((2, 0), (first.Total, first.Offset));
        Assert.Equal(["r-co2"], first.Rows.Select(r => r.Id));

        var second = CalculationYearPages.Page(view, "10", CalculationYearFilter.None, 1, 1);
        var row = Assert.Single(second.Rows);
        Assert.Equal("r-flare", row.Id);

        // Індекси переписано під сторінку: одне обладнання, одна одиниця, одна речовина — нульові.
        Assert.Equal((0, 0, 0, 0), (row.Equipment, row.Methodology, row.Substance, row.Unit));
        Assert.Equal("Flare FS-101", Assert.Single(second.Equipment).Name);
        Assert.Equal("t", Assert.Single(second.Units).Code);
        Assert.Empty(second.Columns);

        var formula = CalculationYearPages.Page(view, "20~4", CalculationYearFilter.None, 0, 200);
        Assert.Equal(0, Assert.Single(formula.Rows).Column);
        Assert.Equal("GHG_TOTAL", Assert.Single(formula.Columns).Code);
        Assert.Empty(formula.Methodologies);

        Assert.Equal(0, CalculationYearPages.Page(view, "999", CalculationYearFilter.None, 0, 200).Total);
    }

    [Theory]
    [InlineData("10", true, 10, null)]
    [InlineData("20~4", true, 20, 4L)]
    [InlineData("0", true, 0, null)]
    [InlineData("0~4", false, 0, null)]
    [InlineData("abc", false, 0, null)]
    [InlineData("1~", false, 0, null)]
    [InlineData("1~2~3", false, 0, null)]
    [InlineData("-1", false, 0, null)]
    [InlineData("", false, 0, null)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Ключ_таблиці_розбирається_лише_у_своїй_формі(string key, bool ok, int def, long? copy)
    {
        Assert.Equal(ok, CalculationYearPages.TryParseKey(key, out var slice));
        if (ok)
        {
            Assert.Equal(new CalculationYearSlice(def, copy), slice);
            Assert.Equal(key, CalculationYearPages.KeyOf(slice));
        }
    }

    /// <summary>
    /// Огляд: таблиця 10 (аркуш 1) — два виходи методології; таблиця 20 копії 4 (аркуш 2) — формула шаблону; таблиця 0
    /// (без таблиці) — один вихід. Лютий застарілий.
    /// </summary>
    private static CalculationYearViewDto View()
    {
        static LocalizedText T(string en, string? ru = null)
            => new(ru is null ? new Dictionary<string, string> { ["en"] = en } : new Dictionary<string, string> { ["en"] = en, ["ru"] = ru });
        static decimal?[] V(params decimal?[] first) => [.. first, .. Enumerable.Repeat<decimal?>(null, 12 - first.Length)];
        var months = Enumerable.Range(1, 12).Select(m => new CalculationYearMonthDto(
            m, 202600 + m, m <= 3 ? "open" : "notOpened", m == 1 ? "current" : m == 2 ? "outOfDate" : m == 3 ? "noRun" : "notOpened",
            null)).ToList();

        return new CalculationYearViewDto(
            7, 2026, months,
            [
                new CalculationYearTableDto(1, "AIR", T("Air"), null, 10, "T1", T("Combustion")),
                new CalculationYearTableDto(2, "GHG", T("GHG"), 4, 20, "T4", T("Direct GHG")),
                new CalculationYearTableDto(0, "", new LocalizedText(), null, 0, "", new LocalizedText()),
            ],
            [
                new CalculationYearEquipmentDto(0, "R1", null, "Boiler B-1", "ES-001"),
                new CalculationYearEquipmentDto(0, "R2", null, "Flare FS-101", "ES-002"),
                new CalculationYearEquipmentDto(1, "R9", T("Total site", "Итог площадки"), null, null),
                new CalculationYearEquipmentDto(2, "OLD", null, null, null),
            ],
            [new CalculationYearMethodologyDto(5, "M-01", T("Stationary combustion"), "2.0")],
            [new CalculationYearSubstanceDto(3, "CO2", T("Carbon dioxide"))],
            [new CalculationYearUnitDto(1, "t", null, "Mass", true)],
            [new CalculationYearColumnDto(99, "GHG_TOTAL", T("GHG total"))],
            [
                new CalculationYearRowDto("r-co2", 0, 0, "E_CO2", null, 0, 0, V(1.5m, 2.5m), "vvnnnnnnnnnn", 4m, 2),
                new CalculationYearRowDto("r-flare", 1, 0, "E_CO2", null, 0, 0, V(3m), "vannnnnnnnnn", 3m, 1),
                new CalculationYearRowDto("r-ghg", 2, null, "GHG_TOTAL", 0, null, 0, V(10m, 11m), "vvnnnnnnnnnn", 21m, 2),
                new CalculationYearRowDto("r-old", 3, 0, "E_OLD", null, null, 0, V(1m), "vannnnnnnnnn", 1m, 1),
            ],
            false);
    }
}
