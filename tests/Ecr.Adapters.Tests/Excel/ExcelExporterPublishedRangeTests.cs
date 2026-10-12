// tests/Ecr.Adapters.Tests/Excel/ExcelExporterPublishedRangeTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// ФВ-0.2 / D-17: діапазон <c>[A:B]</c> у формулі книги — ті самі рядки, які читає розрахунок
/// (ключі з <c>cfg.FormulaDependency</c>), а не рядки між кінцями за ПОТОЧНИМ макетом.
/// </summary>
/// <remarks>
/// ⛔ Презентаційна правка порядку рядків (ФВ-7.2) в опублікованій версії міняє макет книги, але не склад
/// діапазону формули: система рахує <c>R1 + R2 + R3</c>, а експорт, що зліпив діапазон із координат
/// кінців, віддав би формулу над <c>R1, R4, R3</c> — інше число в Excel при тій самій цифрі в системі.
/// <para>
/// Мутація: у <c>FormulaTranslator.Reference</c> прибрати гілку <c>PublishedRanges</c> — перший тест
/// червоний (діапазон <c>A3:A5</c> замість переліку).
/// </para>
/// </remarks>
public sealed class ExcelExporterPublishedRangeTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int TotalColumnId = 12;
    private const int FormulaId = 77;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-0.2")]
    public async Task Перестановка_рядків_після_публікації_експорт_пише_ті_самі_рядки_що_рахує_система()
    {
        // Публікація розкрила R1:R3 як R1, R2, R3. Після презентаційної правки порядок у книзі:
        // R1 (рядок 3), R4 (4), R3 (5), R2 (6), R5 (7).
        var formula = await ExportedFormulaAsync(
            reordered: true, withPublishedGraph: true);

        Assert.Equal("SUM(('Sheet'!A3,'Sheet'!A6,'Sheet'!A5))", formula);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-0.2")]
    public async Task Без_перестановки_опублікований_діапазон_лишається_діапазоном()
    {
        // Контроль: порядок книги збігається з публікацією — формула та сама, що й до правки.
        var formula = await ExportedFormulaAsync(
            reordered: false, withPublishedGraph: true);

        Assert.Equal("SUM('Sheet'!A3:'Sheet'!A5)", formula);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-0.2")]
    public async Task Без_збереженого_графа_чернетка_діапазон_за_макетом_книги()
    {
        // Чернетка (граф не збережено): рахунок іде за поточним порядком, тож і книга — за макетом.
        var formula = await ExportedFormulaAsync(
            reordered: true, withPublishedGraph: false);

        Assert.Equal("SUM('Sheet'!A3:'Sheet'!A5)", formula);
    }

    private static async Task<string> ExportedFormulaAsync(bool reordered, bool withPublishedGraph)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text("Sheet"), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, TableId);

        var input = new ColumnDef(TableId, EcrCode.Create("A"), Text("A"), 1, CellDataType.Decimal);
        SetId(input, InputColumnId);
        var total = new ColumnDef(TableId, EcrCode.Create("TOTAL"), Text("Total"), 2, CellDataType.Decimal);
        SetId(total, TotalColumnId);
        table.AddColumn(input);
        table.AddColumn(total);

        // Поточні Ordinal: R1(1), R4(2), R3(3), R2(4), R5(5) після правки; R1..R5 до неї.
        var ordinals = reordered
            ? new Dictionary<int, int> { [1] = 1, [4] = 2, [3] = 3, [2] = 4, [5] = 5 }
            : new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 5 };

        var rowsByKey = new Dictionary<(int, string), RowDef>();
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 1; i <= 5; i++)
        {
            var row = new RowDef(TableId, RowKey.Create($"R{i}"), ordinals[i], Text($"Row {i}"), RowKind.Item);
            SetId(row, 100 + i);
            table.AddRow(row);
            rowsByKey[(TableId, row.RowKeyValue)] = row;
            rowIds[$"R{i}"] = 1000 + i;
        }

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "SUM([T1].[R1:R3].[A])", ExpressionDialect.Template);
        SetId(formula, FormulaId);
        formula.AssignColumn(TotalColumnId);
        formula.SetEvaluationOrder(1);
        table.AddFormula(formula);

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [TotalColumnId] = total },
            rowsByKey);

        var period = new PeriodKey(PeriodKeyValue);

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [InstanceId] = [] });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        // Граф ОПУБЛІКОВАНОЇ версії: R1, R2, R3 у порядку, розкритому при публікації.
        IReadOnlyList<FormulaDependency> graph = withPublishedGraph
            ?
            [
                .. Enumerable.Range(1, 3).Select(i => FormulaDependency.ForFormula(
                    FormulaId, DependencyExtractor.KindCell, TableId, $"R{i}", InputColumnId,
                    filterJson: null, periodOffset: null, sortOrder: i - 1)),
            ]
            : [];

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(graph);

        var exporter = new ExcelExporter(
            store, rows, metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), templateVersions: versions);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: true, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        using var workbook = new XLWorkbook(output);
        var worksheet = workbook.Worksheet("Sheet");

        // Заголовок — рядок 2, дані — з рядка 3; TOTAL — друга колонка. Формула колонки лежить у кожному рядку.
        return worksheet.Cell(7, 2).FormulaA1.TrimStart('=');
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
