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
    private readonly FakePermitGroupResolver _groups = new();

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
            // Рішення 25.10: вибір запису потребує Registry.View - звичайний користувач фікстури його має.
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
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
               Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>(), groups: _groups);

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

    // ⛔ AN-93 (аудит R11): запис чужого довідника — та сама відповідь, що й неіснуючий (`missingEntry`), а не окрема
    // `foreignRegistry`: різниця казала б, чи існує запис у довіднику, якого читач не бачить (оракул існування).
    // Мутація: повернути окремий арм `foreignRegistry` у `CheckLookupStandings` — ключ відмови інший, червоніє.
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "AN-93")]
    public async Task Запис_чужого_довідника_відхиляється_так_само_як_неіснуючий()
    {
        Entry(registryDefId: OtherRegistryDefId);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal("ECR-CELL-4223", error.ErrorCode);
        Assert.Equal("err.ECR-CELL-4223.missingEntry", error.Details!["messageKey"]);
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
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

    // ── PRMG: ціль колонки — ГРУПА типів дозволів (колонка тримає id прихованого реєстру групи = ColumnRegistryDefId).
    // Запис належить цілі за членством, а не за збігом реєстру. Мутація: прибрати LookupTargetMatcher.Belongs із
    // CheckLookupStandings (повернути пряме порівняння) — перший же тест членства червоніє.

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_запис_реєстру_члена_приймається()
    {
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId]);
        Entry(registryDefId: OtherRegistryDefId);

        await AssertWritten();
        Assert.Equal(1, _groups.Calls);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_запис_реєстру_не_члена_відхиляється_як_немає_запису()
    {
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId]);
        Entry(registryDefId: 3);

        await AssertRejected("err.ECR-CELL-4223.missingEntry");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_тип_вийшов_зі_складу_новий_вибір_відхиляється()
    {
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId]);
        Entry(registryDefId: OtherRegistryDefId);
        await AssertWritten();

        // Знімок оновлено: тип OtherRegistryDefId більше не у складі.
        _groups.WithGroup(ColumnRegistryDefId, [3]);
        _cells.ClearReceivedCalls();

        await AssertRejected("err.ECR-CELL-4223.missingEntry");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_тип_вийшов_зі_складу_незмінене_значення_проходить()
    {
        _groups.WithGroup(ColumnRegistryDefId, [3]);
        Entry(registryDefId: OtherRegistryDefId);
        StoredValue(EntryId);

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_деактивована_новий_вибір_відхиляється()
    {
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId], isActive: false);
        Entry(registryDefId: OtherRegistryDefId);

        await AssertRejected("err.ECR-CELL-4223.missingEntry");
    }

    /// <summary>S18: реєстр запису під Deny — та сама відповідь «немає запису», а не окрема (оракул існування).</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_реєстр_члена_під_Deny_відхиляється_як_немає_запису()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p2", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string> { "Registry:2" }, RoleIds = new HashSet<int>(),
        });
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId]);
        Entry(registryDefId: OtherRegistryDefId);

        await AssertRejected("err.ECR-CELL-4223.missingEntry");

        // ПОВНИЙ details (код, ключ, кількість, перелік комірок) побайтно збігається з відповіддю на запис, якого немає.
        var denied = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding>());
        var absent = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal(absent.ErrorCode, denied.ErrorCode);
        Assert.Equal(absent.Message, denied.Message);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(absent.Details),
            System.Text.Json.JsonSerializer.Serialize(denied.Details));
    }

    private void DenyRegistry(params int[] registryIds)
        => _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p-deny", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" }, Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(registryIds.Select(id => "Registry:" + id)), RoleIds = new HashSet<int>(),
        });

    /// <summary>
    /// P2 (рішення людини 23.10: «Deny блокує і вибір запису»): Deny на ПРЯМУ ціль колонки забороняє вибір її запису -
    /// та сама відповідь, що й для неіснуючого запису (без оракула існування id).
    /// Мутація: прибрати перевірку Deny на прямому збігу в <c>LookupTargetMatcher.Belongs</c> - тест червоніє (запис проходить).
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG-DENY")]
    public async Task Пряма_ціль_під_Deny_відхиляється_як_немає_запису_побайтно()
    {
        DenyRegistry(ColumnRegistryDefId);
        Entry(registryDefId: ColumnRegistryDefId);

        await AssertRejected("err.ECR-CELL-4223.missingEntry");

        var denied = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding>());
        var absent = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal(absent.ErrorCode, denied.ErrorCode);
        Assert.Equal(absent.Message, denied.Message);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(absent.Details),
            System.Text.Json.JsonSerializer.Serialize(denied.Details));
    }

    /// <summary>
    /// S18 (час, P3-1): відмова за Deny/чужим довідником настає ДО читання меж періоду - як і «запису немає» - тож
    /// відповідь не коштує ще одного запиту. Мутація: повернути <c>FindPeriodBoundsAsync</c> перед <c>CheckLookupsBelong</c> - червоніє.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG-DENY")]
    public async Task Пряма_ціль_під_Deny_відмова_до_читання_меж_періоду()
    {
        DenyRegistry(ColumnRegistryDefId);
        Entry(registryDefId: ColumnRegistryDefId);
        _periods.ClearReceivedCalls();

        await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        await _periods.DidNotReceiveWithAnyArgs().FindPeriodBoundsAsync(default, default, default);
    }

    /// <summary>Deny на видалений/нечинний запис прямої цілі не розкриває причину: теж `missingEntry`.</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG-DENY")]
    public async Task Пряма_ціль_під_Deny_видалений_запис_теж_missingEntry()
    {
        DenyRegistry(ColumnRegistryDefId);
        Entry(registryDefId: ColumnRegistryDefId, isDeleted: true);

        await AssertRejected("err.ECR-CELL-4223.missingEntry");
    }

    /// <summary>Deny на ІНШИЙ довідник вибору запису цієї цілі не заважає.</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG-DENY")]
    public async Task Пряма_ціль_Deny_на_інший_довідник_запис_приймається()
    {
        DenyRegistry(OtherRegistryDefId);
        Entry(registryDefId: ColumnRegistryDefId);

        await AssertWritten();
    }

    /// <summary>Не-член і неактивна група - теж повністю однаковий details з «запису немає» (членство не розкривається).</summary>
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Група_відмова_не_розкриває_членство_details_збігається_з_неіснуючим_записом(bool inactiveGroup)
    {
        _groups.WithGroup(ColumnRegistryDefId, inactiveGroup ? [OtherRegistryDefId] : [3], isActive: !inactiveGroup);
        Entry(registryDefId: OtherRegistryDefId);
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding>());
        var absent = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal(absent.Message, refused.Message);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(absent.Details),
            System.Text.Json.JsonSerializer.Serialize(refused.Details));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Група_прийнятий_запис_все_одно_проходить_решту_причин_видалений_відхиляється()
    {
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId]);
        Entry(registryDefId: OtherRegistryDefId, isActive: false, isDeleted: true);

        await AssertRejected("err.ECR-CELL-4223.deletedEntry");
    }

    /// <summary>Land / звичайна ціль: реєстр збігся — резолвер не кличеться взагалі (ні Accepts, ні оновлення знімка).</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "PRMG")]
    public async Task Збіг_реєстру_не_кличе_резолвер_груп()
    {
        _groups.ThrowOnUse = true;
        Entry();

        await AssertWritten();

        Assert.Equal(0, _groups.Calls);
        Assert.Equal(0, _groups.EnsureFreshCalls);
    }

    // ── Решення людини 25.10 («Так, вимагати»): вибір запису Lookup у комірці потребує Registry.View (глобально, у
    // проєкті документа чи грант Read на реєстр); без нього - та сама відповідь, що й для неіснуючого запису.
    // Мутація: прибрати перевірку View в LookupTargetMatcher.Belongs - червоніють тести «без View».

    private const int DocumentProjectId = 10;

    private void ProfileIs(
        string[]? globalPermissions = null,
        string[]? scopedPermissions = null,
        int scopedProject = DocumentProjectId,
        Dictionary<string, GrantLevel>? grants = null,
        string[]? denies = null,
        bool integration = false)
    {
        _access.DocumentProjectIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(DocumentProjectId);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "view-case", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(globalPermissions ?? []),
            Grants = grants ?? new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(denies ?? []), RoleIds = new HashSet<int>(),
            IsIntegrationWriter = integration,
            Scoped = scopedPermissions is null
                ? new Dictionary<int, ScopedProjectAccess>()
                : new Dictionary<int, ScopedProjectAccess>
                {
                    [scopedProject] = new(
                        new Dictionary<string, GrantLevel>(), new HashSet<string>(), new HashSet<int>(),
                        new HashSet<string>(scopedPermissions)) { RegistryView = scopedPermissions.Contains("Registry.View") },
                },
        });
    }

    /// <summary>Побайтно та сама відмова, що й для запису, якого немає.</summary>
    private async Task AssertSameAsAbsent()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());
        Assert.Equal("ECR-CELL-4223", refused.ErrorCode);
        Assert.Equal("err.ECR-CELL-4223.missingEntry", refused.Details!["messageKey"]);

        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding>());
        var absent = await Assert.ThrowsAsync<BusinessRuleException>(() => Write());

        Assert.Equal(absent.ErrorCode, refused.ErrorCode);
        Assert.Equal(absent.Message, refused.Message);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(absent.Details),
            System.Text.Json.JsonSerializer.Serialize(refused.Details));
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    [InlineData("none")]
    [InlineData("otherPermission")]
    [InlineData("scopedOtherProject")]
    [InlineData("readGrantOnOtherRegistry")]
    public async Task Без_Registry_View_існуючий_запис_відхиляється_побайтно_як_неіснуючий(string kind)
    {
        switch (kind)
        {
            case "otherPermission":
                ProfileIs(globalPermissions: ["Document.View"]);
                break;
            case "scopedOtherProject":
                ProfileIs(scopedPermissions: ["Registry.View"], scopedProject: DocumentProjectId + 1);
                break;
            case "readGrantOnOtherRegistry":
                ProfileIs(grants: new Dictionary<string, GrantLevel> { ["Registry:" + OtherRegistryDefId] = GrantLevel.Read });
                break;
            default:
                ProfileIs();
                break;
        }

        Entry();

        await AssertSameAsAbsent();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task З_глобальним_Registry_View_запис_приймається()
    {
        ProfileIs(globalPermissions: ["Registry.View"]);
        Entry();

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task Scoped_оператор_з_View_на_проєкт_документа_запис_приймається()
    {
        ProfileIs(scopedPermissions: ["Registry.View"]);
        Entry();

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task Грант_Read_на_реєстр_колонки_без_View_запис_приймається()
    {
        ProfileIs(grants: new Dictionary<string, GrantLevel> { ["Registry:" + ColumnRegistryDefId] = GrantLevel.Read });
        Entry();

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task View_і_Deny_на_реєстр_відхиляється_як_немає_запису()
    {
        ProfileIs(globalPermissions: ["Registry.View"], denies: ["Registry:" + ColumnRegistryDefId]);
        Entry();

        await AssertSameAsAbsent();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task Збір_IsIntegrationWriter_без_View_запис_приймається()
    {
        ProfileIs(integration: true);
        Entry();

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task Група_реєстр_члена_без_View_відхиляється_а_з_View_приймається()
    {
        _groups.WithGroup(ColumnRegistryDefId, [OtherRegistryDefId]);
        Entry(registryDefId: OtherRegistryDefId);

        ProfileIs();
        await AssertSameAsAbsent();

        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding> { new(EntryId, OtherRegistryDefId, true, false, null, null) });
        ProfileIs(globalPermissions: ["Registry.View"]);
        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "REG-VIEW")]
    public async Task Незмінене_значення_без_View_не_перевіряється_повторно()
    {
        // Те саме значення, що вже стоїть, - не новий вибір (C7): повтор без View не ламає збереження інших комірок.
        ProfileIs();
        Entry();
        StoredValue(EntryId);

        await AssertWritten();
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "C7")]
    public async Task Неіснуючий_запис_як_і_раніше_missingEntry()
    {
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding>());

        await AssertRejected("err.ECR-CELL-4223.missingEntry");
    }
}
