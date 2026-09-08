using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Робочий процес із гранулярністю **аркуш × період** (D-38) і правилом
/// «поданий документ не редагується» (D-67).
/// </summary>
public sealed class SubmitApproveTests
{
    private const long Document = 700;
    private const int Water = 20;
    private const int Waste = 21;
    private const int Period = 202601;

    /// <summary>Єдиний екземпляр таблиці документа за цей період.</summary>
    private const long TableInstance = 500;
    private static readonly DateTime Now = new(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly Dictionary<int, ApprovalState> _sheets = [];
    private readonly List<SubmissionSnapshotRecord> _snapshots = [];

    public SubmitApproveTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);

        _sheets[Water] = new ApprovalState(Document, Water, Period);
        _sheets[Waste] = new ApprovalState(Document, Waste, Period);

        _workflow.GetOrCreateAsync(Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(call => _sheets[call.ArgAt<int>(1)]);
        _workflow.SaveSnapshotAsync(Arg.Any<SubmissionSnapshotRecord>(), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     _snapshots.Add(call.Arg<SubmissionSnapshotRecord>());
                     return (long)_snapshots.Count;
                 });
        _workflow.LockPeriodAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(OpenPeriod());

        // ⚠ Дозвіл за замовчуванням: предмет більшості тестів цього класу —
        // ПОДАННЯ й ПОГОДЖЕННЯ, а не склад документа. Тест на відсутній
        // аркуш підставляє `false` сам, окремо (`S-17`).
        _documents.HasSheetAsync(Document, Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(true);

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanSubmitAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanApproveAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());

