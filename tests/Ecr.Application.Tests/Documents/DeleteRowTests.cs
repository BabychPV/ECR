using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// D3-A: видалення динамічного рядка — правила, порядок і нерозкриття
/// (<see cref="DeleteRowHandler"/>; поведінку на справжній базі тримає
/// <c>Ecr.Infrastructure.Tests/Persistence/DeleteRowTests</c>).
/// </summary>
public sealed class DeleteRowTests
{
    private const long TableInstance = 500;
    private const long RowId = 9001;
    private const int Period = 202601;
    private const string Key = "r-dyn";

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId(object entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private void Arrange(
        TableRowMode mode = TableRowMode.Dynamic, int? rowDefId = null, bool sourceManaged = false, bool rowExists = true)
    {
        var sheet = new SheetDef(templateVersionId: 2, EcrCode.Create("Water"), Text("Water"), 1);
        SetId(sheet, 1);
        var table = new TableDef(sheetDefId: 1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, mode);
        SetId(table, 3);
        foreach (var (id, code) in new[] { (31, "A"), (32, "B") })
        {
            var column = new ColumnDef(3, EcrCode.Create(code), Text(code), id, CellDataType.Decimal);
            SetId(column, id);
            table.AddColumn(column);
        }

        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>()));
        _rows.FindLiveRowAsync(TableInstance, Arg.Any<PeriodKey>(), Key, Arg.Any<CancellationToken>())
             .Returns(rowExists ? new LiveRowRef(RowId, 3, rowDefId, sourceManaged) : null);
        _rows.SoftDeleteRowAsync(RowId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(true);

        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>
              {
                  [new CellAddress(new PeriodKey(Period), RowId, 31)] = new() { ValueNumeric = 12.5m },
              });

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        Allow();
        AllowCells();

