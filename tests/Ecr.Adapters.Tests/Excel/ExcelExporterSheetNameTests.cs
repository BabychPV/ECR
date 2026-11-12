// tests/Ecr.Adapters.Tests/Excel/ExcelExporterSheetNameTests.cs
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
/// X-7 (аудит 2026-11): назва аркуша з апострофом не ламає посилань формул, а назви, які Excel забороняє
/// (апостроф на краю, зарезервоване «History»), розводяться до запису книги.
/// </summary>
/// <remarks>
/// Мутації: прибрати подвоєння <c>'</c> у <c>ExcelExporter.Coordinates</c> — перший тест червоний
/// (<c>'Operator's site'!</c>); прибрати <c>Trim('\'')</c> / перевірку «History» у <c>CleanSheetName</c> — другий
/// і третій тести червоні.
/// </remarks>
public sealed class ExcelExporterSheetNameTests
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
    public async Task Апостроф_у_назві_аркуша_подвоюється_в_посиланні_формули()
    {
        using var workbook = await ExportAsync("Operator's site");

        var sheet = workbook.Worksheet("Operator's site");
        var formula = sheet.Cell(3, 2).FormulaA1.TrimStart('=');

        Assert.Equal("SUM('Operator''s site'!A3:'Operator''s site'!A4)", formula);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("'Quoted'", "Quoted")]
    [InlineData("History", "History 1")]
    [InlineData("history", "history 1")]
    public async Task Назва_яку_Excel_забороняє_розводиться(string name, string expected)
    {
        using var workbook = await ExportAsync(name);

        Assert.Equal(expected, workbook.Worksheets.First().Name);
        Assert.Equal("SUM('" + expected + "'!A3:'" + expected + "'!A4)", workbook.Worksheet(expected).Cell(3, 2).FormulaA1.TrimStart('='));
    }

    private static async Task<XLWorkbook> ExportAsync(string sheetName)
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text(sheetName), 1);
        SetId(sheet, 1);
        var table = new TableDef(
            sheet.Id, EcrCode.Create("T1"), Text("Table 1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, TableId);

        var input = new ColumnDef(TableId, EcrCode.Create("A"), Text("A"), 1, CellDataType.Decimal);
        SetId(input, InputColumnId);
        var total = new ColumnDef(TableId, EcrCode.Create("TOTAL"), Text("Total"), 2, CellDataType.Decimal);
        SetId(total, TotalColumnId);
        table.AddColumn(input);
        table.AddColumn(total);

        var rowsByKey = new Dictionary<(int, string), RowDef>();
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 1; i <= 2; i++)
        {
            var row = new RowDef(TableId, RowKey.Create($"R{i}"), i, Text($"Row {i}"), RowKind.Item);
            SetId(row, 100 + i);
            table.AddRow(row);
            rowsByKey[(TableId, row.RowKeyValue)] = row;
            rowIds[$"R{i}"] = 1000 + i;
        }

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "SUM([T1].[R1:R2].[A])", ExpressionDialect.Template);
        SetId(formula, FormulaId);
        formula.AssignColumn(TotalColumnId);
        formula.SetEvaluationOrder(1);
        table.AddFormula(formula);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [TotalColumnId] = total },
            rowsByKey);

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });

        var store = Substitute.For<ICellStore>();
        store.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(PeriodKeyValue), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [InstanceId] = [] });

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var exporter = new ExcelExporter(
            store, rows, metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator());

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: true, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        // Книга перечитується зі збереженого файлу: зламане посилання чи заборонена назва дали б виняток тут.
        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
