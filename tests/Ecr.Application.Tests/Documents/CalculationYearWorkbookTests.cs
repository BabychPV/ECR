using Ecr.Application.Documents.CalculationYear;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Книга огляду «Calculation results» за рік (<see cref="CalculationYearWorkbook"/>): місяці колонками, довгий
/// перелік, «як відфільтровано», числа без втрати знаків; і правило адитивності одиниць.
/// </summary>
/// <remarks>Мутація (локально): прибрати фільтр <c>keep</c> у <c>Sheets</c> — червоніє тест «як відфільтровано».</remarks>
public sealed class CalculationYearWorkbookTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Місяці_колонками_несуть_значення_суму_і_позначку_застарілого_місяця()
    {
        var sheets = CalculationYearWorkbook.Sheets(View(), new CalculationYearExportSpec(2026, "pivot", null), "en");

        var results = sheets[0];
        Assert.Equal("Results", results.SheetName);
        Assert.Equal(9 + 12 + 2, results.Columns.Count);
        Assert.Equal("Feb 2026 (out of date)", results.Columns[10].Header);
        Assert.Equal(CellDataType.Decimal, results.Columns[9].Kind);
        Assert.Equal(2, results.Rows.Count);

        var tonnes = results.Rows[0];
        Assert.Equal(["Sheet 1", "1.2 Combustion", "Boiler B-1", "ES-001", "Stationary combustion", "2.0", "E_CO2", "CO2", "t"], tonnes.Take(9));
        Assert.Equal("1.5", tonnes[9]);
        Assert.Equal("2.123456789012345678", tonnes[10]);
        Assert.Null(tonnes[11]);
        Assert.Equal("3.623456789012345678", tonnes[21]);
        Assert.Equal("2", tonnes[22]);

        // Неадитивний вихід: суми і числа місяців немає.
        Assert.Null(results.Rows[1][21]);
        Assert.Null(results.Rows[1][22]);

        var months = sheets[1];
        Assert.Equal(12, months.Rows.Count);
        Assert.Equal(["2026-02", "open", "outOfDate", "2026-02-14 09:12"], months.Rows[1]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Довгий_перелік_як_відфільтровано_дає_рядок_на_вихід_і_місяць_зі_значенням()
    {
        var sheets = CalculationYearWorkbook.Sheets(View(), new CalculationYearExportSpec(2026, "long", ["r-rate"]), "en");

        var rows = sheets[0].Rows;
        Assert.Single(rows);
        Assert.Equal("RATE", rows[0][6]);
        Assert.Equal("2026-01", rows[0][9]);
        Assert.Equal("current", rows[0][10]);
        Assert.Equal("0.7", rows[0][11]);
    }

    [Theory]
    [InlineData("Mass", true)]
    [InlineData("Energy", true)]
    [InlineData("Volume", true)]
    [InlineData("StdVolume", true)]
    [InlineData("MassFlow", false)]
    [InlineData("MassPerVolume", false)]
    [InlineData("Dimensionless", false)]
    [InlineData("", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "CALC-RESULTS")]
    public void Сума_за_рік_лише_для_маси_енергії_й_обʼєму(string dimension, bool additive)
        => Assert.Equal(additive, CalculationYearViewBuilder.AdditiveDimensions.Contains(dimension));

    private static CalculationYearViewDto View()
    {
        static LocalizedText T(string en) => new(new Dictionary<string, string> { ["en"] = en });
        var months = Enumerable.Range(1, 12).Select(m => new CalculationYearMonthDto(
            m, 202600 + m, m <= 2 ? "open" : "notOpened", m == 1 ? "current" : m == 2 ? "outOfDate" : "notOpened",
            m == 2 ? new DateTime(2026, 2, 14, 9, 12, 0, DateTimeKind.Utc) : null)).ToList();
        decimal?[] tonnes = [1.5m, 2.123456789012345678m, null, null, null, null, null, null, null, null, null, null];
        decimal?[] rates = [0.7m, null, null, null, null, null, null, null, null, null, null, null];

        return new CalculationYearViewDto(
            7, 2026, months,
            [new CalculationYearTableDto(1, "S1", T("Sheet 1"), null, 10, "1.2", T("Combustion"))],
            [new CalculationYearEquipmentDto(0, "R1", null, "Boiler B-1", "ES-001")],
            [new CalculationYearMethodologyDto(5, "M-01", T("Stationary combustion"), "2.0")],
            [new CalculationYearSubstanceDto(3, "CO2", T("Carbon dioxide"))],
            [new CalculationYearUnitDto(1, "t", null, "Mass", true), new CalculationYearUnitDto(2, "kg_per_s", null, "MassFlow", false)],
            [],
            [
                new CalculationYearRowDto("r-co2", 0, 0, "E_CO2", null, 0, 0, tonnes, "vvnnnnnnnnnn", 3.623456789012345678m, 2),
                new CalculationYearRowDto("r-rate", 0, 0, "RATE", null, null, 1, rates, "vann" + new string('n', 8), null, 0),
            ],
            false);
    }
}