        _jobs.EnlistsInCallerTransaction.Returns(true);
        _jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
             .Returns("job-7");

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    private void Allow()
        => _access.CanCreateRowsAsync(
                      Arg.Any<AccessProfile>(), TableInstance,
                      Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                  .Returns(call => Rows(call.ArgAt<IReadOnlyCollection<string>>(2), EditDecision.Allow()));

    /// <summary>Рішення на значення комірок рядка (P1-2): за замовчуванням усе дозволено.</summary>
    private void AllowCells()
        => _access.CanClearCellsAsync(
                      Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                      Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                  .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call
                      .ArgAt<IReadOnlyCollection<CellAddress>>(3)
                      .ToDictionary(a => a, _ => EditDecision.Allow()));

    private void DenyCells(EditDenyReason reason)
        => _access.CanClearCellsAsync(
                      Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                      Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                  .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call
                      .ArgAt<IReadOnlyCollection<CellAddress>>(3)
                      .ToDictionary(a => a, _ => EditDecision.Deny(reason)));

    private void Deny(EditDenyReason reason)
        => _access.CanCreateRowsAsync(
                      Arg.Any<AccessProfile>(), TableInstance,
                      Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                  .Returns(call => Rows(call.ArgAt<IReadOnlyCollection<string>>(2), EditDecision.Deny(reason)));

    private static Dictionary<string, NewRowAccess> Rows(IReadOnlyCollection<string> keys, EditDecision row)
        => keys.ToDictionary(k => k, _ => new NewRowAccess(row, new Dictionary<int, EditDecision>()), StringComparer.Ordinal);

    private DeleteRowHandler Handler()
        => new(_rows, _documents, _metadata, _access, _cells, _audit, _jobs, _uow, _clock, _gate);

    private Task<DeleteRowResult> Run(string key = Key, long documentId = 700)
        => Handler().HandleAsync(documentId, TableInstance, key, Profile(), CancellationToken.None);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видаляє_динамічний_рядок_слід_ДО_видалення_і_перерахунок_по_всіх_колонках()
    {
        Arrange();

        var result = await Run();

        Assert.Equal("job-7", result.RecalculationJobId);

        // Порядок: слід у аудиті → м'яке видалення → дотик документа → збереження.
        Received.InOrder(() =>
        {
            _ = _audit.WriteStructureChangeAsync(
                Arg.Is<StructureChangeRecord>(r =>
                    r.EntityType == "doc.TableRow" && r.Operation == "RowDeleted" && r.ChangedByUserId == 9
                    && r.NewJson == null && r.OldJson!.Contains(Key, StringComparison.Ordinal)
                    // P2-1: лише склад, значення (12.5) у StructureChange не потрапляє.
                    && !r.OldJson.Contains("12.5", StringComparison.Ordinal)
                    && r.OldJson.Contains("columnDefId", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
            _ = _rows.SoftDeleteRowAsync(RowId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
            _ = _documents.TouchAsync(700, 9, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            _ = _uow.SaveChangesAsync(Arg.Any<CancellationToken>());
        });

        var payload = System.Text.Json.JsonSerializer.Serialize(
            _jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
                 .GetArguments()[1]);
        Assert.Contains($"\"RowId\":{RowId},\"ColumnDefId\":31", payload, StringComparison.Ordinal);
        Assert.Contains($"\"RowId\":{RowId},\"ColumnDefId\":32", payload, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Рядок_із_опису_шаблону_не_видаляється()
    {
        Arrange(TableRowMode.Mixed, rowDefId: 55);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Run());

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-ROW-0409.templateRowNotDeletable", ex.Details!["messageKey"]);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteStructureChangeAsync(Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Рядок_Fixed_таблиці_не_видаляється_навіть_без_RowDef()
    {
        Arrange(TableRowMode.Fixed);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Run());

        Assert.Equal("err.ECR-ROW-0409.templateRowNotDeletable", ex.Details!["messageKey"]);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Рядок_який_веде_синхронізація_подій_не_видаляється()
    {
        // Ручне видалення такого рядка зламало б синхронізацію: ключ рядка лишається зайнятим.
        Arrange(sourceManaged: true);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Run());

        Assert.Equal("err.ECR-ROW-0409.sourceRowNotDeletable", ex.Details!["messageKey"]);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Немає_рядка_або_видалений_це_404_без_подробиць_про_ключ()
    {
        Arrange(rowExists: false);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Run());

        Assert.Equal("ECR-ROW-0404", ex.ErrorCode);
        Assert.Equal("err.ECR-ROW-0404.rowNotFound", ex.Details!["messageKey"]);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.Single(ex.Details);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Ключ_що_не_може_бути_ключем_рядка_теж_404_а_не_400()
    {
        Arrange();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Run(key: "bad key!"));

        Assert.Equal("ECR-ROW-0404", ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Відмова_в_правах_не_відкриває_транзакції_й_не_торкається_рядка()
    {
        Arrange();
        Deny(EditDenyReason.PeriodClosed);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Run());

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
Assert.Equal("err.ECR-ACCS-0403.deleteRowDenied", ex.Details!["messageKey"]);
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), ex.Details["reason"]);
        await _uow.DidNotReceive().ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _rows.DidNotReceive().FindLiveRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted, nameof(EditDenyReason.DocumentSubmitted))]
    [InlineData(DocumentStatus.Approved, nameof(EditDenyReason.DocumentApproved))]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Поданий_або_затверджений_аркуш_рядок_не_видаляє(DocumentStatus status, string reason)
    {
        Arrange();
        _gate.EnterEditAsync(700, 1, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(status);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Run());

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Equal(reason, ex.Details!["reason"]);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteStructureChangeAsync(Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.Rejected)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Чернетка_і_відхилений_аркуш_рядок_видаляють(DocumentStatus status)
    {
        Arrange();
        _gate.EnterEditAsync(700, 1, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(status);

        await Run();

        await _rows.Received(1).SoftDeleteRowAsync(RowId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Невидимий_документ_це_404_до_будь_якої_відмови_про_таблицю_чи_рядок()
    {
        Arrange();
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Run());

        Assert.Equal("ECR-DOC-0404", ex.ErrorCode);
        await _rows.DidNotReceive().FindLiveRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Екземпляр_таблиці_чужого_документа_відхиляється()
    {
        Arrange();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Run(documentId: 701));

        Assert.Equal("ECR-DOC-0404", ex.ErrorCode);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EditDenyReason.NoGrant)]
    [InlineData(EditDenyReason.InsufficientGrantLevel)]
    [InlineData(EditDenyReason.OutOfAccessWindow)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "D3-A P1-2")]
    public async Task Заборона_на_значення_колонки_блокує_видалення_без_назви_колонки(EditDenyReason reason)
    {
        // ⛔ `CanCreateRowsAsync` ≈ право на рядок БЕЗ колонкових грантів і правил періоду: без перевірки
        // значень користувач із Write на таблицю стер би значення колонки, на яку має лише Read.
        Arrange();
        DenyCells(reason);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Run());

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
Assert.Equal(
            reason == EditDenyReason.OutOfAccessWindow
                ? "err.ECR-ACCS-0403.deleteRowOutOfWindow"
                : "err.ECR-ACCS-0403.deleteRowValuesDenied",
            ex.Details!["messageKey"]);
        // Без {reason} в основному тексті; S-4: reason ОДИН для всіх заборон на комірки — не розрізняє «прихована колонка» і «відкрита».
        Assert.Equal(nameof(EditDenyReason.BusinessRule), ex.Details["reason"]);
        Assert.DoesNotContain("31", ex.Message, StringComparison.Ordinal);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteStructureChangeAsync(Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "D3-A S-3")]
    public async Task Дозвіл_за_підтвердженням_або_з_позначкою_поза_вікном_не_дає_видалити(bool marked)
    {
        // Каналу підтвердження для DELETE немає, а PatchCellsHandler такі комірки без нього теж не пише.
        Arrange();
        _access.CanClearCellsAsync(
                   Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call
                   .ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(
                       a => a,
                       _ => marked ? EditDecision.AllowMarked("поза вікном") : EditDecision.AllowWithConfirmation("поза вікном")));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Run());

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deleteRowOutOfWindow", ex.Details!["messageKey"]);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "D3-A S-3")]
    public async Task Рішення_на_комірку_не_прийшло_це_відмова()
    {
        Arrange();
        _access.CanClearCellsAsync(
                   Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Run());
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "D3-A P1-3")]
    public async Task Рядок_з_машинним_походженням_значень_не_видаляється()
    {
        Arrange();
        _rows.HasMachineOriginAsync(700, Arg.Any<PeriodKey>(), RowId, Arg.Any<CancellationToken>()).Returns(true);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Run());

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-ROW-0409.sourceRowNotDeletable", ex.Details!["messageKey"]);
        await _rows.DidNotReceive().SoftDeleteRowAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "D3-A S-4")]
    public async Task Відсутній_чужий_і_недоступний_екземпляр_відповідають_однаково_байт_у_байт()
    {
        // Оракул існування: за різницею тіла не можна вгадувати чужі `TableInstanceId`.
        Arrange();
        // Екземпляр належить ІНШОМУ документові (999); запитано 700 — як і в двох інших випадках.
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 999, 3, 2, Period));
        var foreign = await Assert.ThrowsAsync<NotFoundException>(() => Run());

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns<Task<TableInstanceRef>>(_ => throw new NotFoundException(
                 "ECR-DOC-0404", "Екземпляр таблиці не знайдено.",
                 new Dictionary<string, object?> { ["messageKey"] = "err.ECR-DOC-0404.tableInstance" }));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Run());

        Arrange();
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Deny(EditDenyReason.NoGrant));
        var hidden = await Assert.ThrowsAsync<NotFoundException>(() => Run());

        foreach (var other in new[] { missing, hidden })
        {
            Assert.Equal(foreign.ErrorCode, other.ErrorCode);
            Assert.Equal(foreign.Message, other.Message);
            Assert.Equal(
                foreign.Details!.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"),
                other.Details!.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        }

        Assert.Equal("err.ECR-DOC-0404.document", foreign.Details!["messageKey"]);
    }
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "D3-A P3")]
    public async Task Очікування_блокувань_обмежене_до_будь_яких_блокувань_аркуша()
    {
        Arrange();

        await Run();

        Received.InOrder(() =>
        {
            _ = _cells.LimitLockWaitAsync(Arg.Any<CancellationToken>());
            _ = _gate.EnterSubmitAsync(700, 1, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        });
    }
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Черга_в_базі_ставить_перерахунок_усередині_транзакції_Quartz_після_коміту()
    {
        Arrange();
        _jobs.EnlistsInCallerTransaction.Returns(false);

        var result = await Run();

        Assert.Equal("job-7", result.RecalculationJobId);
        Received.InOrder(() =>
        {
            _ = _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
            _ = _jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        });
    }
}
