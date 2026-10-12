// tests/Ecr.Adapters.Tests/Excel/ExcelImportAllOrNothingTests.cs
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
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
/// RC21 / EA-6 (рішення людини 12.10, квіз 5 №13): Excel-імпорт виконавця з чинною передачею місяця —
/// «все або нічого». Перегляд показує заборонені комірки у <c>Rejected</c>, а застосування (і перевірка
/// порогу «у чергу») за наявності хоч однієї відмовляє ВЕСЬ перелік змін 403 — нічого не записано.
/// </summary>
/// <remarks>
/// ⚠ Справжній <see cref="ExcelImporter"/>, експорт справжнім <see cref="ExcelExporter"/>; сховища —
/// заглушки. Рішення доступу — з <see cref="EditDecision.AllowViaAssignment"/> (так службу доступу
/// бачить виконавець передачі: підйом Read → Write).
/// Мутація: прибрати виклик <c>EnsureAllOrNothing</c> у <c>ExcelImporter.LoadPlanAsync</c> — перший і
/// другий тести червоніють (застосування йде, а в плані лишається лише дозволена комірка).
/// Не записувати <c>AllOrNothing</c> у план (<c>IsUnderAssignment</c> → <c>false</c>) — те саме.
/// </remarks>
public sealed class ExcelImportAllOrNothingTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int SecondColumnId = 12;

    private static readonly PeriodKey Period = new(PeriodKeyValue);
    private static readonly JsonSerializerOptions PlanOptions = new(JsonSerializerDefaults.Web);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();
    private string? _savedPlan;

    public ExcelImportAllOrNothingTests()
    {
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(InstanceId, DocumentId, TableId, TemplateVersionId, PeriodKeyValue)]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [InstanceId] = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1001, ["R2"] = 1002 },
            });
        _rows.GetRowVersionsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, string>>
            {
                [InstanceId] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["R1"] = "AAAAAAAAB9E=",
                    ["R2"] = "AAAAAAAAB9I=",
                },
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
        _previews.SaveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call => _savedPlan = call.ArgAt<string>(1));
        _previews.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_savedPlan));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-6")]
    public async Task Виконавець_з_передачею_і_однією_забороненою_коміркою_весь_імпорт_відхилено_нічого_не_записано()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 10; // R1.A — поза передачею
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A — у передачі

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1001, InputColumnId)] = EditDecision.Deny(EditDenyReason.InsufficientGrantLevel),
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.AllowViaAssignment(),
            });

        // Перегляд — як і раніше: 200, дозволена зміна й відмова з причиною.
        Assert.Equal("R2", Assert.Single(preview.Changes).RowKey);
        var rejected = Assert.Single(preview.Rejected);
        Assert.Equal("R1", rejected.RowKey);
        Assert.Equal("ECR-ACCS-0403", rejected.ReasonCode);

        // ⛔ Застосування — 403 цілком, а не часткове 200 з R2.
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => importer.ApplyAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.importDeniedCells", error.Details!["messageKey"]);
        Assert.Equal("1", error.Details["deniedCount"]);
        var cell = Assert.Single(Assert.IsType<List<Dictionary<string, string?>>>(error.Details["cells"]));
        Assert.Equal("R1", cell["rowKey"]);
        Assert.Equal("A", cell["columnCode"]);
        Assert.Equal("A3", cell["excelCell"]);

        // ⛔ Ні транзакції, ні запису, ні знятого перегляду: нічого не записано.
        Assert.DoesNotContain(_uow.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("Transaction", StringComparison.Ordinal));
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == "ApplyBatchAsync");
        await _previews.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Постановка «у чергу» (поріг) відмовляє так само, ДО фонової задачі.
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-6")]
    public async Task Відмова_не_розкриває_прихованих_колонок_лише_рахує_їх()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A — у передачі
        workbook.Worksheet("Sheet").Cell(3, 2).Value = 12; // R1.B — колонка, якої виконавець не бачить

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.AllowViaAssignment(),
            },
            hiddenColumnId: SecondColumnId);

        Assert.Equal("B", Assert.Single(preview.Rejected).ColumnCode);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => importer.ApplyAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));

        // ⛔ Нерозкриття: комірка прихованої колонки — лише в загальному числі, без назви в `cells`.
        Assert.Equal("1", error.Details!["deniedCount"]);
        Assert.Empty(Assert.IsType<List<Dictionary<string, string?>>>(error.Details["cells"]));
        Assert.DoesNotContain(
            error.Details.Values.OfType<string>(), value => value.Contains("R1", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-6")]
    public async Task Звичайний_користувач_без_передачі_лишає_частковий_шлях_заборонені_лише_показані()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 10;
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6;

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1001, InputColumnId)] = EditDecision.Deny(EditDenyReason.RowReadOnly),
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.Allow(),
            });

        Assert.Single(preview.Rejected);

        // Не EA-виконавець: правило «все або нічого» не вмикається, дозволена зміна лишається застосовною.
        Assert.NotEqual(true, SavedPlan().AllOrNothing);
        Assert.Equal(
            1,
            await importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-6")]
    public async Task Виконавець_з_передачею_без_заборонених_комірок_застосовує_все_як_раніше()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6;

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1001, InputColumnId)] = EditDecision.AllowViaAssignment(),
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.AllowViaAssignment(),
            });

        Assert.Empty(preview.Rejected);
        Assert.True(SavedPlan().AllOrNothing);
        Assert.Equal(
            1,
            await importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    private ImportPlan SavedPlan()
    {
        Assert.NotNull(_savedPlan);
        return JsonSerializer.Deserialize<ImportPlan>(_savedPlan, PlanOptions)!;
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

    private async Task<(ExcelImporter Importer, ImportPreview Preview)> ImportAsync(
        XLWorkbook workbook, IReadOnlyDictionary<CellAddress, EditDecision> decisions, int? hiddenColumnId = null)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var builder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (hiddenColumnId is { } hidden)
        {
            builder = builder.Deny(ResourceKind.Column, hidden);
        }

        var scope = DocumentReadScope.For(builder.Build(), AccessBuilder.ProjectId, _snapshot);

        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => scope);
        access.CanEditSlicesAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>)
                call.ArgAt<IReadOnlyCollection<long>>(1).Distinct().ToDictionary(id => id, _ => decisions));

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
            _uow, Substitute.For<IBackgroundJobScheduler>(), Substitute.For<ISheetEditGate>());

        var preview = await importer.PreviewAsync(DocumentId, stream, CancellationToken.None);
        return (importer, preview);
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
        var second = new ColumnDef(TableId, EcrCode.Create("B"), Text("B"), 2, CellDataType.Decimal);
        SetId(second, SecondColumnId);
        table.AddColumn(second);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [SecondColumnId] = second },
            new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
