// tests/Ecr.Adapters.Tests/Excel/ImportDiffBuilderFormulaOverrideTests.cs
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
/// ✎ Аудит 31.10 (F1, D-461): повторний імпорт книги не перетворює застарілий результат формули на ручне значення.
/// </summary>
/// <remarks>
/// ⛔ Комірка формули, що дозволяє ручне значення, у книзі лишилася такою, як її поклав експорт, а система після
/// вивантаження перерахувала її. Доти перегляд бачив «книга ≠ поточне» й записував застаріле число як ручне
/// значення («Excel import») — і перерахунок обходив його назавжди. Незмінена комірка — не правка людини.
/// </remarks>
public sealed class ImportDiffBuilderFormulaOverrideTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "7001001";
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Незмінена_комірка_формули_перерахована_після_експорту_не_стає_ручним_значенням()
    {
        var (table, calc) = Table();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 10;

        // Експорт поклав 10 і відбиток; система потім перерахувала комірку до 12.
        var block = Block(calc, worksheet, fingerprint: true);

        var diff = Build(worksheet, block, table, calc, current: 12m);

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Змінена_людиною_комірка_формули_стає_ручним_значенням()
    {
        var (table, calc) = Table();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 10;

        var block = Block(calc, worksheet, fingerprint: true);

        // Людина вписала своє число поверх того, що поклав експорт.
        worksheet.Cell(2, 1).Value = 15;

        var diff = Build(worksheet, block, table, calc, current: 12m);

        var change = Assert.Single(diff.Changes);
        Assert.True(change.ReplacesFormula);
        Assert.Equal(15m, change.NewValue);
        Assert.Empty(diff.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Книга_без_відбитка_до_AN_118_розбіжність_вважається_правкою_людини()
    {
        // ⚠ Задокументована межа: без відбитка експорту (книга до P3/AN-118) «не чіпали» від «змінили» не
        // відрізнити, тож розбіжність, як і доти, — ручне значення; перегляд показує його як `replacesFormula`.
        var (table, calc) = Table();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 10;

        var block = Block(calc, worksheet, fingerprint: false);

        var diff = Build(worksheet, block, table, calc, current: 12m);

        var change = Assert.Single(diff.Changes);
        Assert.True(change.ReplacesFormula);
    }

    private static (TableDef Table, ColumnDef Calc) Table()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var calc = builder.Column(table, "Calc", CellDataType.Calculated);
        builder.Row(table, RowKey, 1);
        var formula = builder.Formula(table, "1 + 1", column: calc);
        formula.SetAllowManualOverride(true);
        return (table, calc);
    }

    private static TableDiff Build(IXLWorksheet worksheet, ExcelTableBlock block, TableDef table, ColumnDef calc, decimal current)
    {
        var address = new CellAddress(Period, RowId, calc.Id);
        var decisions = new Dictionary<CellAddress, EditDecision>
        {
            [address] = new EditDecision(true, EditDenyReason.None) { ViaFormulaOverride = true },
        };
        var existing = new[]
        {
            new CellRecord(address, table.Id, new CellValueData { ValueNumeric = current }),
        };

        return new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            decisions, new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            new Dictionary<string, long>(StringComparer.Ordinal) { [RowKey] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { [RowKey] = "0x0A" },
            existing);
    }

    private static ExcelTableBlock Block(ColumnDef calc, IXLWorksheet worksheet, bool fingerprint)
    {
        IReadOnlyList<ExcelColumnRef> columns = [new ExcelColumnRef(calc.Id, "Calc", 1, IsCalculated: true, null)];

        return new ExcelTableBlock(
            TableInstance, TableDefId: 0, "T0", "S0", HeaderRow: 1,
            Columns: columns,
            Rows:
            [
                new ExcelRowRef(
                    RowKey, 2,
                    Calc: fingerprint ? CalculatedCellFingerprint.OfRow(worksheet, columns, 2) : null),
            ]);
    }
}
