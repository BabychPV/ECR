// tests/Ecr.Adapters.Tests/Excel/ExcelImportSheetCopyLockTests.cs
using System.Text.Json;
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
/// C-3 (аудит 2026-11): застосування імпорту в КОПІЮ аркуша (RS) бере замок і стан КОПІЇ — те саме правило, що
/// поштучний PATCH (<c>EnterEditOfAsync</c>), — а не оригіналу.
/// </summary>
/// <remarks>
/// Мутація (червоніють обидва): у <c>ExcelImporter.ApplyAsync</c> і <c>PatchCellsHandler.PersistWorkbookAsync</c>
/// повернути ключ <c>SheetDefId</c> і <c>EnterEditAsync</c> оригіналу. Тоді копія-чернетка при поданому оригіналі
/// відмовляє (перший тест), а подана копія при оригіналі-чернетці приймає запис (другий — диференціальний Deny).
/// </remarks>
public sealed class ExcelImportSheetCopyLockTests
{
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TemplateVersionId = 2;
    private const string Token = "tok";
    private const long Instance = 601;
    private const int TableDefId = 3;
    private const int ColumnId = 11;
    private const int SheetDefId = 10;
    private const long CopyId = 77;
    private const long RowId = 2001;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();

    private int _writes;

    public ExcelImportSheetCopyLockTests()
    {
        BatchStoreStubs.DelegateToSingle(_rows, _cells, _access);
        _clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());
        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());

        var instance = new TableInstanceRef(Instance, DocumentId, TableDefId, TemplateVersionId, Period, CopyId);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([instance]);
        _rows.ResolveTableInstanceAsync(Instance, Arg.Any<CancellationToken>()).Returns(instance);
        _rows.GetRowsAsync(Instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<RowState> { new("R1", RowId, "0xAA", IsOrphaned: false) });
        _rows.GetRowVersionsAsync(Instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["R1"] = "0xAA" });
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), Instance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>
               {
                   [new CellAddress(PeriodKey.Parse(Period), RowId, ColumnId)] = EditDecision.Allow(),
               });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _cells.When(c => c.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>()))
              .Do(_ => _writes++);

        _previews.FindAsync(Token, Arg.Any<CancellationToken>()).Returns(Plan());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C-3")]
    public async Task Apply_пише_в_копію_чернетку_коли_оригінал_поданий()
    {
        States(original: DocumentStatus.Submitted, copy: DocumentStatus.Draft);

        await Importer().ApplyAsync(DocumentId, Token, CancellationToken.None);

        Assert.Equal(1, _writes);
        await _gate.Received().EnterCopyEditAsync(DocumentId, SheetDefId, Arg.Any<PeriodKey>(), CopyId, Arg.Any<CancellationToken>());
        await _gate.DidNotReceive().EnterEditAsync(DocumentId, SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C-3")]
    public async Task Apply_відмовляє_в_подану_копію_навіть_коли_оригінал_чернетка()
    {
        // ⛔ Диференціальний Deny на справжньому шляху: той самий план, що вище, — інший лише стан копії.
        States(original: DocumentStatus.Draft, copy: DocumentStatus.Submitted);

        var error = await Assert.ThrowsAnyAsync<EcrException>(() => Importer().ApplyAsync(DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal(0, _writes);
    }

    private void States(DocumentStatus original, DocumentStatus copy)
    {
        _gate.EnterEditAsync(DocumentId, SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(original));
        _gate.EnterEditNoWaitAsync(DocumentId, SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(original));
        _gate.EnterCopyEditAsync(DocumentId, SheetDefId, Arg.Any<PeriodKey>(), CopyId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(copy));
        _gate.EnterCopyEditNoWaitAsync(DocumentId, SheetDefId, Arg.Any<PeriodKey>(), CopyId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(copy));
    }

    private ExcelImporter Importer()
        => new(
            _metadata, _registries, _access, _user, _previews,
            new PatchCellsHandler(
                _cells, _rows, Substitute.For<IDocumentStore>(), Substitute.For<IPeriodStore>(), _metadata, _access,
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
                _jobs, _uow, _user, _clock, _gate, Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cells, _rows, _uow, _jobs, _gate);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static TemplateVersionSnapshot Snapshot()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S10"), Name("Sheet"), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, SheetDefId);

        var table = new TableDef(
            SheetDefId, EcrCode.Create("T3"), Name("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, TableDefId);

        var column = new ColumnDef(TableDefId, EcrCode.Create("Volume"), Name("Volume"), 1, CellDataType.Decimal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, ColumnId);

        table.AddColumn(column);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [ColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static string Plan()
        => JsonSerializer.Serialize(
            new ImportPlan(
                DocumentId, Period,
                [
                    new TableDiff(
                        Instance, Period, [new ImportChange("R1", "Volume", null, 10m)], [],
                        new Dictionary<string, string> { ["R1"] = "0xAA" }),
                ]),
            Options);
}
