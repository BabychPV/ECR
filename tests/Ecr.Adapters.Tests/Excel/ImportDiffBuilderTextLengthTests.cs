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
/// RC5: текст із книги довший за стовпець сховища (<c>ColumnDef.MaxStringLength</c>)
/// відхиляється в ПЕРЕГЛЯДІ однією коміркою, а не стає зміною, на якій
/// застосування відхиляє весь пакет.
/// </summary>
public sealed class ImportDiffBuilderTextLengthTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "R1";
    private const long RowId = 1001;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "RC5")]
    public void Текст_довший_за_стовпець_відхиляється_в_перегляді_з_ключем()
    {
        var (table, column) = Table();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = new string('я', ColumnDef.MaxStringLength + 1);

        var diff = Build(worksheet, table, column);

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(ImportMessageKeys.ValueTooLong, rejection.MessageKey);
        Assert.Equal("ECR-CELL-0422", rejection.ReasonCode);
        Assert.Equal(column.Code, rejection.ColumnCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "RC5")]
    public void Текст_рівно_на_межі_стовпця_є_звичайною_зміною()
    {
        var (table, column) = Table();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        var atLimit = new string('я', ColumnDef.MaxStringLength);
        worksheet.Cell(2, 1).Value = atLimit;

        var diff = Build(worksheet, table, column);

        Assert.Empty(diff.Rejected);
        Assert.Equal(atLimit, Assert.Single(diff.Changes).NewValue);
    }

    private static (TableDef Table, ColumnDef Column) Table()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var column = builder.Column(table, "C1", CellDataType.String);
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
