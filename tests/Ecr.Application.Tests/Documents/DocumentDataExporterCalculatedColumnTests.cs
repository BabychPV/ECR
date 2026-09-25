// tests/Ecr.Application.Tests/Documents/DocumentDataExporterCalculatedColumnTests.cs
using System.Text;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Колонка результату методології доходить у json/csv-вивантаження числом (F-02).
/// </summary>
/// <remarks>
/// ⛔ Відтворено на стенді: експорт json без <c>EMISSION</c>, хоча панель
/// результатів показувала R1 = 20.
/// </remarks>
public sealed class DocumentDataExporterCalculatedColumnTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int EmissionColumnId = 12;

    /// <remarks>
    /// Мутація: прибрати накладання в <c>DocumentDataExporter.ExportAsync</c> — у
    /// json немає значення <c>EMISSION</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Json_містить_результат_методології_в_колонці_Calculated()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, TableId);
        var emission = new ColumnDef(TableId, EcrCode.Create("EMISSION"), Text("Emission"), 1, CellDataType.Calculated);
        SetId(emission, EmissionColumnId);
        table.AddColumn(emission);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [EmissionColumnId] = emission },
            new Dictionary<(int, string), RowDef>());

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceId] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001 },
            });

        var cells = Substitute.For<ICellStore>();
        cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableId, EmissionColumnId, 2, "EMISSION", "{}", [5])]);

        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns([new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null)]);

        var exporter = new DocumentDataExporter(
            cells, rows, metadata, Substitute.For<IRegistryStore>(), methodologies, results);

        var bytes = await exporter.ExportAsync(DocumentId, PeriodKeyValue, DocumentExportFormat.Json, false, CancellationToken.None);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"EMISSION\": \"20\"", json, StringComparison.Ordinal);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
