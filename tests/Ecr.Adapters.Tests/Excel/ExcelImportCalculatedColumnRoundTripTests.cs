// tests/Ecr.Adapters.Tests/Excel/ExcelImportCalculatedColumnRoundTripTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// P1 (живий прохід 2026-10-01): експорт → імпорт БЕЗ правок для таблиці з
/// колонкою <c>Calculated</c> давав ECR-CELL-4221 «файл змінює значення».
/// </summary>
/// <remarks>
/// ⛔ Причина: експорт пише в таку колонку число оверлея методології, а імпорт
/// порівнював його з <c>doc.CellValue</c>, де значення немає. Мутація: прибрати
/// накладання оверлея в <c>ExcelImporter.PreviewAsync</c> — тест
/// «Незмінена_книга…» червоний (1 відхилення).
/// </remarks>
public sealed class ExcelImportCalculatedColumnRoundTripTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int EmissionColumnId = 12;
    private const int MethodologyVersionId = 5;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();

    public ExcelImportCalculatedColumnRoundTripTests() => Arrange(withResults: true);

    private void Arrange(bool withResults)
    {
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 };

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [InstanceId] = rowIds });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string> { ["R1"] = "0xAA", ["R2"] = "0xBB" },
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] =
                [
                    new CellRecord(new CellAddress(Period, 1001, InputColumnId), TableId, new CellValueData { ValueNumeric = _inputR1 }),
                    new CellRecord(new CellAddress(Period, 1002, InputColumnId), TableId, new CellValueData { ValueNumeric = 5m }),
                ],
            });

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);

        _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableId, EmissionColumnId, 2, "EMISSION", "{}", [MethodologyVersionId])]);

        _results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns(withResults
                ?
                [
                    new CalculationResultRow(MethodologyVersionId, "R1", "EMISSION", 20m, 8, null),
                    new CalculationResultRow(MethodologyVersionId, "R2", "EMISSION", 12.5m, 8, null),
                ]
                : []);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Незмінена_книга_з_колонкою_методології_не_дає_ні_змін_ні_відхилень()
    {
        using var workbook = await ExportAsync();

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Змінене_вхідне_і_застарілий_кеш_CALC_дає_лише_зміну_входу()
    {
        // V-10: «змінили хоч одне вхідне число» не має блокувати Apply.
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 9;

        var preview = await ImportAsync(workbook);

        var change = Assert.Single(preview.Changes);
        Assert.Equal("R1", change.RowKey);
        Assert.Equal("A", change.ColumnCode);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Змінена_обчислювана_комірка_лишається_відхиленою()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 2).Value = 21;

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-CELL-4221", rejection.ReasonCode);
        Assert.Equal("EMISSION", rejection.ColumnCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Колонка_без_результату_і_порожня_в_книзі_нічого_не_дає()
    {
        Arrange(withResults: false);
        using var workbook = await ExportAsync();

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    // P2: вхідна Decimal з 16 знаками дробу; Excel тримає double (15 значущих цифр).
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("8.1234567890123440", "8.12345678901234", false)] // хвіст 16-го знака — не зміна
    [InlineData("8.1234567890123440", "8.12345678901235", true)] // правка 15-ї цифри — зміна
    [InlineData("0.0000001", "0.0000002", true)] // мале абсолютне значення — зміна
    [InlineData("0.0000001", "0.0000001", false)]
    [InlineData("123456789012345.6", "123456789012346", false)] // різниця лише в 16-й цифрі double — вище точності Excel
    [InlineData("123456789012345.6", "123456789012347", true)]
    public async Task Вхідне_число_з_16_знаками_порівнюється_з_точністю_double_Excel(
        string stored, string edited, bool expectChange)
    {
        _inputR1 = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);
        Arrange(withResults: true);
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = double.Parse(edited, System.Globalization.CultureInfo.InvariantCulture);

        var preview = await ImportAsync(workbook);

        Assert.Equal(expectChange ? 1 : 0, preview.Changes.Count);
        Assert.Empty(preview.Rejected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Незмінена_книга_з_16_знаками_у_вхідній_колонці_не_дає_змін()
    {
        // Справжній експорт (double) → імпорт. Мутація: строге `number == d` у Same → червоний.
        _inputR1 = 8.1234567890123440m;
        Arrange(withResults: true);
        using var workbook = await ExportAsync();

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Empty(preview.Rejected);
    }

    private decimal _inputR1 = 8m;

    private async Task<XLWorkbook> ExportAsync()
    {
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator(), _methodologies, _results);

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: true, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
            CancellationToken.None);

        var copy = new MemoryStream();
        await output.CopyToAsync(copy);
        copy.Position = 0;
        return new XLWorkbook(copy);
    }

    private async Task<ImportPreview> ImportAsync(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReadScopes.Everything(_snapshot));
        access.CanEditSlicesAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>)
                call.ArgAt<IReadOnlyCollection<long>>(1).Distinct().ToDictionary(
                    id => id,
                    _ => (IReadOnlyDictionary<CellAddress, EditDecision>)new Dictionary<CellAddress, EditDecision>()));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var importer = new ExcelImporter(
            _metadata, Substitute.For<IRegistryStore>(), access, user, Substitute.For<IImportPreviewStore>(),
            new PatchCellsHandler(
                Substitute.For<ICellStore>(), Substitute.For<IRowStore>(), Substitute.For<IDocumentStore>(),
                Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(), Substitute.For<IAccessDecisionService>(),
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                Substitute.For<IMethodologyStore>(), Substitute.For<IRegistryStore>(), headers,
                Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
                Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
                Substitute.For<ICurrentUser>(), Substitute.For<IClock>(), Substitute.For<ISheetEditGate>(),
                Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cells, _rows,
            Substitute.For<IUnitOfWork>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>(),
            _methodologies, _results);

        return await importer.PreviewAsync(DocumentId, stream, CancellationToken.None);
    }

    private static TemplateVersionSnapshot Snapshot()
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

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [EmissionColumnId] = emission },
            new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
