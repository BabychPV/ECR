using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
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

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// LAND-YEAR G6 (D-NEW-2, «лише річна розкладка»): «попереднє» значення комірки, яке судить <c>C7</c> («той самий
/// вимкнений запис, що вже стоїть, проходить»), у версії <c>YearWithMonthColumns</c> читається у ВЛАСНИКА колонки, а не
/// у фізичній комірці перегляду.
/// </summary>
/// <remarks>
/// <para>
/// Світ: річний перегляд — якір 202601 (екземпляр 500, рядок 1001). <c>Permit</c> — місячна Lookup-колонка вересня:
/// власник — екземпляр 600 @ 202609 (рядок 2001, той самий RowKey). У власника вже стоїть запис 5, який відтоді
/// вимкнули; фізична комірка перегляду порожня (саме так зберігає Q-17). Користувач повторює те саме значення
/// (undo, вставка, імпорт незміненої книги) — це не новий вибір.
/// </para>
/// <para>
/// ⛔ Мутація (локально, не комітити): у <c>PatchCellsHandler.ReadPreviousValuesAsync</c> лишити лише
/// <c>cellStore.ReadCellsAsync(addressesToWrite)</c> — річний тест червоніє (<c>inactiveEntry</c>), контролі зелені.
/// </para>
/// </remarks>
public sealed class PatchCellsYearLayoutPreviousTests
{
    private const long ViewInstance = 500;
    private const long OwnerInstance = 600;
    private const long DocumentId = 700;
    private const int ViewPeriod = 202601;
    private const int OwnerPeriod = 202609;
    private const int LookupColumnId = 12;
    private const long ViewRow = 1001;
    private const long OwnerRow = 2001;
    private const long EntryId = 5;
    private const string RowKeyValue = "7001001";

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly FakePermitGroupResolver _groups = new();
    private readonly ColumnDef _lookup;

