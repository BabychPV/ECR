// tests/Ecr.Adapters.Tests/Excel/ImportDiffBuilderRoundTripTests.cs
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
/// `V-10`: прев'ю порівнює комірку книги з тим, що в ній лежало б після
/// ЕКСПОРТУ, — тому незмінена експортована книга не дає ні змін, ні відмов.
/// </summary>
/// <remarks>
/// ⚠ Наскрізний доказ (експорт → імпорт → Apply на справжньому SQL Server) —
/// <c>ImportRoundTripScenarios</c>. Тут — окремі форми даних, яких сценарій
/// через API не відтворить: порожній рядок у базі, число в колонці дати
/// (слід `V-04`), формула в обчислюваній комірці з «чужим» кешем.
/// </remarks>
public sealed class ImportDiffBuilderRoundTripTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "R4";
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожній_рядок_у_базі_і_порожня_комірка_книги_не_зміна()
    {
        // ⛔ Фантом DOC-000001: «R4 C1 '' → —». У базі — `ValueString = ''`,
        // експорт пише порожню комірку, імпорт читає `null`.
        var (table, column) = Table(CellDataType.String);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");

        var diff = Build(worksheet, table, column, new CellValueData { ValueString = string.Empty });

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Незмінене_число_обчислюваної_колонки_пропускається_а_змінене_відхиляється()
    {
        var (table, column) = Table(CellDataType.Formula);
        var existing = new CellValueData { ValueNumeric = 10m, IsCalculated = true };

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 10;

        var unchanged = Build(worksheet, table, column, existing);

        Assert.Empty(unchanged.Changes);
        Assert.Empty(unchanged.Rejected);

        // А ЗМІНЕНЕ користувачем обчислюване число — відмова, як і було.
        worksheet.Cell(2, 1).Value = 11;

        var changed = Build(worksheet, table, column, existing);

        Assert.Empty(changed.Changes);
        Assert.Equal("ECR-CELL-4221", Assert.Single(changed.Rejected).ReasonCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Формула_в_обчислюваній_комірці_не_порівнюється_з_базою()
    {
        // ⚠ Excel перераховує формулу, щойно користувач змінить вхідну комірку
        // поруч, — і кешований результат розійдеться з базою. Це не правка
        // користувача: формулу туди поклав експорт.
        var (table, column) = Table(CellDataType.Formula);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 2).Value = 21;
        worksheet.Cell(2, 1).FormulaA1 = "B2*2";

        var diff = Build(worksheet, table, column, new CellValueData { ValueNumeric = 10m, IsCalculated = true });

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Read_only_комірка_з_числом_не_свого_типу_не_відхиляється_на_незміненій_книзі()
    {
        // ⛔ DOC-000009, рядок RTOT: у колонці дати лежить ЧИСЛО (слід `V-04`),
        // експорт дату не пише — комірка книги порожня. Доти `Same()` бачив
        // непорожнє значення і відхиляв комірку, якої ніхто не чіпав.
        var (table, column) = Table(CellDataType.Date);
        column.SetReadOnly(true);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");

        var diff = Build(worksheet, table, column, new CellValueData { ValueNumeric = 19.75m });

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);
    }

    private static (TableDef Table, ColumnDef Column) Table(CellDataType type)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var column = builder.Column(table, "C1", type);
        builder.Row(table, RowKey, 1);

        return (table, column);
    }

    private static TableDiff Build(IXLWorksheet worksheet, TableDef table, ColumnDef column, CellValueData existing)
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
            [new CellRecord(new CellAddress(Period, RowId, column.Id), table.Id, existing)]);
}
