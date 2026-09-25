// tests/Ecr.Adapters.Tests/Excel/ExcelExporterCalculatedColumnTests.cs
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
/// Колонка результату методології (<c>Calculated</c>) доходить у книгу числом
/// (F-02, четвертий раунд UX).
/// </summary>
/// <remarks>
/// ⛔ Відтворено на стенді: панель «Calculation results» показувала R1 = 20, а
/// колонка <c>EMISSION</c> у xlsx лишалася порожньою — результат живе в
/// <c>calc.CalculationResult</c> (<c>D-69</c>), а експорт читав лише
/// <c>doc.CellValue</c>.
/// </remarks>
public sealed class ExcelExporterCalculatedColumnTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int EmissionColumnId = 12;
    private const int MethodologyId = 2;
    private const int MethodologyVersionId = 5;

    /// <remarks>
    /// Мутація: прибрати накладання результатів у <c>ExcelExporter.ExportAsync</c>
    /// (рядок із <c>CalculatedCellOverlay</c>) — комірка EMISSION порожня.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Результат_методології_лягає_в_колонку_Calculated_книги()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var input = new ColumnDef(TableId, EcrCode.Create("A"), Text("A"), 1, CellDataType.Decimal);
        SetId(input, InputColumnId);
        var emission = new ColumnDef(TableId, EcrCode.Create("EMISSION"), Text("Emission"), 2, CellDataType.Calculated);
        SetId(emission, EmissionColumnId);
        table.AddColumn(input);
        table.AddColumn(emission);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [EmissionColumnId] = emission },
            new Dictionary<(int, string), RowDef>());

        var period = new PeriodKey(PeriodKeyValue);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 };

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] =
                [
                    new CellRecord(new CellAddress(period, 1001, InputColumnId), TableId, new CellValueData { ValueNumeric = 8m }),
                    new CellRecord(new CellAddress(period, 1002, InputColumnId), TableId, new CellValueData { ValueNumeric = 5m }),
                ],
            });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableId, EmissionColumnId, MethodologyId, "EMISSION", "{}", [MethodologyVersionId])]);

        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CalculationResultRow(MethodologyVersionId, "R1", "EMISSION", 20m, 8, null),
                new CalculationResultRow(MethodologyVersionId, "R2", "EMISSION", 12.5m, 8, null),
            ]);

        var exporter = new ExcelExporter(
            store, rows, metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), methodologies, results);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        using var workbook = new XLWorkbook(output);
        var worksheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2, дані — з рядка 3; EMISSION — друга колонка.
        Assert.Equal(20m, worksheet.Cell(3, 2).GetValue<decimal>());
        Assert.Equal(12.5m, worksheet.Cell(4, 2).GetValue<decimal>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
