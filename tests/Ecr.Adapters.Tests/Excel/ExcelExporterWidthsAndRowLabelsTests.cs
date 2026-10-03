// tests/Ecr.Adapters.Tests/Excel/ExcelExporterWidthsAndRowLabelsTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// T4-04 (проход тестувальника №4): ширина стовпців експорту залежить від
/// вмісту, а підписи рядків потрапляють у книгу — окремим стовпцем праворуч
/// від колонок даних, не зсуваючи їх (карта й імпорт тримаються за номери).
/// </summary>
public sealed class ExcelExporterWidthsAndRowLabelsTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int NameColumnId = 11;
    private const int QtyColumnId = 12;
    private const string LongName = "Long descriptive name of the first measured item";

    private static readonly string[] Keys = ["R1", "R2"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Ширини_стовпців_не_менші_за_вміст_а_підписи_рядків_виведені()
    {
        using var workbook = await ExportAsync();
        var worksheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2, дані — з рядка 3.
        Assert.True(worksheet.Column(1).Width >= LongName.Length, $"A = {worksheet.Column(1).Width}");
        Assert.True(worksheet.Column(2).Width >= "12345.678".Length + 4, $"B = {worksheet.Column(2).Width}");

        // Колонки даних лишились на місцях, підписи — у стовпці 3.
        Assert.Equal(LongName, worksheet.Cell(3, 1).GetString());
        Assert.Equal("Row", worksheet.Cell(2, 3).GetString());
        Assert.Equal("First row", worksheet.Cell(3, 3).GetString());
        Assert.Equal("Second row", worksheet.Cell(4, 3).GetString());
        Assert.True(worksheet.Column(3).Width >= "Second row".Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Ширина_за_вмістом_обмежена_зверху()
    {
        using var workbook = await ExportAsync(valueText: new string('x', 400));
        var worksheet = workbook.Worksheet("Sheet");

        Assert.True(worksheet.Column(1).Width <= 51,$"A = {worksheet.Column(1).Width}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Стовпець_підписів_не_вважається_сторонім_значенням_при_імпорті()
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Sheet");
        worksheet.Cell(1, 1).Value = "Table 1";
        worksheet.Cell(2, 1).Value = "Name";
        worksheet.Cell(2, 2).Value = "Qty";
        worksheet.Cell(2, 3).Value = "Row";
        worksheet.Cell(3, 3).Value = "First row";
        worksheet.Cell(4, 3).Value = "Second row";

        var table = new TableDef(
            1, EcrCode.Create("T1"), Text("Table 1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        var block = new ExcelTableBlock(
            InstanceId, TableId, "T1", "Sheet", 2,
            [new ExcelColumnRef(NameColumnId, "NAME", 1, false, null), new ExcelColumnRef(QtyColumnId, "QTY", 2, false, null)],
            [new ExcelRowRef("R1", 3), new ExcelRowRef("R2", 4)]);

        Assert.Empty(StrayValueDetector.Find(worksheet, [(block, table)]));

        // Значення в ДАНИХ стовпці поза рядками карти — як і раніше відмова.
        worksheet.Cell(7, 1).Value = "orphan";
        Assert.Single(StrayValueDetector.Find(worksheet, [(block, table)]));
    }

    private static async Task<XLWorkbook> ExportAsync(string? valueText = null)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var name = new ColumnDef(TableId, EcrCode.Create("NAME"), Text("NAME"), 1, CellDataType.String);
        SetId(name, NameColumnId);
        var qty = new ColumnDef(TableId, EcrCode.Create("QTY"), Text("QTY"), 2, CellDataType.Decimal);
        SetId(qty, QtyColumnId);
        table.AddColumn(name);
        table.AddColumn(qty);
        sheet.AddTable(table);

        var rowDefs = new Dictionary<(int, string), RowDef>
        {
            [(TableId, "R1")] = new RowDef(TableId, RowKey.Create("R1"), 1, Text("First row"), RowKind.Item),
            [(TableId, "R2")] = new RowDef(TableId, RowKey.Create("R2"), 2, Text("Second row"), RowKind.Item),
        };

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [NameColumnId] = name, [QtyColumnId] = qty },
            rowDefs);

        var period = new PeriodKey(PeriodKeyValue);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 };

        var cells = new List<CellRecord>
        {
            new(new CellAddress(period, 1001, NameColumnId), TableId, new CellValueData { ValueString = valueText ?? LongName }),
            new(new CellAddress(period, 1001, QtyColumnId), TableId, new CellValueData { ValueNumeric = 12345.678m }),
            new(new CellAddress(period, 1002, QtyColumnId), TableId, new CellValueData { ValueNumeric = -98765.4m }),
        };

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [InstanceId] = cells });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var exporter = new ExcelExporter(
            store, rows, metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator());

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        return new XLWorkbook(output);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
