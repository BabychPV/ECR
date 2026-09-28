// tests/Ecr.Infrastructure.Tests/Reporting/ReportWideRowsTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Entities.Reporting;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// P4 (перф-аудит): <c>WideRows</c> складає комірки групи в словник замість
/// лінійного пошуку на кожну колонку. Результат мусить бути ТИМ САМИМ, що й
/// у старої форми, — включно з «перша комірка коду виграє».
/// </summary>
public sealed class ReportWideRowsTests
{
    private static readonly ReportColumnSpec[] Columns =
    [
        new("OutputCode", ReportSourceColumns.Text),
        new("Value", ReportSourceColumns.Number),
        new("UnitCode", ReportSourceColumns.Text),
        new("Taken", "date"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P4")]
    public void Словник_колонок_дає_той_самий_результат_що_й_лінійний_пошук()
    {
        var cells = new List<ReportRow>
        {
            // Рядки не за порядком, комірки рядка — теж.
            Cell(3, "Value", null, 7m),
            Cell(1, "UnitCode", "t", null),
            Cell(1, "OutputCode", "E_CO2", null),
            Cell(1, "Value", null, 12.5000m),
            Cell(2, "OutputCode", "E_NOX", null),

            // Рядок 2 без Value і UnitCode: порожні значення, а не пропуск ключа.
            Cell(3, "OutputCode", "E_SO2", null),
            Cell(3, "Taken", null, null, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)),

            // Код, якого опис не знає: у рядок не потрапляє.
            Cell(2, "Stray", "x", null),
        };

        var actual = ReportSnapshotBuilder.WideRows(cells, Columns);

        Assert.Equal(Render(Reference(cells, Columns)), Render(actual));
        Assert.Equal([1, 2, 3], actual.Select(r => r.RowNo));
        Assert.Equal(["OutputCode", "Value", "UnitCode", "Taken"], actual[0].Cells.Keys);
        Assert.Null(actual[1].Cells["Value"]);
        Assert.False(actual[1].Cells.ContainsKey("Stray"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P4")]
    public void Дублікат_коду_колонки_в_рядку_дає_першу_комірку()
    {
        // ⛔ Саме тут словник найлегше зламати: `ToDictionary` на комірках кинув
        // би, а «останній виграє» (індексатор) мовчки змінив би значення.
        var cells = new List<ReportRow>
        {
            Cell(1, "OutputCode", "FIRST", null),
            Cell(1, "Value", null, 1m),
            Cell(1, "OutputCode", "SECOND", null),
            Cell(1, "Value", null, 2m),
        };

        var row = Assert.Single(ReportSnapshotBuilder.WideRows(cells, Columns));

        Assert.Equal("FIRST", row.Cells["OutputCode"]);
        Assert.Equal(1m, row.Cells["Value"]);
        Assert.Equal(Render(Reference(cells, Columns)), Render([row]));
    }

    /// <summary>Стара форма <c>WideRows</c> дослівно: пошук комірки на кожну колонку.</summary>
    private static List<SnapshotRow> Reference(
        IReadOnlyList<ReportRow> cells, IReadOnlyList<ReportColumnSpec> columns)
        => [.. cells
            .GroupBy(c => c.RowNo)
            .OrderBy(g => g.Key)
            .Select(g => new SnapshotRow(
                g.Key,
                columns.ToDictionary(
                    c => c.Code,
                    c => ReportSnapshotBuilder.WideRows([.. g.Where(x => x.ColumnCode == c.Code).Take(1)], [c])
                        is [var only] ? only.Cells[c.Code] : null,
                    StringComparer.Ordinal)))];

    private static string Render(IEnumerable<SnapshotRow> rows)
        => string.Join(
            '\n',
            rows.Select(r => $"{r.RowNo}:" + string.Join(
                ';', r.Cells.Select(c => $"{c.Key}={Convert.ToString(c.Value, System.Globalization.CultureInfo.InvariantCulture)}"))));

    private static ReportRow Cell(int rowNo, string code, string? text, decimal? number, DateTime? date = null)
    {
        var row = new ReportRow(1, rowNo, code);
        row.SetValue(text, number, date);
        return row;
    }
}
