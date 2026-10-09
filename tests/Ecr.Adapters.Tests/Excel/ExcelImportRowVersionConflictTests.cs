// tests/Ecr.Adapters.Tests/Excel/ExcelImportRowVersionConflictTests.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// D1-02 (HU-13 Q1, варіант A): книга, вивантажена ДО чужої правки рядка, не
/// повертає старе значення поверх неї мовчки — рядок, змінений після експорту,
/// у перегляді стає конфліктом і в план застосування не потрапляє.
/// </summary>
/// <remarks>
/// ⚠ Експорт → імпорт справжніми <see cref="ExcelExporter"/> і
/// <see cref="ExcelImporter"/>: версію рядка в карту пише експорт, а звіряє
/// імпорт, і перевірка лише однієї половини довела б тільки, що вона узгоджена
/// сама з собою. «Чужа правка» — заглушка сховища, яка між експортом і
/// імпортом віддає нове значення і нову версію рядка (так їх бачить
/// <c>PatchCellsHandler</c> після <c>TouchRowsAsync</c>).
/// <para>
/// Тести/мутація — CI, локально не запускались. Мутація: прибрати гілку
/// <c>RowChangedSinceExport</c> у <c>ImportDiffBuilder.Build</c> — перший тест
/// червоний (у перегляді «9 → 8» замість конфлікту); не писати <c>Version</c> в
/// <c>ExcelExporter.WriteTable</c> — так само.
/// </para>
/// </remarks>
public sealed class ExcelImportRowVersionConflictTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();
    private string? _savedPlan;

    public ExcelImportRowVersionConflictTests()
    {
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceId] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 },
            });
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _previews.SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call => _savedPlan = call.ArgAt<string>(1));

        State(r1: 8m, r1Version: "AAAAAAAAB9E=", r2: 5m, r2Version: "AAAAAAAAB9I=");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Чужа_правка_рядка_після_експорту_дає_конфлікт_а_не_повернення_старого_значення()
    {
        // 1. Експорт: R1.A = 8.
        using var workbook = await ExportAsync();

        // 2. Інший оператор у сітці міняє R1.A на 9 — версія рядка змінилась.
        State(r1: 9m, r1Version: "AAAAAAAAC/8=", r2: 5m, r2Version: "AAAAAAAAB9I=");

        // 3. Імпорт тієї самої книги: у ній R1.A = 8 (людина цієї комірки не чіпала).
        var preview = await ImportAsync(workbook);

        // ⛔ До D1-02: зміна «9 → 8» — і Apply мовчки повертав старе число.
        Assert.Empty(preview.Changes);
        var conflict = Assert.Single(preview.Rejected);
        Assert.Equal("R1", conflict.RowKey);
        Assert.Equal("A", conflict.ColumnCode);
        Assert.Equal("ECR-CELL-0409", conflict.ReasonCode);
        Assert.Equal(ImportMessageKeys.RowChangedSinceExport, conflict.MessageKey);
        Assert.Equal("A3", conflict.ExcelCell);

        // Рядок не змінюється: у плані застосування для R1 нічого немає.
        Assert.DoesNotContain("R1", PlannedRowKeys());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Правка_в_незміненому_рядку_застосовується_поруч_із_конфліктом()
    {
        using var workbook = await ExportAsync();
        State(r1: 9m, r1Version: "AAAAAAAAC/8=", r2: 5m, r2Version: "AAAAAAAAB9I=");
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6;

        var preview = await ImportAsync(workbook);

        var change = Assert.Single(preview.Changes);
        Assert.Equal("R2", change.RowKey);
        Assert.Equal(6m, change.NewValue);
        Assert.Equal("R1", Assert.Single(preview.Rejected).RowKey);
        Assert.Equal(["R2"], PlannedRowKeys());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Правка_людини_в_рядку_зміненому_після_експорту_теж_конфлікт()
    {
        // ⚠ Варіант A: рядок, змінений після експорту, не застосовується без
        // явного «перезаписати», навіть якщо число в книзі вписала людина.
        using var workbook = await ExportAsync();
        State(r1: 9m, r1Version: "AAAAAAAAC/8=", r2: 5m, r2Version: "AAAAAAAAB9I=");
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 10;

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Changes);
        Assert.Equal(ImportMessageKeys.RowChangedSinceExport, Assert.Single(preview.Rejected).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_без_змін_після_експорту_імпортується_як_раніше()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 10;

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Rejected);
        var change = Assert.Single(preview.Changes);
        Assert.Equal("R1", change.RowKey);
        Assert.Equal(10m, change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Книга_без_версій_у_карті_поводиться_як_раніше()
    {
        // ⚠ Сумісність: книги, вивантажені до D1-02, версій рядків не мають.
        using var workbook = await ExportAsync();
        StripVersions(workbook);
        State(r1: 9m, r1Version: "AAAAAAAAC/8=", r2: 5m, r2Version: "AAAAAAAAB9I=");

        var preview = await ImportAsync(workbook);

        Assert.Empty(preview.Rejected);
        var change = Assert.Single(preview.Changes);
        Assert.Equal("R1", change.RowKey);
        Assert.Equal(8m, change.NewValue);
    }

    /// <summary>Поточний стан сховища: значення <c>A</c> і версії рядків.</summary>
    private void State(decimal r1, string r1Version, decimal r2, string r2Version)
    {
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["R1"] = r1Version,
                    ["R2"] = r2Version,
                },
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Period, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [InstanceId] =
                [
                    new CellRecord(new CellAddress(Period, 1001, InputColumnId), TableId, new CellValueData { ValueNumeric = r1 }),
                    new CellRecord(new CellAddress(Period, 1002, InputColumnId), TableId, new CellValueData { ValueNumeric = r2 }),
                ],
            });
    }

    /// <summary>Ключі рядків, які план застосування збирається змінити.</summary>
    private List<string> PlannedRowKeys()
    {
        Assert.NotNull(_savedPlan);
        using var plan = JsonDocument.Parse(_savedPlan);

        return
        [
            .. plan.RootElement.GetProperty("tables").EnumerateArray()
                .SelectMany(t => t.GetProperty("changes").EnumerateArray())
                .Select(c => c.GetProperty("rowKey").GetString()!)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static void StripVersions(XLWorkbook workbook)
    {
        var map = workbook.Worksheet(ExcelWorkbookMap.SheetName);
        var json = string.Concat(map.CellsUsed().OrderBy(c => c.Address.RowNumber).Select(c => c.GetString()));
        var stripped = Regex.Replace(json, ",\"version\":\"[^\"]*\"", string.Empty);
        Assert.NotEqual(json, stripped);
        map.Clear();
        for (var offset = 0; offset < stripped.Length; offset += ExcelWorkbookMap.ChunkSize)
        {
            map.Cell((offset / ExcelWorkbookMap.ChunkSize) + 1, 1).Value =
                stripped.Substring(offset, Math.Min(ExcelWorkbookMap.ChunkSize, stripped.Length - offset));
        }
    }

    private async Task<XLWorkbook> ExportAsync()
    {
        var exporter = new ExcelExporter(
            _cells, _rows, _metadata, Substitute.For<IStyleCatalog>(), Substitute.For<IRegistryStore>(),
            new StyleMapper(), new FormulaTranslator());

        await using var output = await exporter.ExportAsync(
            DocumentId,
            new ExcelExportOptions(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: PeriodKeyValue),
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
            _metadata, Substitute.For<IRegistryStore>(), access, user, _previews,
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
            Substitute.For<IUnitOfWork>(), Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>());

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
        table.AddColumn(input);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input },
            new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
