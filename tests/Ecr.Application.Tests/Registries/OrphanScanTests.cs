// tests/Ecr.Application.Tests/Registries/OrphanScanTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Ознака `IsOrphaned` (ФВ-8.13, D-98). Механізм **симетричний**: те, що
/// ставить ознаку, має її й знімати — інакше виправлення довідника не
/// розблокує `Submit`.
/// </summary>
public sealed class OrphanScanTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int Sheet = 20;
    private const int Period = 202603;
    private const int VolumeId = 11;
    private const long Row1 = 1001;
    private const long PermitEntry = 101;

    /// <summary>Кінець періоду — дата, на яку резолвиться чинність.</summary>
    private static readonly DateOnly PeriodEnd = new(2026, 3, 31);

    private static readonly DateTime Now = new(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly RegistryResolver _resolver = new();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IOrphanScanner _scanner = Substitute.For<IOrphanScanner>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public OrphanScanTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _user.CorrelationId.Returns("test");

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        var column = new ColumnDef(3, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(column, VolumeId);
        table.AddColumn(column);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = column },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = Row1 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A" });
        _cells.ReadSliceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>
              {
                  new(new CellAddress(new PeriodKey(Period), Row1, VolumeId), 3,
                      new CellValueData { ValueNumeric = 12500m }),
              });
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanSubmitAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(),
                               Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _workflow.GetOrCreateAsync(Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(new ApprovalState(Document, Sheet, Period));
        _workflow.SaveSnapshotAsync(Arg.Any<SubmissionSnapshotRecord>(), Arg.Any<CancellationToken>())
                 .Returns(1L);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Звуження_ValidTo_ставить_IsOrphaned()
    {
        // Дозвіл був чинним до кінця року; його закривають січнем — тобто
        // раніше за період документа.
        var permit = Entry(PermitEntry, from: new DateOnly(2025, 1, 1), to: new DateOnly(2026, 12, 31));
        _registries.FindEntryAsync(PermitEntry, Arg.Any<CancellationToken>()).Returns(permit);
        _registries.FindDefinitionByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(Definition());
        _scanner.RescanForEntryAsync(PermitEntry, Arg.Any<CancellationToken>()).Returns(3);

        var affected = await Handler().HandleAsync(
            PermitEntry, new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 31), CancellationToken.None);

        // Перерахунок відбувся В ТІЙ САМІЙ операції, що й зміна вікна: між
        // двома комітами існував би стан, у якому запис уже нечинний, а рядки
        // ще не позначені — і Submit у цю мить пройшов би (ФВ-8.13a).
        await _scanner.Received(1).RescanForEntryAsync(PermitEntry, Arg.Any<CancellationToken>());
        Assert.Equal(3, affected);

        // Правило, за яким сканер вирішує: посилання на запис, нечинний на
        // дату періоду, робить рядок осиротілим.
        Assert.False(_resolver.IsSelectable(permit, PeriodEnd));

        var decision = OrphanScanPlan.Plan(
            [new OrphanCandidate(Row1, PeriodState.Open, IsOrphaned: false, ReferenceIsValid: false)]);

        Assert.Equal([Row1], decision.ToFlag);
        Assert.Empty(decision.ToClear);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Осиротілий_рядок_блокує_Submit_із_ECR_SUB_4221()
    {
        _rows.GetOrphanedRowIdsAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<long> { Row1 });

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Submit().HandleAsync(Document, Sheet, Period, CancellationToken.None));

        Assert.Equal("ECR-SUB-4221", error.ErrorCode);

        // ⚠ Зріз НЕ створено: поданий аркуш без зрізу — звіт, який неможливо
        // ні звірити, ні перерахувати «як тоді».
        await _workflow.DidNotReceive().SaveSnapshotAsync(
            Arg.Any<SubmissionSnapshotRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Осиротілий_рядок_не_блокує_читання()
    {
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool> { [Row1] = true });

        var slice = await Slice().HandleAsync(
            Document, TableInstance, Profile(), "en", CancellationToken.None);

        // ⛔ Читання проходить, а ознака видима. Заборонити читання означало б
        // сховати від користувача саме той рядок, який він має виправити —
        // і лишити його з помилкою подання без жодного способу її знайти.
        var row = Assert.Single(slice.Rows);
        Assert.True(row.IsOrphaned);
        Assert.Single(row.Cells);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розширення_вікна_назад_знімає_ознаку()
    {
        var permit = Entry(PermitEntry, from: new DateOnly(2026, 6, 1), to: null);

        // Спершу запис не чинний у березні — рядок осиротілий.
        Assert.False(_resolver.IsSelectable(permit, PeriodEnd));

        // Вікно розширили назад: запис знову чинний у періоді документа.
        permit.SetValidity(new DateOnly(2025, 1, 1), null);
        Assert.True(_resolver.IsSelectable(permit, PeriodEnd));

        var decision = OrphanScanPlan.Plan(
            [new OrphanCandidate(Row1, PeriodState.Open, IsOrphaned: true, ReferenceIsValid: true)]);

        // ⚠ Ось половина механізму, без якої решта — пастка: виправлення
        // довідника не розблокувало б Submit, і користувач лишився б із
        // помилкою, причину якої вже усунуто.
        Assert.Equal([Row1], decision.ToClear);
        Assert.Empty(decision.ToFlag);

        // І симетрично: коли стан збігається з дійсністю, не пишеться нічого.
        // Зайвий UPDATE підняв би ModifiedAt і зламав оптимістичне блокування
        // чужої відкритої форми.
        var unchanged = OrphanScanPlan.Plan(
        [
            new OrphanCandidate(Row1, PeriodState.Open, IsOrphaned: true, ReferenceIsValid: false),
            new OrphanCandidate(1002, PeriodState.Open, IsOrphaned: false, ReferenceIsValid: true),
        ]);

        Assert.Equal(0, unchanged.Total);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Читання_зрізу_ознаку_не_перераховує()
    {
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool> { [Row1] = true });

        await Slice().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        // ⛔ Жодного звернення до сканера і до довідника. Темпоральна перевірка
        // на кожен рядок при кожному відкритті таблиці зжерла б бюджет 400 мс
        // цілком (D-98). Ознака ЧИТАЄТЬСЯ зі збереженого поля.
        await _scanner.DidNotReceive().ScanAllAsync(Arg.Any<CancellationToken>());
        await _scanner.DidNotReceive().RescanForEntryAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _registries.DidNotReceive().ListEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Рівно один запит за ознаками на весь зріз — не по рядку.
        await _rows.Received(1).GetOrphanFlagsAsync(
            TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нічна_перевірка_не_чіпає_закриті_періоди()
    {
        var decision = OrphanScanPlan.Plan(
        [
            new OrphanCandidate(1001, PeriodState.Open, IsOrphaned: false, ReferenceIsValid: false),
            new OrphanCandidate(1002, PeriodState.Grace, IsOrphaned: false, ReferenceIsValid: false),
            new OrphanCandidate(1003, PeriodState.Closed, IsOrphaned: false, ReferenceIsValid: false),
            new OrphanCandidate(1004, PeriodState.Closed, IsOrphaned: true, ReferenceIsValid: true),
            new OrphanCandidate(1005, PeriodState.Scheduled, IsOrphaned: false, ReferenceIsValid: false),
        ]);

        // ⛔ Закритий період не чіпається в ОБИДВА боки: ні поставити, ні
        // зняти. Його дані вже подані й погоджені — ознака нічого не
        // розблокує і нічого не заборонить, зате перепише рядок, що входить
        // у контрольну суму зрізу подання.
        Assert.Equal([1001L, 1002L], decision.ToFlag);
        Assert.Empty(decision.ToClear);

        Assert.True(OrphanScanPlan.IsScannable(PeriodState.Open));
        Assert.True(OrphanScanPlan.IsScannable(PeriodState.Grace));
        Assert.False(OrphanScanPlan.IsScannable(PeriodState.Closed));

        // Scheduled теж не чіпається: документів у ньому ще немає, і
        // прохід по ньому — робота, яка нічого не знаходить за побудовою.
        Assert.False(OrphanScanPlan.IsScannable(PeriodState.Scheduled));
    }

    private SetEntryValidityHandler Handler()
        => new(_registries, _scanner, _uow, _audit, _user, _clock);

    private GetTableSliceHandler Slice() => new(_rows, _cells, _metadata, _access);

    private SubmitSheetHandler Submit()
        => new(_cells, _rows, _workflow, _access, validation: null!, _uow, _user, _clock);

    private static RegistryDef Definition()
        => new(EcrCode.Create("PERMITS"), Text("Permits"), isTemporal: true);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(),
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
    };

    private static RegistryEntry Entry(long id, DateOnly? from, DateOnly? to)
    {
        var entry = new RegistryEntry(4, EcrCode.Create("PERMIT_A"), Text("Permit A"));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        entry.SetValidity(from, to);
        return entry;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
