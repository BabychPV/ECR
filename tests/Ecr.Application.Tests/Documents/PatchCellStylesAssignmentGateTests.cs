using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// CELL-STYLE L3b (вимога огляду, EA-B TOCTOU): передача зрізу перевіряється ПОВТОРНО під блокуванням аркуша.
/// Рішення про права прийняте ДО транзакції (дозволено), а між ним і записом передачу знято/позначено
/// «Заповнено»/«Валідовано»/вона спливла/стала ексклюзивною — запис відхиляється, у базі нічого.
/// </summary>
public sealed class PatchCellStylesAssignmentGateTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int ColumnId = 11;
    private const int UserId = 9;
    private const int OwnerId = 1;
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStyleStore _styles = Substitute.For<ICellStyleStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IEditAssignmentStore _assignments = Substitute.For<IEditAssignmentStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public PatchCellStylesAssignmentGateTests()
    {
        var column = new ColumnDef(TableDef, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(column, ColumnId);
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        SetId(sheet, 4);
        var table = new TableDef(4, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(2, 0, [sheet],
            new Dictionary<int, ColumnDef> { [ColumnId] = column },
            new Dictionary<(int, string), RowDef>());
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
               .Returns(_ => ReadScopes.Everything(snapshot));

        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1 });

        _gate.EnterStructureAsync(Document, false, Arg.Any<CancellationToken>()).Returns(2);
        _gate.EnterEditAsync(Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(DocumentStatus.Draft);

        _styles.LoadForUpdateAsync(Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<CellStyle>>([]));

        // Транзакція виконується одразу: тіло — те, що в бою виконується під замком.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
    }

    public static TheoryData<string> Scenarios => new()
    {
        "filled", "validated", "expired", "exclusive-of-other", "revoked-after-lift",
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Передача_змінилась_між_рішенням_і_записом_відмова_без_запису(string scenario)
    {
        // Рішення ДО транзакції: дозволено (для revoked — саме передачею).
        var lifted = scenario == "revoked-after-lift";
        _access.CanClearCellsAsync(
                Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call => new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(new PeriodKey(Period), 1, ColumnId)] =
                    lifted ? EditDecision.AllowViaAssignment() : EditDecision.Allow(),
            });

        // Факти ПІД замком — уже змінені.
        _assignments.ReadFactsForGateAsync(Document, UserId, Arg.Any<CancellationToken>())
                    .Returns(new EditAssignmentGateFacts(OwnerId, Facts(scenario)));

        var handler = Handler();
        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => handler.HandleAsync(
            Document,
            new TableInstanceRef(TableInstance, Document, TableDef, 2, Period),
            new PatchCellStylesRequest(
                Period, [new CellStyleChange("R1", "Volume", "#FFFF00")], false),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        _styles.DidNotReceive().Add(Arg.Any<CellStyle>());
        _styles.DidNotReceive().Remove(Arg.Any<CellStyle>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Передачі_читаються_під_замком_завжди_навіть_якщо_рішення_не_знало_про_них()
    {
        _access.CanClearCellsAsync(
                Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call => new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(new PeriodKey(Period), 1, ColumnId)] = EditDecision.Allow(),
            });
        _assignments.ReadFactsForGateAsync(Document, UserId, Arg.Any<CancellationToken>())
                    .Returns(new EditAssignmentGateFacts(OwnerId, []));

        await Handler().HandleAsync(
            Document,
            new TableInstanceRef(TableInstance, Document, TableDef, 2, Period),
            new PatchCellStylesRequest(Period, [new CellStyleChange("R1", "Volume", "#FFFF00")], false),
            CancellationToken.None);

        await _assignments.Received(1).ReadFactsForGateAsync(Document, UserId, Arg.Any<CancellationToken>());
        _styles.Received(1).Add(Arg.Any<CellStyle>());
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Approved)]
    public async Task Аркуш_поданий_між_рішенням_і_записом_відмова_без_запису_під_замком(DocumentStatus statusUnderLock)
    {
        // Рішення ДО транзакції — «Draft, дозволено»; під блокуванням аркуш уже поданий/затверджений.
        _access.CanClearCellsAsync(
                Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call => new Dictionary<CellAddress, EditDecision>
            {
                [new CellAddress(new PeriodKey(Period), 1, ColumnId)] = EditDecision.Allow(),
            });
        _gate.EnterEditAsync(Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(statusUnderLock);
        _assignments.ReadFactsForGateAsync(Document, UserId, Arg.Any<CancellationToken>())
                    .Returns(new EditAssignmentGateFacts(OwnerId, []));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Document,
            new TableInstanceRef(TableInstance, Document, TableDef, 2, Period),
            new PatchCellStylesRequest(Period, [new CellStyleChange("R1", "Volume", "#FFFF00")], false),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        _styles.DidNotReceive().Add(Arg.Any<CellStyle>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static List<EditAssignmentFact> Facts(string scenario) => scenario switch
    {
        "filled" => [Fact(progress: AssigneeProgress.Filled)],
        "validated" => [Fact(progress: AssigneeProgress.Assigned, validated: true)],
        "expired" => [Fact(progress: AssigneeProgress.Assigned, expiresAt: Now.AddDays(-1))],
        "exclusive-of-other" => [Fact(progress: null, exclusive: true)],
        _ => [],
    };

    private static EditAssignmentFact Fact(
        AssigneeProgress? progress, bool validated = false, bool exclusive = false, DateTime? expiresAt = null)
        => new(
            SheetCode: null, TableCode: null, MonthNumber: null, Period: null,
            IsExclusive: exclusive, IsValidated: validated, DelegatorActive: true,
            DelegatedByUserId: OwnerId, ExpiresAt: expiresAt, MyProgress: progress);

    private PatchCellStylesHandler Handler()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new PatchCellStylesHandler(
            _rows, _styles, _metadata, _access, _gate, _uow, _audit, user, clock, _assignments);
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = UserId,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
