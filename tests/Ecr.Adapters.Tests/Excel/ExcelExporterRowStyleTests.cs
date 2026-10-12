using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// D-393: стиль РЯДКА шаблону (<c>RowDef.StyleId</c>) потрапляє в книгу — поверх стилю колонки, під користувацьким
/// кольором комірки; без стилів — ні; рядок без стилю не чіпається.
/// </summary>
public sealed class ExcelExporterRowStyleTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;
    private const int RowStyleId = 5;
    private static readonly DateTime At = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Row_style_reaches_the_book_under_user_color_and_other_rows_stay_plain()
    {
        using var workbook = await ExportAsync(includeStyles: true);
        var sheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2; R1 (рядок 3) має стиль рядка: заливка 99CC00, жирний.
        var row = sheet.Cell(3, 1).Style;
        Assert.Equal("99CC00", Hex(row.Fill.BackgroundColor));
        Assert.True(row.Font.Bold);

        // R2 (рядок 4): стиль рядка й користувацький колір — видно користувацький (шар вище).
        Assert.Equal("FFFF00", Hex(sheet.Cell(4, 1).Style.Fill.BackgroundColor));

        // R3 (рядок 5): без стилю рядка — не пофарбований.
        Assert.NotEqual("99CC00", Hex(sheet.Cell(5, 1).Style.Fill.BackgroundColor));
    }

    [Fact]
    public async Task Without_styles_the_row_style_is_not_applied()
    {
        using var workbook = await ExportAsync(includeStyles: false);
        var row = workbook.Worksheet("Sheet").Cell(3, 1).Style;

        Assert.NotEqual("99CC00", Hex(row.Fill.BackgroundColor));
        Assert.False(row.Font.Bold);
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

        var styledRow = new RowDef(TableId, RowKey.Create("R1"), 1, Text("R1"), RowKind.Item);
        styledRow.SetStyle(RowStyleId);
        var secondRow = new RowDef(TableId, RowKey.Create("R2"), 2, Text("R2"), RowKind.Item);
        secondRow.SetStyle(RowStyleId);
        var plainRow = new RowDef(TableId, RowKey.Create("R3"), 3, Text("R3"), RowKind.Item);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>
            {
                [(TableId, "R1")] = styledRow,
                [(TableId, "R2")] = secondRow,
                [(TableId, "R3")] = plainRow,
            });

        var rowStyle = new StyleDef(TemplateVersionId, EcrCode.Create("ROWSTYLE"));
        rowStyle.SetAppearance(null, null, true, false, null, unchecked((int)0xFF99CC00), null, null, null, false, null);
        SetId(rowStyle, RowStyleId);

        var period = new PeriodKey(PeriodKeyValue);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002, ["R3"] = 1003 };
        var cells = new List<CellRecord>
        {
            new(new CellAddress(period, 1001, ColumnId), TableId, new CellValueData { ValueNumeric = 1m }),
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
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<int, StyleDef> { [RowStyleId] = rowStyle });

        var userStyles = Substitute.For<ICellStyleStore>();
        var user = new CellStyle(new CellAddress(period, 1002, ColumnId), 1, At);
        user.Apply("#FFFF00", null, null, 1, At);
        userStyles.ReadInstancesAsync(Arg.Any<IReadOnlyCollection<long>>(), period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellStyle>> { [InstanceId] = [user] });

        var exporter = new ExcelExporter(
            store, rows, metadata, styles, Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), cellStyles: userStyles);

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
