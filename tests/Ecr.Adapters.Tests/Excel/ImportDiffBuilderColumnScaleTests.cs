using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// ФВ-9.16b (<c>D-109</c>): імпорт <c>.xlsx</c> округлює число до
/// <c>ColumnDef.Scale</c> (<c>AwayFromZero</c>) в ПЕРЕГЛЯДІ, а ручне введення й
/// <c>PATCH</c> те саме число відхиляють. Показ = застосування: застосування
/// бере <c>NewValue</c> з плану, і <c>PATCH</c> його приймає.
/// </summary>
public sealed class ImportDiffBuilderColumnScaleTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "R1";
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    [InlineData(2, 2.344, "2.34")]
    [InlineData(2, 2.346, "2.35")]
    // Межове .5: від нуля, а не банківське (ToEven дало б 2.34 і -2.34).
    [InlineData(2, 2.345, "2.35")]
    [InlineData(2, -2.345, "-2.35")]
    [InlineData(2, -2.344, "-2.34")]
    // Scale = 0: цілі; .5 йде від нуля в обидва боки (ToEven дало б 2 і -2).
    [InlineData(0, 2.5, "3")]
    [InlineData(0, -2.5, "-3")]
    [InlineData(0, 0.4, "0")]
    [InlineData(0, -0.4, "0")]
    // Менше за половину останнього розряду — нуль, а не «без змін».
    [InlineData(2, 0.004, "0")]
    public void Число_з_зайвими_знаками_округлюється_до_Scale_колонки(int scale, double cellValue, string expected)
    {
        var (table, column) = Table((byte)scale);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = cellValue;

        var diff = Build(worksheet, table, column, []);

        Assert.Empty(diff.Rejected);
        var change = Assert.Single(diff.Changes);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            Assert.IsType<decimal>(change.NewValue));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    [InlineData(2, 2.345)]
    [InlineData(0, -2.5)]
    [InlineData(3, 1.23456)]
    public void Перегляд_показує_те_саме_що_приймає_запис(int scale, double cellValue)
    {
        var (table, column) = Table((byte)scale);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = cellValue;

        var diff = Build(worksheet, table, column, []);
        var shown = Assert.IsType<decimal>(Assert.Single(diff.Changes).NewValue);

        // Застосування передає в `PatchCell` саме `NewValue` з плану, а запис
        // проганяє його тим самим читачем і доменною перевіркою колонки.
        var written = CellValueReader.Read(shown, column);
        Assert.NotNull(written);
        Assert.Equal(shown, written.ValueNumeric);
        Assert.Null(column.ValidateValue(written));

        // І зворотний бік: число з книги як є `PATCH` відхилив би — тобто
        // округлення в перегляді не декорація, без нього застосування впало б.
        var raw = decimal.Parse(
            cellValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueNumeric = raw }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    public void Число_в_межах_Scale_і_колонка_без_Scale_лишаються_як_є()
    {
        var (table, column) = Table(scale: 3);
        var (freeTable, free) = Table(scale: null);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 1.234;

        Assert.Equal(1.234m, Assert.Single(Build(worksheet, table, column, []).Changes).NewValue);

        worksheet.Cell(2, 1).Value = 1.23456789;

        Assert.Equal(1.23456789m, Assert.Single(Build(worksheet, freeTable, free, []).Changes).NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    public void Scale_понад_масштаб_сховища_не_ламає_округлення()
    {
        // `decimal.Round` не приймає понад 28: Scale 34 не має туди потрапити.
        var (table, column) = Table(scale: 34, precision: 34);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 0.1d + 0.2d;

        var change = Assert.Single(Build(worksheet, table, column, []).Changes);

        Assert.Equal(0.3m, change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    public void Порожня_комірка_не_округлюється_і_не_є_зміною()
    {
        var (table, column) = Table(scale: 2);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Clear();

        var diff = Build(worksheet, table, column, []);

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    // Історичне значення понад Scale, книга не змінена — не «зміна».
    [InlineData(1.2345, "1.2345")]
    // Правка лише в знаках понад Scale, що округлюється до наявного, — не зміна.
    [InlineData(1.234, "1.23")]
    public void Значення_що_збігається_з_наявним_до_або_після_округлення_не_є_зміною(
        double cellValue, string stored)
    {
        var (table, column) = Table(scale: 2);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = cellValue;

        var existing = new[]
        {
            new CellRecord(
                new CellAddress(Period, RowId, column.Id), table.Id,
                new CellValueData
                {
                    ValueNumeric = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture),
                }),
        };

        Assert.Empty(Build(worksheet, table, column, existing).Changes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    public void Округлення_яке_виводить_число_за_межу_сховища_відхиляється_в_перегляді()
    {
        // Порядок: спершу округлення, потім перевірка цілої частини. Інакше
        // 999999999999999999.5 при Scale 0 (→ 10^18, 19 розрядів) пройшло б
        // перегляд, а застосування впало б для всієї книги.
        var (table, column) = Table(scale: 0);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = "999999999999999999.5";

        var diff = Build(worksheet, table, column, []);

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(ImportMessageKeys.IntegerDigits, rejection.MessageKey);
    }

    private static (TableDef Table, ColumnDef Column) Table(byte? scale, byte precision = 18)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var column = builder.Column(table, "C1", CellDataType.Decimal);
        column.SetNumericFormat(scale is null ? null : precision, scale);
        builder.Row(table, RowKey, 1);

        return (table, column);
    }

    private static TableDiff Build(
        IXLWorksheet worksheet, TableDef table, ColumnDef column, IReadOnlyList<CellRecord> current)
        => new ImportDiffBuilder().Build(
            worksheet,
            new ExcelTableBlock(
                TableInstance, table.Id, table.Code, "S0", HeaderRow: 1,
                Columns: [new ExcelColumnRef(column.Id, column.Code, 1, false, null)],
                Rows: [new ExcelRowRef(RowKey, 2)]),
            PeriodKeyValue,
            table,
            new Dictionary<CellAddress, EditDecision>(),
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            new Dictionary<string, long>(StringComparer.Ordinal) { [RowKey] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { [RowKey] = "0x0A" },
            current);
}
