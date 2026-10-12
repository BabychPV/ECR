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
/// CELL-STYLE L3b: користувацька заливка/колір тексту/жирність потрапляють у книгу — між стилем колонки
/// й умовним форматуванням; без стилів — ні. Перевіряється файл, який отримає людина.
/// </summary>
public sealed class ExcelExporterUserCellStyleTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;
    private static readonly DateTime At = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task User_fill_and_text_color_reach_the_book_and_conditional_format_wins_over_them()
    {
        using var workbook = await ExportAsync(includeStyles: true);
        var sheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2, дані: R1=150 (правило gt 100 → червоний), R2=5, R3 без значення.
        // R1: користувач поставив жовте — умовний формат лишається вищим шаром.
        Assert.Equal("FF0000", Hex(sheet.Cell(3, 1).Style.Fill.BackgroundColor));

        // R2: правило не спрацювало — видно користувацьку заливку, колір тексту й жирність.
        var user = sheet.Cell(4, 1).Style;
        Assert.Equal("99CCFF", Hex(user.Fill.BackgroundColor));
        Assert.Equal("0000FF", Hex(user.Font.FontColor));
        Assert.True(user.Font.Bold);

        // R3: порожня комірка без оформлення не пофарбована.
        Assert.NotEqual("99CCFF", Hex(sheet.Cell(5, 1).Style.Fill.BackgroundColor));
    }

    [Fact]
    public async Task Without_styles_the_user_formatting_is_not_applied()
    {
        using var workbook = await ExportAsync(includeStyles: false);
        var user = workbook.Worksheet("Sheet").Cell(4, 1).Style;

        Assert.NotEqual("99CCFF", Hex(user.Fill.BackgroundColor));
        Assert.False(user.Font.Bold);
    }

    [Fact]
    public void Mapper_ignores_non_hex_color_instead_of_failing_the_export()
    {
        using var book = new XLWorkbook();
        var cell = book.AddWorksheet("S").Cell(1, 1);

        new StyleMapper().ApplyUserStyle(
            cell.Style, new Ecr.Application.Templates.CellFormatDto("red", "#12", false));

        Assert.NotEqual("FF0000", Hex(cell.Style.Fill.BackgroundColor));
        Assert.False(cell.Style.Font.Bold);
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
            ]));

        var userStyles = Substitute.For<ICellStyleStore>();
        userStyles.ReadInstancesAsync(Arg.Any<IReadOnlyCollection<long>>(), period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellStyle>>
            {
                [InstanceId] =
                [
                    Styled(1001, "#FFFF00", null, false),
                    Styled(1002, "#99CCFF", "#0000FF", true),
                ],
            });

        var exporter = new ExcelExporter(
            store, rows, metadata, styles, Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), conditionalFormats: formats, cellStyles: userStyles);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: includeStyles, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;

        return new XLWorkbook(copy);
    }

    private static CellStyle Styled(long rowId, string background, string? foreground, bool bold)
    {
        var style = new CellStyle(new CellAddress(new PeriodKey(PeriodKeyValue), rowId, ColumnId), 1, At);
        style.Apply(background, foreground, bold, 1, At);
        return style;
    }

    private static string Hex(XLColor color) => $"{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}";

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
