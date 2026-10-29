// tests/Ecr.Adapters.Tests/Excel/ExcelHeaderGroupsRoundTripTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;
using static Ecr.Adapters.Tests.Excel.ExcelExportHarness;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Групи заголовків колонок в Excel: N рядків шапки з об'єднаннями, рядок підписів - останній (HeaderRow),
/// імпорт приймає багаторядкову шапку, відбиток груп стабільний, значень поза рядками немає.
/// </summary>
public sealed class ExcelHeaderGroupsRoundTripTests
{
    private static readonly HarnessGroup[] TwoLevels =
    [
        new("EM", L("Emissions · Q1 2026", "Выбросы · Q1 2026")),
        new("JAN", L("January", "Январь"), "EM"),
    ];

    /// <summary>Emissions · Q1 2026 &gt; January &gt; (t, mg), плюс колонка без групи.</summary>
    private static readonly HarnessColumn[] Columns =
    [
        new("T", L("t", "т"), CellDataType.Decimal, "JAN"),
        new("MG", L("mg", "мг"), CellDataType.Decimal, "JAN"),
        new("NOTE", L("Note", "Примечание"), CellDataType.String),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Дві_групи_дають_три_рядки_шапки_з_об_єднаннями_а_підписи_колонок_останні()
    {
        using var workbook = await ExportAsync(Columns, TwoLevels);
        var sheet = workbook.Worksheet("Sheet");
        var block = Map(workbook).Tables.Single();

        // Назва - рядок 1, групи - 2 і 3, підписи колонок - 4, дані - з 5.
        Assert.Equal(4, block.HeaderRow);
        Assert.Equal(2, block.GroupRows);
        Assert.Equal(1, block.TitleRow);
        Assert.Equal(5, block.Rows[0].Number);

        Assert.Equal("Emissions · Q1 2026", sheet.Cell(2, 1).GetString());
        Assert.Equal("January", sheet.Cell(3, 1).GetString());
        Assert.Equal("t", sheet.Cell(4, 1).GetString());
        Assert.Equal("mg", sheet.Cell(4, 2).GetString());
        Assert.Equal("Note", sheet.Cell(4, 3).GetString());

        var merged = sheet.MergedRanges.Select(m => m.RangeAddress.ToStringRelative()).Order().ToArray();
        Assert.Equal(["A2:B2", "A3:B3"], merged);

        // Колонка без групи: рівні груп порожні, підпис - у рядку підписів.
        Assert.True(sheet.Cell(2, 3).IsEmpty());
        Assert.True(sheet.Cell(3, 3).IsEmpty());

        Assert.Equal(4, sheet.SheetView.SplitRow);
        Assert.Equal(100.5m, sheet.Cell(5, 1).GetValue<decimal>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Три_рівні_груп_і_різна_глибина_колонок()
    {
        HarnessGroup[] groups =
        [
            new("A", L("A")),
            new("B", L("B"), "A"),
            new("C", L("C"), "B"),
            new("D", L("D"), "A"),
        ];
        HarnessColumn[] columns =
        [
            new("T", L("t"), CellDataType.Decimal, "C"),
            new("MG", L("mg"), CellDataType.Decimal, "C"),
            new("X", L("x"), CellDataType.Decimal, "D"),
            new("Y", L("y"), CellDataType.Decimal),
        ];

        using var workbook = await ExportAsync(columns, groups);
        var sheet = workbook.Worksheet("Sheet");

        Assert.Equal(5, Map(workbook).Tables.Single().HeaderRow);
        Assert.Equal(
            ["A2:C2", "A3:B3", "A4:B4"],
            sheet.MergedRanges.Select(m => m.RangeAddress.ToStringRelative()).Order().ToArray());

        // D - група другого рівня лише над X (одна колонка): без об'єднання, рядок 3, стовпець C.
        Assert.Equal("D", sheet.Cell(3, 3).GetString());
        Assert.True(sheet.Cell(4, 3).IsEmpty());
        Assert.Equal("x", sheet.Cell(5, 3).GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Підписи_груп_беруть_мову_експорту_з_тим_самим_запасним_варіантом_що_й_колонки()
    {
        using var ru = await ExportAsync(Columns, TwoLevels, "ru");
        var sheet = ru.Worksheet("Sheet");
        Assert.Equal("Выбросы · Q1 2026", sheet.Cell(2, 1).GetString());
        Assert.Equal("Январь", sheet.Cell(3, 1).GetString());
        Assert.Equal("т", sheet.Cell(4, 1).GetString());

        // kz: підпису немає ні в групи, ні в колонки - обидва падають на en.
        using var kz = await ExportAsync(Columns, TwoLevels, "kz");
        Assert.Equal("January", kz.Worksheet("Sheet").Cell(3, 1).GetString());
        Assert.Equal("t", kz.Worksheet("Sheet").Cell(4, 1).GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Наскрізно_експорт_імпорт_без_змін_без_сторонніх_значень_і_з_стабільним_відбитком()
    {
        using var workbook = await ExportAsync(Columns, TwoLevels);
        var map = Map(workbook);
        var block = map.Tables.Single();
        var sheet = workbook.Worksheet(block.SheetName);

        _ = Snapshot(Columns, TwoLevels, out var table);

        Assert.Empty(StrayValueDetector.Find(sheet, [(block, table)]));
        Assert.Null(LayoutFingerprint.FirstMismatch(sheet, block));

        var diff = Diff(sheet, block, table);

        Assert.Empty(diff.Changes);
        Assert.Empty(diff.Rejected);

        // Відбиток детермінований: повторний експорт - та сама карта.
        using var again = await ExportAsync(Columns, TwoLevels);
        Assert.Equal(MapJson(workbook), MapJson(again));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Правка_значення_в_багаторядковій_шапці_дає_зміну_а_стороннє_значення_і_зміна_групи_відмову()
    {
        using var workbook = await ExportAsync(Columns, TwoLevels);
        var block = Map(workbook).Tables.Single();
        var sheet = workbook.Worksheet(block.SheetName);
        _ = Snapshot(Columns, TwoLevels, out var table);

        // Дані починаються після останнього рядка шапки.
        sheet.Cell(block.Rows[0].Number, 1).Value = 999;
        var change = Assert.Single(Diff(sheet, block, table).Changes);
        Assert.Equal("T", change.ColumnCode);
        Assert.Equal(999m, Convert.ToDecimal(change.NewValue, System.Globalization.CultureInfo.InvariantCulture));

        // Значення у вільній комірці рядка груп колонки без групи: комірки груп - "відоме" для детектора сторонніх,
        // але відбиток груп ловить це як зміну розкладки.
        sheet.Cell(3, 3).Value = "stray";
        Assert.Empty(StrayValueDetector.Find(sheet, [(block, table)]));
        Assert.Equal("NOTE", LayoutFingerprint.FirstMismatch(sheet, block)!.Value.ColumnCode);
        sheet.Cell(3, 3).Clear();

        // Значення під таблицею - стороннє, як і без груп.
        sheet.Cell(20, 1).Value = "orphan";
        var stray = Assert.Single(StrayValueDetector.Find(sheet, [(block, table)]));
        Assert.Equal(ImportMessageKeys.OutsideRows, stray.MessageKey);
        sheet.Cell(20, 1).Clear();

        // Перейменована в Excel група - розкладка змінена: відмова таблиці, не правдоподібний diff.
        sheet.Cell(2, 1).Value = "Renamed";
        var moved = LayoutFingerprint.FirstMismatch(sheet, block);
        Assert.NotNull(moved);
        Assert.Equal("T", moved!.Value.ColumnCode);
        Assert.Equal("A2", moved.Value.Address);
        var rejection = Assert.Single(Diff(sheet, block, table).Rejected);
        Assert.Equal(ImportMessageKeys.LayoutChanged, rejection.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Карта_без_груп_не_несе_нових_полів()
    {
        using var workbook = await ExportAsync(ExcelExporterNoGroupsRegressionTests.Columns, []);

        var json = MapJson(workbook);

        Assert.DoesNotContain("groupRows", json, StringComparison.Ordinal);
        Assert.DoesNotContain("groups", json, StringComparison.Ordinal);
    }

    private static TableDiff Diff(IXLWorksheet sheet, ExcelTableBlock block, TableDef table)
    {
        var rowIds = block.Rows.Select((r, i) => (r.RowKey, Id: 1001L + i))
            .ToDictionary(x => x.RowKey, x => x.Id, StringComparer.Ordinal);
        var period = new PeriodKey(PeriodKeyValue);
        var current = new List<CellRecord>();

        for (var r = 0; r < block.Rows.Count; r++)
        {
            for (var c = 0; c < block.Columns.Count; c++)
            {
                var type = table.Columns.Single(x => x.Id == block.Columns[c].ColumnDefId).DataType;
                current.Add(new CellRecord(
                    new CellAddress(period, 1001 + r, block.Columns[c].ColumnDefId), table.Id,
                    type == CellDataType.String
                        ? new CellValueData { ValueString = $"s{r}{c}" }
                        : new CellValueData { ValueNumeric = Value(r, c) }));
            }
        }

        return new ImportDiffBuilder().Build(
            sheet,
            block,
            PeriodKeyValue,
            table,
            new Dictionary<CellAddress, EditDecision>(),
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            rowIds,
            block.Rows.ToDictionary(r => r.RowKey, _ => "0x0A", StringComparer.Ordinal),
            current);
    }
}