    public PatchCellsYearLayoutPreviousTests()
    {
        _lookup = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Permit_M09"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" }), 1, CellDataType.Lookup);
        _lookup.SetLookup(registryDefId: 1);
        _lookup.SetMonth(true, 9);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(_lookup, LookupColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Land"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Land" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(_lookup);
        sheet.AddTable(table);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [LookupColumnId] = _lookup },
            RowsByKey: new Dictionary<(int, string), RowDef>()));

        _clock.UtcNow.Returns(new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _rows.ResolveTableInstanceAsync(ViewInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(ViewInstance, DocumentId, TableDefId: 3, TemplateVersionId: 2, PeriodKey: ViewPeriod));
        _rows.GetRowsAsync(ViewInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<RowState> { new(RowKeyValue, ViewRow, "0x0A", IsOrphaned: false) });
        _rows.GetRowIdsAsync(ViewInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { [RowKeyValue] = ViewRow });
        _rows.GetTableInstancesOfTableInYearAsync(DocumentId, 3, 2026, Arg.Any<CancellationToken>())
             .Returns(new List<TableInstanceRef>
             {
                 new(ViewInstance, DocumentId, 3, 2, ViewPeriod),
                 new(OwnerInstance, DocumentId, 3, 2, OwnerPeriod),
             });
        _rows.GetRowIdsAsync(OwnerInstance, PeriodKey.Parse(OwnerPeriod), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { [RowKeyValue] = OwnerRow });

        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());
        _periods.FindPeriodBoundsAsync(DocumentId, ViewPeriod, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), ViewInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(a => a, _ => EditDecision.Allow()));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        // Запис 5 вимкнений після того, як його обрали.
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding> { new(EntryId, 1, IsActive: false, IsDeleted: false, null, null) });

        // Фізичне сховище: значення лише у власника (202609); комірка перегляду (202601) порожня.
        var stored = new Dictionary<CellAddress, CellValueData>
        {
            [new CellAddress(PeriodKey.Parse(OwnerPeriod), OwnerRow, LookupColumnId)] = new() { ValueRegistryEntryId = EntryId },
        };
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<CellAddress, CellValueData>)call.ArgAt<IReadOnlyCollection<CellAddress>>(0)
                  .Where(stored.ContainsKey)
                  .Distinct()
                  .ToDictionary(a => a, a => stored[a]));
    }

    /// <summary>Роутер: місячна колонка вересня має власника 202609; <paramref name="yearLayout"/> — розкладка версії.</summary>
    private IMonthColumnRouter Router(bool yearLayout)
    {
        var router = Substitute.For<IMonthColumnRouter>();
        router.Enabled.Returns(true);
        router.RoutesComputedVersion(2).Returns(yearLayout);
        router.GetDocumentAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(new MonthRoutingDocument(DocumentId, 1, 2));
        router.AppliesToTable(Arg.Any<TableDef>()).Returns(true);
        router.ForeignColumnsAsync(
                  Arg.Any<MonthRoutingDocument>(), Arg.Any<TableDef>(), PeriodKey.Parse(ViewPeriod), Arg.Any<CancellationToken>())
              .Returns((IReadOnlyList<ForeignColumn>)[new ForeignColumn(_lookup, PeriodKey.Parse(OwnerPeriod))]);
        router.OwnerPeriodAsync(
                  Arg.Any<MonthRoutingDocument>(), Arg.Any<TableDef>(), Arg.Any<ColumnDef>(), Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(call => call.ArgAt<ColumnDef>(2).Id == LookupColumnId
                  ? PeriodKey.Parse(OwnerPeriod)
                  : call.ArgAt<PeriodKey>(3));
        return router;
    }

    private PatchCellsHandler Handler(IMonthColumnRouter router)
        => new(_cells, _rows, Substitute.For<IDocumentStore>(), _periods, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(),
               Substitute.For<IAuditReader>(), Substitute.For<IBackgroundJobScheduler>(), _uow, _user, _clock,
               Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>(), groups: _groups, monthRouter: router);

    private Task<PatchCellsResponse> Write(IMonthColumnRouter router)
        => Handler(router).HandleAsync(
            new PatchCellsRequest(ViewInstance, ViewPeriod, "UserEdit",
                [new PatchRow(RowKeyValue, "0x0A", [new PatchCell("Permit_M09", EntryId)])]),
            CancellationToken.None);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-474")] [Trait("Finding", "C7")]
    public async Task Річна_розкладка_той_самий_вимкнений_запис_у_власника_проходить_і_пишеться_у_власника()
    {
        var response = await Write(Router(yearLayout: true));

        Assert.Equal(1, response.AppliedCells);
        await _cells.Received(1).ApplyBatchAsync(
            Arg.Is<IReadOnlyCollection<CellChangeSet>>(sets => sets.Any(
                s => s.TableInstanceId == OwnerInstance
                     && s.Upserts.Any(u => u.Address == new CellAddress(PeriodKey.Parse(OwnerPeriod), OwnerRow, LookupColumnId)
                                           && u.Value.ValueRegistryEntryId == EntryId))),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Контроль D-NEW-2: версія <c>PerMonthCopies</c> під прапором Q-17 — «попереднє» фізичне, як до G6: той самий
    /// вимкнений запис відхиляється (порожня комірка перегляду), і жодного читання екземплярів року до відмови.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-474")] [Trait("Finding", "C7")]
    public async Task Контроль_PerMonthCopies_попереднє_фізичне_без_запитів_власника()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write(Router(yearLayout: false)));

        Assert.Equal("err.ECR-CELL-4223.inactiveEntry", error.Details!["messageKey"]);
        await _rows.DidNotReceive().GetTableInstancesOfTableInYearAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _rows.DidNotReceive().GetRowIdsAsync(OwnerInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _cells.Received(1).ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Контроль кількості запитів PerMonthCopies на успішному записі (активний запис): читання власників — лише одне,
    /// у самому записі (<c>RouteForeignOwnersAsync</c>), як до G6.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Requirement", "D-474")]
    public async Task Контроль_PerMonthCopies_успішний_запис_не_додає_запитів_власника()
    {
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding> { new(EntryId + 1, 1, IsActive: true, IsDeleted: false, null, null) });

        var response = await Handler(Router(yearLayout: false)).HandleAsync(
            new PatchCellsRequest(ViewInstance, ViewPeriod, "UserEdit",
                [new PatchRow(RowKeyValue, "0x0A", [new PatchCell("Permit_M09", EntryId + 1)])]),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        await _rows.Received(1).GetTableInstancesOfTableInYearAsync(
            DocumentId, 3, 2026, Arg.Any<CancellationToken>());
        await _rows.Received(1).GetRowIdsAsync(OwnerInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }
}
