using System.Globalization;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Аудит `C1` (enterprise, 2026-09-28): число з книги не залежить від культури
/// сервера.
/// </summary>
/// <remarks>
/// ⛔ Доти числова комірка йшла через <c>cell.GetString()</c>, що форматує
/// <c>double</c> ПОТОЧНОЮ культурою, а далі — <c>decimal.TryParse(…,
/// NumberStyles.Number, InvariantCulture)</c>. На сервері з uk-UA 12.5 ставало
/// «12,5», кома читалася як роздільник тисяч — і в базу йшло 125. А 0.00001
/// («1E-05») відхилялось як «не число» за будь-якої культури.
/// </remarks>
public sealed class ImportDiffBuilderCultureTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const long RowId = 1001;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    public void Числа_з_книги_читаються_однаково_під_культурою_з_десятковою_комою()
    {
        // ⛔ Мутація: повернути `GetString()` + `TryParse(NumberStyles.Number)`
        // для числових комірок — 12.5 стане 125, а 0.00001 — відмовою.
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
            CultureInfo.CurrentUICulture = new CultureInfo("uk-UA");

            var (table, columns) = Table(("N1", CellDataType.Decimal), ("N2", CellDataType.Decimal),
                                         ("T1", CellDataType.Decimal), ("T2", CellDataType.Decimal));

            using var workbook = Reopen(worksheet =>
            {
                worksheet.Cell(2, 1).Value = 12.5;
                worksheet.Cell(2, 2).Value = 0.00001;

                // Текстові комірки лишаються на шляху розбору тексту.
                worksheet.Cell(2, 3).SetValue("7.25");
                worksheet.Cell(2, 4).SetValue("2E-05");
            });

            Assert.Equal(XLDataType.Number, workbook.Worksheet("S0").Cell(2, 1).DataType);
            Assert.Equal(XLDataType.Text, workbook.Worksheet("S0").Cell(2, 3).DataType);

            var diff = Build(workbook.Worksheet("S0"), table, columns);

            var values = diff.Changes.ToDictionary(change => change.ColumnCode, change => change.NewValue);
            Assert.Equal(12.5m, Assert.IsType<decimal>(values["N1"]));
            Assert.Empty(diff.Rejected);
            Assert.Equal(0.00001m, Assert.IsType<decimal>(values["N2"]));
            Assert.Equal(7.25m, Assert.IsType<decimal>(values["T1"]));
            Assert.Equal(0.00002m, Assert.IsType<decimal>(values["T2"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    public void Число_з_17_значущими_цифрами_не_обрізається_до_15()
    {
        // ⛔ Мутація: замінити круговий "R"-шлях на `(decimal)double` — той
        // округлює до 15 значущих цифр, і 123456789.12345679 стане
        // 123456789.123457: тиха втрата точності, якої текстовий шлях не мав.
        //
        // ⚠ Книга тут навмисно лише в пам'яті: `SaveAs` ClosedXML сам пише
        // число 15 значущими цифрами, і тест перевіряв би бібліотеку, а не
        // читання. Справжній Excel зберігає всі 17.
        var (table, columns) = Table(("N1", CellDataType.Decimal));

        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("S0").Cell(2, 1).Value = 123456789.12345679;

        var diff = Build(workbook.Worksheet("S0"), table, columns);

        Assert.Empty(diff.Rejected);
        Assert.Equal(123456789.12345679m, Assert.IsType<decimal>(Assert.Single(diff.Changes).NewValue));
    }

    /// <summary>Книга проходить через справжній <c>.xlsx</c>, а не лише через об'єкт у пам'яті.</summary>
    private static XLWorkbook Reopen(Action<IXLWorksheet> fill)
    {
        using var stream = new MemoryStream();

        using (var source = new XLWorkbook())
        {
            fill(source.Worksheets.Add("S0"));
            source.SaveAs(stream);
        }

        stream.Position = 0;

        return new XLWorkbook(stream);
    }

    private static (TableDef Table, IReadOnlyList<ColumnDef> Columns) Table(params (string Code, CellDataType Type)[] specs)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var columns = specs.Select(spec => builder.Column(table, spec.Code, spec.Type)).ToList();
        builder.Row(table, "R1", 1);

        return (table, columns);
    }

    private static TableDiff Build(IXLWorksheet worksheet, TableDef table, IReadOnlyList<ColumnDef> columns)
        => new ImportDiffBuilder().Build(
            worksheet,
            new ExcelTableBlock(
                TableInstance, table.Id, table.Code, "S0", HeaderRow: 1,
                Columns: [.. columns.Select((column, index) => new ExcelColumnRef(column.Id, column.Code, index + 1, false, null))],
                Rows: [new ExcelRowRef("R1", 2)]),
            PeriodKeyValue,
            table,
            new Dictionary<CellAddress, EditDecision>(),
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["R1"] = "0x0A" },
            []);
}
