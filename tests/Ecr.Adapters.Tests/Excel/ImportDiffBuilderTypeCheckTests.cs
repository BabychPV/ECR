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
/// `F-06`, `F-30` (UX-PASS, четвертий раунд): перегляд імпорту відхиляє те,
/// що відхилить запис, і каже це ключем, а не українським реченням.
/// </summary>
public sealed class ImportDiffBuilderTypeCheckTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "R1";
    private const long RowId = 1001;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F-06")]
    [InlineData(CellDataType.Decimal, "abc", ImportMessageKeys.ExpectsNumber)]
    [InlineData(CellDataType.Int, "abc", ImportMessageKeys.ExpectsNumber)]
    [InlineData(CellDataType.Bool, "maybe", ImportMessageKeys.ExpectsBoolean)]
    [InlineData(CellDataType.Date, "not a date", ImportMessageKeys.ExpectsDate)]
    [InlineData(CellDataType.Unit, "kg?", ImportMessageKeys.ExpectsIdentifier)]
    public void Значення_не_свого_типу_відхиляється_в_перегляді_а_не_на_застосуванні(
        CellDataType type, string text, string expectedKey)
    {
        // ⛔ Мутація: прибрати перевірку `TypeMismatch` у `Build` — `abc` знову
        // стане «зміною», і Apply відповість 422 на всю книгу.
        var (table, column) = Table(type);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = text;

        var diff = Build(worksheet, table, column, decisions: new Dictionary<CellAddress, EditDecision>());

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(CellValueReader.TypeMismatch, rejection.ReasonCode);
        Assert.Equal(expectedKey, rejection.MessageKey);
        Assert.Equal(column.Code, rejection.ColumnCode);

        // І перевіряє саме той читач, що й запис: він справді відмовив би.
        Assert.Throws<Ecr.Application.Errors.BusinessRuleException>(() => CellValueReader.Read(text, column));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F-06")]
    public void Число_в_числовій_колонці_лишається_зміною()
    {
        var (table, column) = Table(CellDataType.Decimal);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 12.5;

        var diff = Build(worksheet, table, column, decisions: new Dictionary<CellAddress, EditDecision>());

        Assert.Empty(diff.Rejected);
        Assert.Equal(12.5m, Assert.Single(diff.Changes).NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F-30")]
    public void Відмова_погодженим_документом_має_ключ_і_англійську_діагностику()
    {
        var (table, column) = Table(CellDataType.Decimal);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 5;

        var diff = Build(
            worksheet, table, column,
            decisions: new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, RowId, column.Id)] =
                    EditDecision.Deny(EditDenyReason.DocumentApproved, "Документ погоджено."),
            });

        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal("deny.DocumentApproved", rejection.MessageKey);

        // ⛔ Мутація: повернути `decision.Detail ?? "Змінювати комірку не
        // дозволено: …"` — тут знову кирилиця.
        Assert.DoesNotContain(rejection.Message, c => c is >= 'Ѐ' and <= 'ӿ');
        Assert.Contains("DocumentApproved", rejection.Message, StringComparison.Ordinal);
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

    private static TableDiff Build(
        IXLWorksheet worksheet, TableDef table, ColumnDef column,
        IReadOnlyDictionary<CellAddress, EditDecision> decisions)
        => new ImportDiffBuilder().Build(
            worksheet,
            new ExcelTableBlock(
                TableInstance, table.Id, table.Code, "S0", HeaderRow: 1,
                Columns: [new ExcelColumnRef(column.Id, column.Code, 1, false, null)],
                Rows: [new ExcelRowRef(RowKey, 2)]),
            PeriodKeyValue,
            table,
            decisions,
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            new Dictionary<string, long>(StringComparer.Ordinal) { [RowKey] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { [RowKey] = "0x0A" },
            []);
}
