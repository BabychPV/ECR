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
/// P2: текстовий результат Formula/Calculated-колонки (міст, «No - Нет») потрапляє в книгу рядковою
/// клітинкою; числовий — числом; порожній — порожньою.
/// </summary>
public sealed class ExcelExporterFormulaTextTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int FormulaColumnId = 12;

    /// <remarks>
    /// Мутація: у <c>ExcelExporter.WriteValue</c> прибрати гілку <c>else if … ValueString</c> для
    /// Formula/Calculated — клітинка «No - Нет» порожня.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Текст_формули_лягає_рядком_число_числом_порожнє_порожнім()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var formula = new ColumnDef(TableId, EcrCode.Create("MEASURE"), Text("Measure"), 1, CellDataType.Formula);
        SetId(formula, FormulaColumnId);
        table.AddColumn(formula);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [FormulaColumnId] = formula },
            new Dictionary<(int, string), RowDef>());

        var period = new PeriodKey(PeriodKeyValue);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["R1"] = 1001,
            ["R2"] = 1002,
            ["R3"] = 1003,
        };

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] =
                [
                    new CellRecord(new CellAddress(period, 1001, FormulaColumnId), TableId, new CellValueData { ValueString = "No - Нет" }),
                    new CellRecord(new CellAddress(period, 1002, FormulaColumnId), TableId, new CellValueData { ValueNumeric = 12.5m }),
                    new CellRecord(new CellAddress(period, 1003, FormulaColumnId), TableId, new CellValueData()),
                ],
            });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ColumnResultBinding>());
        var results = Substitute.For<ICalculationResultStore>();

        var exporter = new ExcelExporter(
            store, rows, metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), methodologies, results);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        using var workbook = new XLWorkbook(output);
        var worksheet = workbook.Worksheet("Sheet");

        var text = worksheet.Cell(3, 1);
        Assert.Equal(XLDataType.Text, text.DataType);
        Assert.Equal("No - Нет", text.GetString());

        var number = worksheet.Cell(4, 1);
        Assert.Equal(XLDataType.Number, number.DataType);
        Assert.Equal(12.5m, number.GetValue<decimal>());

        Assert.True(worksheet.Cell(5, 1).IsEmpty());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
