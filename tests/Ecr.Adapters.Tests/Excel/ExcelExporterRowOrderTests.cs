// tests/Ecr.Adapters.Tests/Excel/ExcelExporterRowOrderTests.cs
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
/// `V-10`: рядки без опису в шаблоні йдуть у книзі в тому самому порядку, що
/// й у сітці, — <c>R1, R2, …, R10</c>, а не <c>R1, R10, …, R2</c>.
/// </summary>
/// <remarks>
/// ⚠ Книга розбирається ClosedXML — перевіряється файл, який отримає людина,
/// а не проміжна структура експортера.
/// </remarks>
public sealed class ExcelExporterRowOrderTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int ColumnId = 11;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "V-10")]
    public async Task Рядки_без_опису_йдуть_у_порядку_сітки_а_не_за_ключем_ординально()
    {
        var keys = Enumerable.Range(1, 18).Select(i => $"R{i}").ToList();

        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var column = new ColumnDef(TableId, EcrCode.Create("C1"), Text("C1"), 1, CellDataType.String);
        SetId(column, ColumnId);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>());

        var period = new PeriodKey(PeriodKeyValue);

        // ⚠ Словник наповнюється в ПЕРЕМІШАНОМУ порядку: порядок книги не має
        // залежати від того, як рядки прийшли з бази.
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in keys.OrderByDescending(k => k, StringComparer.Ordinal))
        {
            rowIds[key] = 1000 + int.Parse(key[1..], System.Globalization.CultureInfo.InvariantCulture);
        }

        var cells = keys
            .Select(k => new CellRecord(
                new CellAddress(period, rowIds[k], ColumnId), TableId, new CellValueData { ValueString = k }))
            .ToList();

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
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

        using var workbook = new XLWorkbook(output);
        var worksheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2, дані — з рядка 3.
        var order = Enumerable.Range(3, keys.Count).Select(r => worksheet.Cell(r, 1).GetString()).ToList();

        Assert.Equal(keys, order);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