        _rows.GetOrphanFlagsAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());

        // ⚠ Екземпляри таблиць і знімок структури: подання кличе валідацію
        // (`ФВ-5.4`, `W8`), а вона питає обидва. Порожній набір правил тут
        // навмисний — предмет цих тестів робочий процес, а не валідація;
        // тест про блокування підставляє правило сам.
        _rows.GetTableInstancesAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns([new TableInstanceRef(TableInstance, Document, TableDefId: 3, TemplateVersionId: TemplateVersion, PeriodKey: Period)]);
        _rows.GetRowIdsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = 1001L });
        _metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(Snapshot());
        // ⚠ Ключ — `TableInstanceId` (500), а не `DocumentId`: саме так
        // адресується зріз (`ICellStore.ReadSliceAsync`). Доти обробник
        // передавав сюди `documentId`, і фікстура повторювала ту саму
        // помилку — тобто перевіряла зріз, набраний із неіснуючого
        // екземпляра таблиці.
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>
              {
                  new(new CellAddress(new PeriodKey(Period), 1001, 11), 3,
                      new CellValueData { ValueNumeric = 12500m }),
              });
    }

    /// <summary>Версія шаблону, за якою живе документ цих тестів.</summary>
    private const int TemplateVersion = 2;

    /// <summary>
    /// Знімок структури: аркуш <c>Water</c> з однією таблицею й колонкою.
    /// </summary>
    /// <param name="rule">Правило валідації таблиці; <c>null</c> — без правил.</param>
    private static Ecr.Domain.Entities.Configuration.TemplateVersionSnapshot Snapshot(
        Ecr.Domain.Entities.Configuration.ValidationRule? rule = null)
    {
        var column = new Ecr.Domain.Entities.Configuration.ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, 11);

        var sheet = new Ecr.Domain.Entities.Configuration.SheetDef(
            TemplateVersion, EcrCode.Create("WATER"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, Water);

        var table = new Ecr.Domain.Entities.Configuration.TableDef(
            sheetDefId: Water, EcrCode.Create("MAIN"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 3);

        table.AddColumn(column);
        if (rule is not null)
        {
            table.AddValidationRule(rule);
        }

        sheet.AddTable(table);

        return new Ecr.Domain.Entities.Configuration.TemplateVersionSnapshot(
            TemplateVersion, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, Ecr.Domain.Entities.Configuration.ColumnDef> { [11] = column },
            RowsByKey: new Dictionary<(int, string), Ecr.Domain.Entities.Configuration.RowDef>());
    }

    private static AccessProfile Profile()
        => new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve)
            .Permission(ReopenDocumentHandler.Permission)
            .Build();

    private static Period OpenPeriod(PeriodState state = PeriodState.Open)
    {
        var project = ProjectBuilder.Project();
        var periods = PeriodCalendar.Build(
            project, ProjectBuilder.Policy(), ProjectBuilder.Zone(), existing: []);

        if (state != PeriodState.Scheduled)
        {
            periods[0].TransitionTo(PeriodState.Open, Now);
        }

        if (state is PeriodState.Grace or PeriodState.Closed)
        {
            periods[0].TransitionTo(PeriodState.Grace, Now);
        }

        if (state == PeriodState.Closed)
        {
            periods[0].TransitionTo(PeriodState.Closed, Now);
        }

        return periods[0];
    }

    /// <summary>
    /// Черга сповіщень для перевірок робочого процесу.
    /// </summary>
    /// <remarks>
    /// ⚠ Підставна, але НЕ порожня: подання й затвердження зобов'язані класти
    /// подію в чергу тим самим комітом (`A7-31`), і перевірка цього має бути
    /// можливою тут, а не лише в базі.
    /// </remarks>
    private readonly INotificationOutbox _outbox = Substitute.For<INotificationOutbox>();

    /// <summary>Побудовник зрізів звітності — предмет `H-23b`.</summary>
    private readonly IReportSnapshotBuilder _reportSnapshots = Substitute.For<IReportSnapshotBuilder>();

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();

    /// <summary>Проведення стану аркушів у зрізи звітності.</summary>
    private Ecr.Application.Reporting.ReportSnapshotSync Reports()
        => new(_reportSnapshots, _documents);

    private SubmitSheetHandler Submit()
        => new(_cells, _rows, _workflow, _documents, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               Reports(), _uow, _user, _clock);

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private ApproveSheetHandler Approve() => new(_workflow, _access, Reports(), _uow, _user, _clock, _audit);

    private ReopenDocumentHandler Reopen() => new(_workflow, _access, _uow, _user, _clock);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Подання_аркуша_якого_немає_в_складі_документа_відхиляється()
    {
        // ⛔ Директива №09 §6.4, `S-17`: до цієї перевірки `POST …/submit` на
        // ДОВІЛЬНИЙ `sheetDefId` — навіть той, якого в документі ніколи не
        // було, — проходив кодом `204`. `IWorkflowStore.GetOrCreateAsync`
        // створює новий рядок стану для будь-якого ідентифікатора, а
        // `CanSubmitAsync` перевіряє права, не існування.
        const int unknownSheet = 999;
        _documents.HasSheetAsync(Document, unknownSheet, Arg.Any<CancellationToken>()).Returns(false);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Submit().HandleAsync(Document, unknownSheet, Period, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);

        // Ані рядка стану, ані зрізу: відмова має спинити подання ДО того, як
        // з'явиться будь-який слід неіснуючого аркуша.
        await _workflow.DidNotReceive().GetOrCreateAsync(
            Arg.Any<long>(), unknownSheet, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Подання_НЕ_породжує_сповіщення()
    {
        // ⛔ `D-119`, рішення замовника: події черги — лише ЗБОЇ. Лист про
        // кожне подання це шум, а шум вимикають разом із корисними листами.
        //
        // ⚠ Тест лишається саме тому, що раніше тут стояла протилежна
        // перевірка: без нього постановку події легко повернути «як було».
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        await _outbox.DidNotReceive().EnqueueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public async Task Подання_морозить_зріз_звітності_за_період()
    {
        // ⛔ Регресія: подання знову перестає доходити до зрізу. Ніщо не
        // падає — просто `ER-C-11` («подане не перераховується») знову
        // тримається на позначці, якої не ставить ніхто, і зріз за поданим
        // звітом одного дня перебудується з іншими числами (ФВ-9.17).
        _documents.FindProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(3);

        _reportSnapshots.ListAsync(3, Period, Arg.Any<CancellationToken>())
            .Returns([
                new ReportSnapshotSummary(
                    55, ReportVersionId: 1, ProjectId: 3, PeriodKey: Period,
                    Status: nameof(SnapshotStatus.Draft), IsCurrent: true,
                    RowCount: 10, ContentHash: null, BuiltAt: Now),
            ]);

        _reportSnapshots.RefreshStatusAsync(55, Arg.Any<CancellationToken>())
            .Returns(SnapshotStatus.Submitted);

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        await _reportSnapshots.Received(1).MarkSubmittedAsync(55, 9, Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Подання_аркуша_за_період_не_зачіпає_інші_аркуші()
    {
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        // 24 аркуші рідко готові одночасно, і чекати найповільніший не має
        // сенсу (D-38).
        Assert.Equal(DocumentStatus.Submitted, _sheets[Water].Status);
        Assert.Equal(DocumentStatus.Draft, _sheets[Waste].Status);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Подання_з_незакритими_помилками_валідації_відхиляється()
    {
        _access.CanSubmitAsync(Arg.Any<AccessProfile>(), Document, Water, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Deny(EditDenyReason.BusinessRule, "Є незакриті помилки валідації."));

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Submit().HandleAsync(Document, Water, Period, CancellationToken.None));

        // ⚠ При поданні блокує БУДЬ-ЯКИЙ Error будь-якого рівня (ФВ-5.19), на
        // відміну від запису, де блокує лише комірковий (D-90): подана форма
        // йде назовні цілком, і рядковий Error у ній — це неправильний звіт.
        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal(DocumentStatus.Draft, _sheets[Water].Status);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Подання_створює_іммутабельний_зріз_із_версіями_і_режимами()
    {
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        var snapshot = Assert.Single(_snapshots);

        // Зріз фіксує не лише значення, а й режими, за якими їх рахували: без
        // них «перерахувати як тоді» неможливо, і поданий звіт стає незвіряним.
        Assert.Equal(Document, snapshot.DocumentId);
        Assert.Equal(Water, snapshot.SheetDefId);

        // ⛔ Версія шаблону — СПРАВЖНЯ. Тут стояв нуль, і сам тест його не
        // перевіряв: зріз, створений заради відповіді «за якою структурою це
        // подавали», не ніс структури взагалі (директива №09 `W8` п.5).
        Assert.Equal(TemplateVersion, snapshot.TemplateVersionId);
        Assert.Equal((byte)CalendarMode.Actual, snapshot.CalendarMode);
        Assert.Equal(Now, snapshot.SubmittedAt);
        Assert.Contains("12500", snapshot.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(64, snapshot.ContentHash.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.4")]
    public async Task Блокувальна_помилка_валідації_відхиляє_подання()
    {
        // Правило рівня РЯДКА: `[Volume] <= 100`, а в комірці 12500.
        WithRule("[Volume] <= 100");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Submit().HandleAsync(Document, Water, Period, CancellationToken.None));

        // ⛔ Це те, чого подання не робило зовсім: `ValidationEngine` був
        // упорснутий і не читаний, аркуш із блокувальними помилками подавався
        // кодом 204 і йшов далі по маршруту погодження як придатний
        // (директива №09 `W8` п.5, `S-28`).
        Assert.Equal("ECR-SUB-4221", error.ErrorCode);

        // ⚠ І НІЧОГО не сталося: ні зрізу, ні зміни стану. Подання, яке
        // відмовило, але встигло заморозити зріз, лишило б документ у стані,
        // якого не було ні до, ні після.
        Assert.Empty(_snapshots);
        Assert.Equal(DocumentStatus.Draft, _sheets[Water].Status);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.4")]
    public async Task Попередження_валідації_подання_не_блокує()
    {
        // ⚠ Блокує лише `Error`. Попередження — привід подивитися, а не
        // причина не подати звіт у строк (R-B3, D-90).
        WithRule("[Volume] <= 100", ValidationSeverity.Warning);

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        Assert.Equal(DocumentStatus.Submitted, _sheets[Water].Status);
    }

    /// <summary>Підставляє знімок структури з одним правилом валідації рядка.</summary>
    private void WithRule(string expression, ValidationSeverity severity = ValidationSeverity.Error)
        => _metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            Snapshot(new Ecr.Domain.Entities.Configuration.ValidationRule(
                tableDefId: 3, EcrCode.Create("CAP"), severity, scope: 1, expression,
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume is over the cap" }))));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поданий_аркуш_не_редагується_навіть_у_стані_Grace()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage).Build();

        var decision = EditRules.CanEdit(
            profile, AccessBuilder.Cell(period: PeriodState.Grace, sheet: DocumentStatus.Submitted));

        // ⚠ Grace дає час на правки НЕПОДАНИХ документів, а не право змінити
        // подану форму (D-67). Плутанина тут означала б тиху зміну чисел після
        // того, як звіт пішов.
        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.DocumentSubmitted, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Reopen_документа_повертає_аркуш_у_Draft_із_обовязковою_причиною()
    {
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        await Assert.ThrowsAsync<DomainException>(
            () => Reopen().HandleAsync(Document, Water, Period, "  ", CancellationToken.None));

        await Reopen().HandleAsync(Document, Water, Period, "помилка в рядку 7001003", CancellationToken.None);

        Assert.Equal(DocumentStatus.Draft, _sheets[Water].Status);
        Assert.Equal("помилка в рядку 7001003", _sheets[Water].ReopenReason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Reopen_документа_при_закритому_періоді_відхиляється_ECR_PRD_4223()
    {
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);
        _workflow.LockPeriodAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(OpenPeriod(PeriodState.Closed));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Reopen().HandleAsync(Document, Water, Period, "причина", CancellationToken.None));

        // Спершу Reopen ПЕРІОДУ, потім аркуша (ФВ-5.20a): інакше правка пішла б
        // у період, який уже віддали назовні.
        Assert.Equal("ECR-PRD-4223", error.ErrorCode);
        Assert.Equal(DocumentStatus.Submitted, _sheets[Water].Status);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_Reopen_зміни_позначаються_як_пізні()
    {
        // Reopen переводить період у Grace, а Grace — це і є вікно пізніх
        // правок: сам факт, що документ відкрили після закриття, має лишитися
        // в аудиті кожної зміни, а не тільки в журналі відкриття.
        var period = OpenPeriod(PeriodState.Closed);
        period.Reopen(Now.AddDays(1), "уточнення", Now);

        Assert.Equal(PeriodState.Grace, period.State);
        Assert.True(period.IsLateEditWindow);
        Assert.True(period.AllowsEditing);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Старий_поданий_зріз_лишається_після_повторного_подання()
    {
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);
        await Reopen().HandleAsync(Document, Water, Period, "правка", CancellationToken.None);

        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>
              {
                  new(new CellAddress(new PeriodKey(Period), 1001, 11), 3,
                      new CellValueData { ValueNumeric = 999m }),
              });

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        // ⚠ Зрізи НАКОПИЧУЮТЬСЯ, а не перезаписуються: старий лишається
        // Submitted назавжди і не перераховується ніколи (ФВ-9.17). Інакше
        // після правки неможливо було б показати, що саме подавали раніше.
        Assert.Equal(2, _snapshots.Count);
        Assert.Contains("12500", _snapshots[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("999", _snapshots[1].PayloadJson, StringComparison.Ordinal);
        Assert.NotEqual(_snapshots[0].ContentHash, _snapshots[1].ContentHash);
    }
}
