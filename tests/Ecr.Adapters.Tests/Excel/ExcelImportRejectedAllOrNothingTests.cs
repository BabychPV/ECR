// tests/Ecr.Adapters.Tests/Excel/ExcelImportRejectedAllOrNothingTests.cs
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
/// AN-143 (слово людини p53: «все або нічого», відмова на СЕРВЕРІ): якщо перегляд має хоч одну відхилену комірку
/// (<c>preview.rejected &gt; 0</c>), застосування відмовляє цілком - <c>ECR-IMP-0422</c> <c>importHasRejected</c>,
/// у базу нічого не пишеться, навіть валідні зміни інших рядків. AN-144: незмінена книга з текстовим результатом
/// обчислюваної колонки не дає <c>ECR-CELL-4221 importCalculatedStale</c>.
/// </summary>
/// <remarks>
/// ⚠ Справжні <see cref="ExcelImporter"/> і <see cref="ExcelExporter"/>; сховища - заглушки.
/// Мутація: прибрати перевірку <c>rejectedCount</c> з <c>EnsureAllOrNothing</c> - тест відмови червоніє;
/// повернути <c>Current()</c> без <c>ValueString</c> - тест AN-144 червоніє.
/// </remarks>
public sealed class ExcelImportRejectedAllOrNothingTests
{
    private const long DocumentId = 1;
    private const int TemplateVersionId = 1;
    private const int PeriodKeyValue = 202610;
    private const long InstanceId = 729;
    private const int TableId = 1;
    private const int InputColumnId = 11;
    private const int CalcColumnId = 12;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();
    private string? _savedPlan;

    public ExcelImportRejectedAllOrNothingTests()
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
                    new CellRecord(new CellAddress(Period, 1001, CalcColumnId), TableId, new CellValueData { ValueString = "No - Нет" }),
                    new CellRecord(new CellAddress(Period, 1002, CalcColumnId), TableId, new CellValueData { ValueString = "Yes - Да" }),
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
    [Trait("Decision", "AN-143")]
    public async Task Валідна_зміна_плюс_відхилена_комірка_в_іншому_рядку_застосування_відмовляє_цілком()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 1).Value = "не число"; // R1.A - ECR-CELL-0422
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A - валідна зміна

        var (importer, preview) = await ImportAsync(workbook);

        Assert.Equal("R2", Assert.Single(preview.Changes).RowKey);
        Assert.Single(preview.Rejected);

        // ⛔ Відмова, не часткове застосування: ні лічильник «у чергу», ні застосування нічого не пишуть.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => importer.ApplyAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
        Assert.Equal("ECR-IMP-0422", error.ErrorCode);
        Assert.Equal(ExcelImporter.ImportRejectedMessageKey, error.Details!["messageKey"]);
        Assert.Equal("1", error.Details["rejectedCount"]);

        Assert.DoesNotContain(_uow.ReceivedCalls(), c => c.GetMethodInfo().Name.Contains("Transaction", StringComparison.Ordinal));
        Assert.DoesNotContain(_cells.ReceivedCalls(), c => c.GetMethodInfo().Name == "ApplyBatchAsync");

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "AN-143")]
    public async Task Без_відхилень_перегляд_застосовується_як_раніше()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(4, 1).Value = 6; // R2.A

        var (importer, preview) = await ImportAsync(workbook);

        Assert.Empty(preview.Rejected);
        Assert.Equal(
            1,
            await importer.CountPendingChangesAsync(DocumentId, preview.PreviewToken, null, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "AN-144")]
    public async Task Незмінений_експорт_з_текстовим_результатом_обчислюваної_колонки_не_дає_відмов()
    {
        using var workbook = await ExportAsync();

        var (_, preview) = await ImportAsync(workbook);

        Assert.Empty(preview.Rejected);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "AN-144")]
    public async Task Змінений_користувачем_текст_обчислюваної_колонки_лишається_відмовою()
    {
        using var workbook = await ExportAsync();
        workbook.Worksheet("Sheet").Cell(3, 2).Value = "Yes - Да"; // R1.Calc: було «No - Нет»

        var (_, preview) = await ImportAsync(workbook);

        var rejected = Assert.Single(preview.Rejected);
        Assert.Equal("ECR-CELL-4221", rejected.ReasonCode);
        Assert.NotEqual(ImportMessageKeys.CalculatedStale, rejected.MessageKey);
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

    private async Task<(ExcelImporter Importer, ImportPreview Preview)> ImportAsync(XLWorkbook workbook)
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
                call.ArgAt<IReadOnlyCollection<long>>(1).Distinct().ToDictionary(
                    id => id,
                    _ => (IReadOnlyDictionary<CellAddress, EditDecision>)new Dictionary<CellAddress, EditDecision>
                    {
                        [new CellAddress(Period, 1001, InputColumnId)] = EditDecision.Allow(),
                        [new CellAddress(Period, 1002, InputColumnId)] = EditDecision.Allow(),
                    }));

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
        var calc = new ColumnDef(TableId, EcrCode.Create("CALC"), Text("Calc"), 2, CellDataType.Calculated);
        SetId(calc, CalcColumnId);
        table.AddColumn(calc);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet],
            new Dictionary<int, ColumnDef> { [InputColumnId] = input, [CalcColumnId] = calc },
            new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
