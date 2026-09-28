// tests/Ecr.Adapters.Tests/Excel/ExcelImportLookupValidityTests.cs
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
/// <c>C7</c>: імпорт книги не обходить перевірку придатності запису довідника —
/// зміна <c>Lookup</c>-комірки на вимкнений запис відхиляється так само, як
/// прямий <c>PATCH</c>.
/// </summary>
/// <remarks>
/// ⚠ <c>ExcelImporter.ApplyAsync</c> пише ЛИШЕ через
/// <c>PatchCellsHandler.HandleAsync</c> (окремого шляху запису в нього немає),
/// тож обробник тут справжній, а підмінено лише сховища. Тест доводить, що
/// відмова доходить до імпорту і що жодна таблиця книги не записана.
/// </remarks>
public sealed class ExcelImportLookupValidityTests
{
    private const long DocumentId = 700;
    private const long Instance = 501;
    private const long RowId = 1001;
    private const int Period = 202601;
    private const int TableDefId = 3;
    private const int TemplateVersionId = 2;
    private const int PermitColumnId = 12;
    private const int PermitRegistryDefId = 1;
    private const long EntryId = 5;
    private const string Token = "tok";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IImportPreviewStore _previews = Substitute.For<IImportPreviewStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public ExcelImportLookupValidityTests()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        Clock = clock;

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), Instance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(a => a, _ => EditDecision.Allow()));

        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());
        _methodologies.GetMethodologyIdsBoundToTableAsync(TableDefId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());
        _periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        _rows.ResolveTableInstanceAsync(Instance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(Instance, DocumentId, TableDefId, TemplateVersionId, Period));
        _rows.GetRowsAsync(Instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<RowState> { new("R1", RowId, "0xAA", IsOrphaned: false) });
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>());
        _cells.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, string> { [RowId] = "0xAB" });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _previews.FindAsync(Token, Arg.Any<CancellationToken>()).Returns(JsonSerializer.Serialize(
            new ImportPlan(
                DocumentId,
                Period,
                [
                    new TableDiff(
                        Instance, Period, [new ImportChange("R1", "Permit", null, EntryId)], [],
                        new Dictionary<string, string> { ["R1"] = "0xAA" }),
                ]),
            Options));
    }

    private IClock Clock { get; }

    private void Entry(bool isActive)
        => _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                      .Returns(new List<RegistryEntryStanding>
                      {
                          new(EntryId, PermitRegistryDefId, isActive, IsDeleted: false, null, null),
                      });

    private static TemplateVersionSnapshot Snapshot()
    {
        var permit = new ColumnDef(TableDefId, EcrCode.Create("Permit"), Name("Permit"), 1, CellDataType.Lookup);
        permit.SetLookup(registryDefId: PermitRegistryDefId);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(permit, PermitColumnId);

        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("Water"), Name("Water"), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"), Name("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, TableDefId);
        table.AddColumn(permit);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [PermitColumnId] = permit },
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private ExcelImporter Importer()
        => new(
            _metadata, _registries, _access, _user, _previews,
            new PatchCellsHandler(
                _cells, _rows, Substitute.For<IDocumentStore>(), _periods, _metadata, _access,
                new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
                _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(),
                Substitute.For<IAuditReader>(), _jobs, _uow, _user, Clock,
                Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>()),
            new ImportDiffBuilder(), _cells, _rows, _uow, _jobs, Substitute.For<ISheetEditGate>());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C7")]
    public async Task Імпорт_зі_зміною_на_вимкнений_запис_відхиляється_і_нічого_не_пише()
    {
        Entry(isActive: false);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().ApplyAsync(DocumentId, Token, CancellationToken.None));

        Assert.Equal("ECR-CELL-4223", error.ErrorCode);
        Assert.Equal("err.ECR-CELL-4223.inactiveEntry", error.Details!["messageKey"]);

        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await _jobs.DidNotReceive().EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _previews.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Контроль: активний запис свого довідника імпортується.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C7")]
    public async Task Імпорт_зі_зміною_на_активний_запис_застосовується()
    {
        Entry(isActive: true);

        var response = await Importer().ApplyAsync(DocumentId, Token, CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        await _cells.Received(1).ApplyAsync(
            Arg.Is<CellChangeSet>(c => c.Upserts.Single().Value.ValueRegistryEntryId == EntryId),
            Arg.Any<CancellationToken>());
    }
}
