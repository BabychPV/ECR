// tests/Ecr.Adapters.Tests/Excel/ExcelExportHarness.cs
using System.Text;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>Колонка тестової таблиці: код, підписи (мова → текст), тип, код групи (<c>null</c> — без групи).</summary>
internal sealed record HarnessColumn(
    string Code, IReadOnlyDictionary<string, string> Header, CellDataType Type = CellDataType.Decimal, string? Group = null);

/// <summary>Група заголовків: код, підписи, код батька.</summary>
internal sealed record HarnessGroup(string Code, IReadOnlyDictionary<string, string> Header, string? Parent = null);

/// <summary>
/// Збирає знімок шаблону з однією таблицею, вантажить значення й експортує через справжній
/// <see cref="ExcelExporter"/> (порти — заглушки). Спільний для регресії «без груп» і тестів груп.
/// </summary>
internal static class ExcelExportHarness
{
    public const long DocumentId = 1;
    public const int TemplateVersionId = 1;
    public const int PeriodKeyValue = 202609;
    public const long InstanceId = 729;
    public const int TableId = 1;

    public static readonly string[] RowKeys = ["R1", "R2", "R3"];

    public static IReadOnlyDictionary<string, string> L(string en, string? ru = null)
        => ru is null
            ? new Dictionary<string, string> { ["en"] = en }
            : new Dictionary<string, string> { ["en"] = en, ["ru"] = ru };

    public static TemplateVersionSnapshot Snapshot(
        IReadOnlyList<HarnessColumn> columns, IReadOnlyList<HarnessGroup> groups, out TableDef table)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text(L("Sheet")), 1);
        SetId(sheet, 1);
        table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text(L("Table 1")), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);

        var groupIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextGroup = 100;
        foreach (var g in groups)
        {
            groupIds[g.Code] = nextGroup++;
        }

        foreach (var g in groups)
        {
            var def = new ColumnHeaderGroupDef(
                TableId, EcrCode.Create(g.Code), Text(g.Header), 1, g.Parent is null ? null : groupIds[g.Parent]);
            SetId(def, groupIds[g.Code]);
            table.AddHeaderGroup(def);
        }

        var byId = new Dictionary<int, ColumnDef>();
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var column = new ColumnDef(TableId, EcrCode.Create(c.Code), Text(c.Header), i + 1, c.Type);
            SetId(column, 10 + i);
            if (c.Group is not null)
            {
                column.SetHeaderGroup(groupIds[c.Group]);
            }

            table.AddColumn(column);
            byId[column.Id] = column;
        }

        sheet.AddTable(table);

        var rowDefs = RowKeys.Select((k, i) => (k, i)).ToDictionary(
            x => (TableId, x.k),
            x => new RowDef(TableId, RowKey.Create(x.k), x.i + 1, Text(L("Row " + x.k)), RowKind.Item));

        return new TemplateVersionSnapshot(TemplateVersionId, 1, [sheet], byId, rowDefs);
    }

    /// <summary>Значення: колонка <c>c</c> рядка <c>r</c> = 100·(r+1) + c + 0.5 (числові колонки).</summary>
    public static decimal Value(int row, int column) => (100 * (row + 1)) + column + 0.5m;

    public static async Task<XLWorkbook> ExportAsync(
        IReadOnlyList<HarnessColumn> columns, IReadOnlyList<HarnessGroup> groups, string language = "en")
    {
        var snapshot = Snapshot(columns, groups, out _);
        var period = new PeriodKey(PeriodKeyValue);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var r = 0; r < RowKeys.Length; r++)
        {
            rowIds[RowKeys[r]] = 1001 + r;
        }

        var cells = new List<CellRecord>();
        for (var r = 0; r < RowKeys.Length; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                if (columns[c].Type == CellDataType.Decimal)
                {
                    cells.Add(new CellRecord(
                        new CellAddress(period, 1001 + r, 10 + c), TableId,
                        new CellValueData { ValueNumeric = Value(r, c) }));
                }
                else if (columns[c].Type == CellDataType.String)
                {
                    cells.Add(new CellRecord(
                        new CellAddress(period, 1001 + r, 10 + c), TableId,
                        new CellValueData { ValueString = $"s{r}{c}" }));
                }
            }
        }

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
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: language, PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        // Копія в пам'ять: потік експорту DeleteOnClose.
        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    /// <summary>Блоки карти книги (прихований аркуш <c>_ecr</c>).</summary>
    public static string MapJson(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet(ExcelWorkbookMap.SheetName);
        var builder = new StringBuilder();
        for (var row = 1; sheet.Cell(row, 1).GetString() is { Length: > 0 } chunk; row++)
        {
            builder.Append(chunk);
        }

        return builder.ToString();
    }

    private static readonly System.Text.Json.JsonSerializerOptions MapOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public static ExcelWorkbookMap Map(XLWorkbook workbook)
        => System.Text.Json.JsonSerializer.Deserialize<ExcelWorkbookMap>(MapJson(workbook), MapOptions)!;

    /// <summary>
    /// Канонічний текст книги: значення, тип, формат, стиль шапки, об'єднання, закріплення, карта.
    /// Без ширин стовпців (залежать від шрифтів ОС) і без метаданих файла.
    /// </summary>
    public static string Dump(XLWorkbook workbook)
    {
        var text = new StringBuilder();
        foreach (var ws in workbook.Worksheets)
        {
            text.Append("# ").Append(ws.Name).Append(" visible=").Append(ws.Visibility).Append('\n');
            text.Append("merged=").Append(string.Join(',', ws.MergedRanges.Select(m => m.RangeAddress.ToStringRelative()).Order(StringComparer.Ordinal))).Append('\n');
            text.Append("frozen=").Append(ws.SheetView.SplitRow).Append('\n');

            if (ws.Name == ExcelWorkbookMap.SheetName)
            {
                text.Append(MapJson(workbook)).Append('\n');
                continue;
            }

            foreach (var cell in ws.CellsUsed(XLCellsUsedOptions.All))
            {
                text.Append(cell.Address).Append('|')
                    .Append(cell.DataType).Append('|')
                    .Append(cell.GetString()).Append('|')
                    .Append(cell.Style.Font.Bold ? "B" : "-").Append('|')
                    .Append(cell.Style.NumberFormat.Format).Append('|')
                    .Append(cell.Style.Fill.BackgroundColor.ToString()).Append('\n');
            }
        }

        return text.ToString();
    }

    private static LocalizedText Text(IReadOnlyDictionary<string, string> values) => new(new Dictionary<string, string>(values));

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
