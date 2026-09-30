using System.Globalization;
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
/// ФВ-9.16b: перегляд імпорту КАЖЕ, що число округлено (<see cref="ImportChange.RoundedFrom"/>),
/// і перевіряє <c>Precision</c> УЖЕ округленого числа — тим самим правилом, що
/// <c>ColumnDef.ValidateValue</c> на застосуванні.
/// </summary>
public sealed class ImportDiffBuilderRoundingMarkTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "R1";
    private const long RowId = 1001;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    [InlineData(2, "2.345", "2.35")]
    [InlineData(0, "-2.5", "-3")]
    [InlineData(2, "0.004", "0")]
    public void Округлена_зміна_несе_число_з_файлу_до_округлення(int scale, string cellText, string expected)
    {
        var (table, column) = Table((byte)scale);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = decimal.Parse(cellText, CultureInfo.InvariantCulture);

        var change = Assert.Single(Build(worksheet, table, column).Changes);

        Assert.Equal(Parse(expected), Assert.IsType<decimal>(change.NewValue));
        Assert.Equal(Parse(cellText), Assert.IsType<decimal>(change.RoundedFrom));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    // У межах Scale — нічого не округлено.
    [InlineData((byte)3, "1.234")]
    // Хвостові нулі текстом: `1.230` → `1.23` числом не змінюється.
    [InlineData((byte)2, "1.230")]
    // Колонка без Scale не округлює взагалі.
    [InlineData(null, "1.23456789")]
    public void Неокруглена_зміна_не_має_позначки(byte? scale, string cellText)
    {
        var (table, column) = Table(scale);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = cellText;

        var change = Assert.Single(Build(worksheet, table, column).Changes);

        Assert.Null(change.RoundedFrom);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    public void Текстова_колонка_не_має_позначки_округлення()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("Water"), "Main");
        var column = builder.Column(table, "C1", CellDataType.String);
        builder.Row(table, RowKey, 1);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = "1.23456";

        var change = Assert.Single(Build(worksheet, table, column).Changes);

        Assert.Equal("1.23456", change.NewValue);
        Assert.Null(change.RoundedFrom);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    // Округлення додає розряд: 99.999 → 100.00 не вміщується в (4,2).
    [InlineData((byte)4, (byte)2, "99.999")]
    [InlineData((byte)4, (byte)2, "-99.995")]
    [InlineData((byte)3, (byte)2, "9.9999")]
    // Без округлення, але понад Precision — теж відмова в перегляді, а не 422 на Apply.
    [InlineData((byte)4, (byte)2, "123.45")]
    public void Число_що_після_округлення_не_вміщується_в_Precision_відхиляється_в_перегляді(
        byte precision, byte scale, string cellText)
    {
        var (table, column) = Table(scale, precision);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = decimal.Parse(cellText, CultureInfo.InvariantCulture);

        var diff = Build(worksheet, table, column);

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(ImportMessageKeys.Precision, rejection.MessageKey);
        Assert.Equal(CellValueReader.TypeMismatch, rejection.ReasonCode);

        // Те саме число застосування відхилило б — відмова в перегляді не
        // строгіша за запис, а рівно така сама.
        var rounded = decimal.Round(Parse(cellText), scale, MidpointRounding.AwayFromZero);
        Assert.Equal("ECR-CELL-0422", column.ValidateValue(new CellValueData { ValueNumeric = rounded }));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.16b")]
    // Межа знизу від переповнення: 99.994 → 99.99 вміщується в (4,2).
    [InlineData((byte)4, (byte)2, "99.994", "99.99")]
    [InlineData((byte)3, (byte)2, "9.994", "9.99")]
    public void Число_що_після_округлення_вміщується_в_Precision_приймається(
        byte precision, byte scale, string cellText, string expected)
    {
        var (table, column) = Table(scale, precision);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = decimal.Parse(cellText, CultureInfo.InvariantCulture);

        var diff = Build(worksheet, table, column);

        Assert.Empty(diff.Rejected);
        var change = Assert.Single(diff.Changes);
        var shown = Assert.IsType<decimal>(change.NewValue);
        Assert.Equal(Parse(expected), shown);
        Assert.Null(column.ValidateValue(new CellValueData { ValueNumeric = shown }));
    }

    private static decimal Parse(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

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

    private static TableDiff Build(IXLWorksheet worksheet, TableDef table, ColumnDef column)
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
            []);
}
