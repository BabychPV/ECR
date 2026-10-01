// tests/Ecr.Adapters.Tests/Excel/ExcelImportStaleWorkbookTests.cs
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
/// P3 імпорту: відхилена обчислювана комірка каже, ЧОМУ — «змінили» чи
/// «книга застаріла після перерахунку», — і кожна відмова несе адресу комірки книги.
/// </summary>
/// <remarks>
/// ⚠ Експорт → імпорт справжніми <see cref="ExcelExporter"/> і
/// <see cref="ExcelImporter"/> (тим самим стендом, що
/// <c>ExcelImportCalculatedColumnRoundTripTests</c>): відбиток комірки пише
/// експорт, а читає імпорт, і перевірка лише однієї половини довела б тільки,
/// що вона узгоджена сама з собою.
/// </remarks>
public sealed class ExcelImportStaleWorkbookTests
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

    public ExcelImportStaleWorkbookTests() => Arrange(withResults: true);

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
                    new CellRecord(new CellAddress(Period, 1001, InputColumnId), TableId, new CellValueData { ValueNumeric = 8m }),
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
    public async Task Перерахунок_після_експорту_на_незміненій_книзі_дає_причину_застаріла_книга()
    {
        using var workbook = await ExportAsync();
        Recalculated(r1: 25m, r2: 12.5m);

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-CELL-4221", rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.CalculatedStale, rejection.MessageKey);
        Assert.Equal("R1", rejection.RowKey);
        Assert.Equal("B3", rejection.ExcelCell);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Змінена_користувачем_обчислювана_комірка_дає_причину_змінили()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 2).Value = 21;

        var preview = await ImportAsync(workbook);

        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.Calculated, rejection.MessageKey);
        Assert.Equal("B3", rejection.ExcelCell);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Правка_користувача_поверх_перерахунку_лишається_причиною_змінили()
    {
        // ⚠ Обидві причини разом: число в книзі вже не те, що поклав експорт, —
        // отже його вписала людина, і саме це їй треба почути.
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 2).Value = 21;
        Recalculated(r1: 25m, r2: 12.5m);

        var preview = await ImportAsync(workbook);

        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.Calculated, rejection.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожня_при_експорті_комірка_що_отримала_результат_дає_застаріла_книга()
    {
        Arrange(withResults: false);
        using var workbook = await ExportAsync();
        Recalculated(r1: 20m, r2: 12.5m);

        var preview = await ImportAsync(workbook);

        Assert.Equal(2, preview.Rejected.Count);
        Assert.All(preview.Rejected, r => Assert.Equal(ImportMessageKeys.CalculatedStale, r.MessageKey));
        Assert.Equal(["B3", "B4"], preview.Rejected.Select(r => r.ExcelCell).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Стерта_користувачем_обчислювана_комірка_не_читається_як_застаріла()
    {
        // ⚠ Порожнеча — теж значення відбитка: стерте число не має збігтися з
        // експортованим, інакше людина почула б «вивантажте наново» замість
        // «ви змінили комірку».
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 2).Clear(XLClearOptions.Contents);

        var preview = await ImportAsync(workbook);

        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal(ImportMessageKeys.Calculated, rejection.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відмова_типу_у_вхідній_колонці_несе_адресу_комірки_книги()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(4, 1).Value = "abc";

        var preview = await ImportAsync(workbook);

        var rejection = Assert.Single(preview.Rejected);
        Assert.Equal("R2", rejection.RowKey);
        Assert.Equal("A", rejection.ColumnCode);
        Assert.Equal("A4", rejection.ExcelCell);
    }

    private void Recalculated(decimal r1, decimal r2)
        => _results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CalculationResultRow(MethodologyVersionId, "R1", "EMISSION", r1, 8, null),
                new CalculationResultRow(MethodologyVersionId, "R2", "EMISSION", r2, 8, null),
            ]);

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
