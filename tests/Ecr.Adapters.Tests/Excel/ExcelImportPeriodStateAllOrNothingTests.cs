// tests/Ecr.Adapters.Tests/Excel/ExcelImportPeriodStateAllOrNothingTests.cs
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
/// P53 (слово людини 12.10 «все або нічого» + P2-A): змішаний файл Excel — комірки відкритого місяця і місячної
/// колонки ЗАКРИТОГО/ПОДАНОГО/ПОГОДЖЕНОГО місяця (<c>MonthsInColumns</c>, <see cref="EditDecision.ColumnPeriod"/>).
/// Реальна ЗМІНА значення в такому місяці відмовляє ВЕСЬ пакет (<c>ECR-ACCS-0403</c> з <c>reason</c>, як у PATCH,
/// нічого не записано); НЕЗМІНЕНА комірка закритого місяця шуму не дає (ні відмови, ні блокування пакета).
/// </summary>
/// <remarks>
/// ⚠ Справжній <see cref="ExcelImporter"/> і <see cref="ExcelExporter"/>; сховища — заглушки; рішення доступу —
/// задані вручну (так службу доступу бачить перегляд). Перегляд лишає відмову видимою (<c>Rejected</c> з
/// <c>deny.&lt;причина&gt;</c> — клієнт на ній блокує «Застосувати»); сервер при застосуванні відмовляє сам.
/// Мутація: прибрати <c>PeriodDenied</c> з <c>EnsureAllOrNothing</c> — перші три тести червоніють.
/// </remarks>
public sealed class ExcelImportPeriodStateAllOrNothingTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202610;
    private const int ClosedMonth = 202603;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();
    private string? _savedPlan;

    public ExcelImportPeriodStateAllOrNothingTests()
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

    [Theory]
    [InlineData(EditDenyReason.PeriodClosed)]
    [InlineData(EditDenyReason.DocumentSubmitted)]
    [InlineData(EditDenyReason.DocumentApproved)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "P53")]
    public async Task Open_плюс_змінена_комірка_закритого_місяця_весь_пакет_відхилено_нічого_не_записано(EditDenyReason reason)
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = 10; // R1.A — місячна колонка закритого місяця: ЗМІНА
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A — відкритий місяць

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1001, InputColumnId)] = ClosedMonthDecision(reason),
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.Allow(),
            });

        // Перегляд: відмова названа (клієнт блокує «Застосувати»), причина — ключ `deny.<причина>`.
        var rejected = Assert.Single(preview.Rejected);
        Assert.Equal("R1", rejected.RowKey);
        Assert.Equal("ECR-ACCS-0403", rejected.ReasonCode);
        Assert.Equal($"deny.{reason}", rejected.MessageKey);

        // ⛔ Застосування — 403 цілком, як у PATCH: код, `reason`, `deniedCount`; R2 не записано.
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => importer.ApplyAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", error.Details!["messageKey"]);
        Assert.Equal(reason.ToString(), error.Details["reason"]);
        Assert.Equal("1", error.Details["deniedCount"]);

        Assert.DoesNotContain(_uow.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("Transaction", StringComparison.Ordinal));
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == "ApplyBatchAsync");

        // Постановка «у чергу» (поріг) відмовляє так само, ДО фонової задачі.
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "P53")]
    public async Task Лише_відкритий_місяць_застосовується_як_раніше()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1001, InputColumnId)] = EditDecision.Allow(),
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.Allow(),
            });

        Assert.Empty(preview.Rejected);
        Assert.Equal(
            1,
            await importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(EditDenyReason.PeriodClosed)]
    [InlineData(EditDenyReason.DocumentSubmitted)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "P53")]
    public async Task Open_плюс_незмінена_комірка_закритого_місяця_застосовується_без_шуму(EditDenyReason reason)
    {
        using var workbook = await ExportAsync();
        // R1.A (закритий місяць) — як експортовано, 8 = 8: НЕ зміна.
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A — відкритий місяць

        var (importer, preview) = await ImportAsync(
            workbook,
            new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(Period, 1001, InputColumnId)] = ClosedMonthDecision(reason),
                [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.Allow(),
            });

        Assert.Empty(preview.Rejected);
        Assert.Equal("R2", Assert.Single(preview.Changes).RowKey);
        Assert.Equal(
            1,
            await importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "P53")]
    public async Task Звичайна_заборона_без_періоду_колонки_лишає_частковий_шлях()
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
        Assert.Equal(
            1,
            await importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    private static EditDecision ClosedMonthDecision(EditDenyReason reason)
        => EditDecision.Deny(reason) with { ColumnPeriod = ClosedMonth };

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
        XLWorkbook workbook, IReadOnlyDictionary<CellAddress, EditDecision> decisions)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var builder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        var scope = DocumentReadScope.For(builder.Build(), AccessBuilder.ProjectId, _snapshot);

        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
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
