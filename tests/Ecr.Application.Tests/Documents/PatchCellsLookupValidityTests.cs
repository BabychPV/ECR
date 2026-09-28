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
/// <c>C7</c>: <c>Lookup</c>-комірка на шляху запису не бере запис, якого пікер
/// не пропонує, — чужого довідника, видалений, вимкнений чи нечинний на
/// останній день періоду. Той самий запис, що вже стоїть у комірці, проходить.
/// </summary>
/// <remarks>
/// ⛔ До фіксу обробник питав лише «чи існує Id» (<c>FindExistingEntryIdsAsync</c>),
/// і кожен тест відмови нижче був зеленим НА ЗАПИСІ: <c>ApplyAsync</c> кликався.
/// </remarks>
public sealed class PatchCellsLookupValidityTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int LookupColumnId = 12;
    private const int ColumnRegistryDefId = 1;
    private const int OtherRegistryDefId = 2;
    private const long ExistingRowId = 1001;
    private const long EntryId = 5;

    private static readonly DateOnly PeriodStart = new(2026, 1, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 1, 31);

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

    public PatchCellsLookupValidityTests()
    {
        var lookup = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Permit"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" }), 1, CellDataType.Lookup);
        lookup.SetLookup(registryDefId: ColumnRegistryDefId);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(lookup, LookupColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(lookup);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [LookupColumnId] = lookup },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, DocumentId, TableDefId: 3, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _rows.GetRowsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<RowState> { new("7001001", ExistingRowId, "0x0A", IsOrphaned: false) });
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(a => a, _ => EditDecision.Allow()));
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<string>>(2).ToDictionary(
                   k => k,
                   _ => new NewRowAccess(EditDecision.Allow(), new Dictionary<int, EditDecision>
                   {
                       [LookupColumnId] = EditDecision.Allow(),
                   }),
                   StringComparer.Ordinal));
        _rows.CreateRowsAsync(
                 TableInstance, Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(),
                 Arg.Any<CancellationToken>())
             .Returns(new List<long> { 2001L });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _cells.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, string> { [ExistingRowId] = "0x0B", [2001L] = "0x0C" });

        // Порожня комірка до запису — кожен тест, що про «те саме значення»,
        // підставляє своє.
        StoredValue(null);

        _periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(PeriodStart, PeriodEnd));
    }

    private PatchCellsHandler Handler()
        => new(_cells, _rows, Substitute.For<IDocumentStore>(), _periods, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(),
               Substitute.For<IAuditReader>(), Substitute.For<IBackgroundJobScheduler>(), _uow, _user, _clock,
               Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>());

    private void Entry(
        int registryDefId = ColumnRegistryDefId,
        bool isActive = true,
        bool isDeleted = false,
        DateOnly? validFrom = null,
        DateOnly? validTo = null)
        => _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                      .Returns(new List<RegistryEntryStanding>
                      {
                          new(EntryId, registryDefId, isActive, isDeleted, validFrom, validTo),
                      });

    private void StoredValue(long? entryId)
        => _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(entryId is null
                     ? new Dictionary<CellAddress, CellValueData>()
                     : new Dictionary<CellAddress, CellValueData>
                     {
                         [new CellAddress(PeriodKey.Parse(Period), ExistingRowId, LookupColumnId)] =
                             new() { ValueRegistryEntryId = entryId },
                     });

    private Task<PatchCellsResponse> Write(string rowKey = "7001001", string? baseVersion = "0x0A")
        => Handler().HandleAsync(
            new PatchCellsRequest(TableInstance, Period, "UserEdit",
                [new PatchRow(rowKey, baseVersion, [new PatchCell("Permit", EntryId)])]),
            CancellationToken.None);

    private async Task AssertRejected(string messageKey)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal("ECR-CELL-4223", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        Assert.Equal("1", error.Details["cellCount"]);

        // Комірка не змінена: до сховища запис не дійшов.
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertWritten()
    {
        var response = await Write();

        Assert.Equal(1, response.AppliedCells);
        await _cells.Received(1).ApplyAsync(
            Arg.Is<CellChangeSet>(c => c.Upserts.Single().Value.ValueRegistryEntryId == EntryId),
            Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Запис_чужого_довідника_відхиляється()
    {
        Entry(registryDefId: OtherRegistryDefId);

        await AssertRejected("err.ECR-CELL-4223.foreignRegistry");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Вимкнений_запис_відхиляється()
    {
        Entry(isActive: false);

        await AssertRejected("err.ECR-CELL-4223.inactiveEntry");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Видалений_запис_відхиляється()
    {
        // Так його лишає `RegistryEntry.SoftDelete`: видалений і вимкнений разом.
        Entry(isActive: false, isDeleted: true);

        await AssertRejected("err.ECR-CELL-4223.deletedEntry");
    }

    /// <summary>
    /// Виключна межа <c>ValidTo</c> = останній день періоду: запис чинний до
    /// 30 січня включно, а дата перевірки — 31 січня.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Запис_що_закінчився_до_кінця_періоду_відхиляється()
    {
        Entry(validTo: PeriodEnd);

        await AssertRejected("err.ECR-CELL-4223.entryNotValidOnDate");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Запис_що_починається_після_періоду_відхиляється_з_датою()
    {
        Entry(validFrom: PeriodEnd.AddDays(1));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal("err.ECR-CELL-4223.entryNotValidOnDate", error.Details!["messageKey"]);
        Assert.Equal("2026-01-31", error.Details["asOf"]);
    }

    /// <summary>
    /// Дата перевірки — КІНЕЦЬ періоду, як у пікері й сканері осиротілих рядків:
    /// запис, що почав діяти посеред січня, для січневого звіту чинний.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Запис_чинний_на_останній_день_періоду_приймається()
    {
        Entry(validFrom: new DateOnly(2026, 1, 15), validTo: PeriodEnd.AddDays(1));

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Активний_запис_свого_довідника_приймається()
    {
        Entry();

        await AssertWritten();
    }

    /// <summary>
    /// Запис став вимкненим ПІСЛЯ того, як його обрали: повтор того самого
    /// значення (undo, вставка, імпорт незміненої книги) не є новим вибором.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Те_саме_значення_що_вже_стоїть_проходить_навіть_для_вимкненого_запису()
    {
        Entry(isActive: false, isDeleted: true, registryDefId: OtherRegistryDefId);
        StoredValue(EntryId);

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Інше_значення_в_комірці_не_рятує_вимкнений_запис()
    {
        Entry(isActive: false);
        StoredValue(EntryId + 1);

        await AssertRejected("err.ECR-CELL-4223.inactiveEntry");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Новий_рядок_із_вимкненим_записом_відхиляється()
    {
        Entry(isActive: false);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write("NEW-1", baseVersion: null));

        Assert.Equal("err.ECR-CELL-4223.inactiveEntry", error.Details!["messageKey"]);
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Неіснуючий_запис_як_і_раніше_missingEntry()
    {
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding>());

        await AssertRejected("err.ECR-CELL-4223.missingEntry");
    }
}
