using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// ФВ-2.6/2.7: Excel-експорт фарбує комірки тими самими правилами, що сітка.
/// Перевіряється файл, який отримає людина: книга читається назад.
/// </summary>
public sealed class ExcelExporterConditionalFormatTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;

    [Fact]
    public async Task Cells_are_colored_by_rules_and_empty_rule_hits_missing_cell()
    {
        using var workbook = await ExportAsync(includeStyles: true);
        var sheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2, дані: R1=150, R2=5, R3 без комірки.
        var hit = sheet.Cell(3, 1).Style;
        Assert.Equal("FF0000", Hex(hit.Fill.BackgroundColor));
        Assert.Equal("FFFFFF", Hex(hit.Font.FontColor));
        Assert.True(hit.Font.Bold);

        var miss = sheet.Cell(4, 1).Style;
        Assert.NotEqual("FF0000", Hex(miss.Fill.BackgroundColor));
        Assert.False(miss.Font.Bold);

        var empty = sheet.Cell(5, 1).Style;
        Assert.Equal("CCCCCC", Hex(empty.Fill.BackgroundColor));
    }

    [Fact]
    public async Task Without_styles_the_rules_are_not_applied()
    {
        using var workbook = await ExportAsync(includeStyles: false);
        var sheet = workbook.Worksheet("Sheet");

        Assert.False(sheet.Cell(3, 1).Style.Font.Bold);
        Assert.NotEqual("FF0000", Hex(sheet.Cell(3, 1).Style.Fill.BackgroundColor));
    }

    private static async Task<XLWorkbook> ExportAsync(bool includeStyles)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var column = new ColumnDef(TableId, EcrCode.Create("C1"), Text("C1"), 1, CellDataType.Decimal);
        SetId(column, ColumnId);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>());

        var period = new PeriodKey(PeriodKeyValue);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002, ["R3"] = 1003 };
        var cells = new List<CellRecord>
        {
            new(new CellAddress(period, 1001, ColumnId), TableId, new CellValueData { ValueNumeric = 150m }),
            new(new CellAddress(period, 1002, ColumnId), TableId, new CellValueData { ValueNumeric = 5m }),
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

        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());

        var formats = Substitute.For<IConditionalFormatStore>();
        formats.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ConditionalFormatRule>>(
            [
                new ConditionalFormatRule(TemplateVersionId, "C1", 1, "gt", "100", null, "#ff0000", "#ffffff", true),
                new ConditionalFormatRule(TemplateVersionId, "C1", 2, "empty", null, null, "#cccccc", null, false),
            ]));

        var exporter = new ExcelExporter(
            store, rows, metadata, styles, Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), conditionalFormats: formats);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: includeStyles, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;

        return new XLWorkbook(copy);
    }

    private static string Hex(XLColor color) => $"{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}";

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
