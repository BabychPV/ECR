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
using Ecr.Expressions.Evaluation;
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

        // ⛔ Той самий клас дефекту, що Q-243/Q-244, тільки в ТЕСТІ:
        // `ReopenDocumentHandler` тепер виконує блокування періоду, перевірку
        // стану й запис одним замиканням через
        // `IUnitOfWork.ExecuteInTransactionAsync` (аудит 2026-09-16, §6.1).
        // Без цього налаштування NSubstitute повертає typed-default
        // (`Task.CompletedTask`) і НІКОЛИ не викликає передане замикання —
        // тести Reopen мовчки перестали б щось доводити. Тут — виклик
        // замикання НАПРАВДУ, тим самим `ct`, що йому передали.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

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
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanSubmitAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanApproveAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanReopenAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
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
    /// <param name="requiredColumn">
    /// Колонка <c>Volume</c> обов'язкова (<c>ColumnDef.IsRequired</c>) — предмет
    /// перевірки «рядок, чиєї обов'язкової клітинки НІКОЛИ не торкались».
    /// </param>
    private static Ecr.Domain.Entities.Configuration.TemplateVersionSnapshot Snapshot(
        Ecr.Domain.Entities.Configuration.ValidationRule? rule = null, bool requiredColumn = false)
    {
        var column = new Ecr.Domain.Entities.Configuration.ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, 11);
        if (requiredColumn)
        {
            column.SetRequired(true);
        }

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

    /// <summary>Шапка документа — тести цього файлу її не читають.</summary>
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    /// <summary>Проведення стану аркушів у зрізи звітності.</summary>
    private Ecr.Application.Reporting.ReportSnapshotSync Reports()
        => new(_reportSnapshots, _documents);

    private SubmitSheetHandler Submit()
        => new(_cells, _rows, _workflow, _documents, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _headers,
               Reports(), _uow, _user, _clock, Substitute.For<ISheetEditGate>(), NSubstitute.Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>());

    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        // ⚠ Порожня шапка за замовчуванням: більшість тестів цього файлу її не
        // читає, а SnapshotPayloadAsync (ФВ-9.4) кличе GetValuesAsync БЕЗУМОВНО
        // на кожне подання — без цього стабу NSubstitute повернув би `null`
        // замість словника, і `SnapshotPayloadAsync` падав би з NRE на
        // `.Where(...)` у кожному тесті цього файлу, не лише в тих, що про шапку.
        store.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData>());
        return store;
    }

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

        _reportSnapshots.ListAsync(3, Period, null, Arg.Any<CancellationToken>())
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

        // ⛔ `NumericMode`/`CalendarMode` — `null`, НЕ підставні `Legacy`/
        // `Actual` (Q-155, RESOLVED). Обробник не знає чинного режиму на
        // момент подання, і писати сюди правдоподібне число замість
        // порожнечі — фальсифікація факту: воно виглядало б як зафіксований
        // вибір, якого насправді ніхто не робив.
        Assert.Null(snapshot.NumericMode);
        Assert.Null(snapshot.CalendarMode);
        Assert.Equal(Now, snapshot.SubmittedAt);
        Assert.Contains("12500", snapshot.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(64, snapshot.ContentHash.Length);
    }

    /// <summary>Одне поле шапки <c>AREA</c> (тип <c>String</c>) для тестів ФВ-9.4.</summary>
    private static Ecr.Domain.Entities.Configuration.HeaderFieldDef AreaHeaderField(int id)
    {
        var field = new Ecr.Domain.Entities.Configuration.HeaderFieldDef(
            TemplateVersion, EcrCode.Create("AREA"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), 1, CellDataType.String);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(field, id);
        return field;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Подання_копіює_значення_шапки_документа_в_зріз_як_окрему_секцію()
    {
        // ⛔ За аналізом старої системи (Excel/VBA, вкладка "Contract"): подання
        // будь-якої звітної таблиці ЗАНОВО читає й ВБУДОВУЄ шапку в збережений
        // запис — знімок на момент подання, не посилання. Якщо шапку пізніше
        // змінять, уже подані звіти мають зберігати те, що було правдою тоді.
        const int AreaFieldId = 55;
        _metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>())
                 .Returns(Snapshot() with { HeaderFields = [AreaHeaderField(AreaFieldId)] });

        _headers.GetValuesAsync(Document, Arg.Any<CancellationToken>())
                .Returns(new Dictionary<int, DocumentHeaderValueData>
                {
                    [AreaFieldId] = new() { ValueString = "Дніпровський" },
                });

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        var snapshot = Assert.Single(_snapshots);
        var header = SubmissionPayload.ReadHeader(snapshot.PayloadJson);

        Assert.Equal(new SubmissionPayloadHeaderValue("Дніпровський", null), header["AREA"]);

        // Клітинки лишаються там же, де й завжди — секція header їх не заступає.
        var cells = SubmissionPayload.Read(snapshot.PayloadJson);
        Assert.Contains(cells, c => c.Value == "12500");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Подання_без_жодного_значення_шапки_дає_порожню_секцію_header_і_не_падає()
    {
        // Поле шапки в шаблоні Є, але його ніхто не заповнював — GetValuesAsync
        // не повертає запису взагалі (той самий контракт, що для клітинок,
        // R-B4: відсутній запис ≠ явна порожнеча, але тут різниця не потрібна).
        _metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>())
                 .Returns(Snapshot() with { HeaderFields = [AreaHeaderField(55)] });

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        var snapshot = Assert.Single(_snapshots);
        Assert.Empty(SubmissionPayload.ReadHeader(snapshot.PayloadJson));

        // ⚠ Формат лишається як до ФВ-9.4 (голий масив) — ContentHash документів
        // без заповненої шапки не зрушується попри нову можливість.
        Assert.StartsWith("[", snapshot.PayloadJson, StringComparison.Ordinal);
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Подання_рядка_чию_обовязкову_клітинку_ніколи_не_редагували_відхиляється()
    {
        // ⛔ Це саме та прогалина, яку `PatchCellsHandler` не закриває:
        // `ColumnDef.IsRequired` перевіряється ЛИШЕ в момент запису значення
        // через `PATCH /cells`. Рядок, чию обов'язкову клітинку взагалі не
        // торкались редагуванням, не лишає жодного запису `doc.CellValue`
        // (ФВ-3.8) — і тому не проходить НІ через `ColumnDef.ValidateValue`,
        // ні через жодну іншу перевірку. Подання — природна точка
        // «готовність», де ця гарантія має нарешті з'явитися.
        _metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>())
                 .Returns(Snapshot(requiredColumn: true));

        // Зріз ПОРОЖНІЙ: рядок 7001001 існує (є в `GetRowIdsAsync`, підставленому
        // в конструкторі), але жодної клітинки за нього ніколи не записували.
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Submit().HandleAsync(Document, Water, Period, CancellationToken.None));

        Assert.Equal("ECR-SUB-4221", error.ErrorCode);
        Assert.Empty(_snapshots);
        Assert.Equal(DocumentStatus.Draft, _sheets[Water].Status);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Подання_рядка_з_явно_заповненою_обовязковою_клітинкою_проходить()
    {
        // Контрольний випадок для попереднього теста: та сама обов'язкова
        // колонка, але значення справді записане — подання не має чіплятися
        // до заповнених рядків.
        _metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>())
                 .Returns(Snapshot(requiredColumn: true));

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        Assert.Equal(DocumentStatus.Submitted, _sheets[Water].Status);
    }

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
    public async Task Reopen_без_рішення_прив_язаного_до_документа_відхиляється()
    {
        // ⛔ Q-173 (аудит фази 2, авторизація). Глобального права
        // `Document.Reopen` НЕДОСТАТНЬО: обробник мусить питати
        // `CanReopenAsync` так само, як `Submit`/`Approve` питають
        // `CanSubmitAsync`/`CanApproveAsync` — операція, що скасовує подання,
        // не має вимагати менше за саме подання.
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        _access.CanReopenAsync(Arg.Any<AccessProfile>(), Document, Water, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Reopen().HandleAsync(Document, Water, Period, "причина", CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);

        // ⚠ Аркуш лишається Submitted — відмова стається ДО будь-якої зміни
        // стану, не після.
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

    [Fact] [Trait("Requirement", "ФВ-5.7")]
    public async Task Зріз_подання_несе_дату_булеве_й_запис_довідника()
    {
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>
              {
                  new(new CellAddress(new PeriodKey(Period), 1001, 11), 3, new CellValueData { ValueDate = new DateTime(2026, 1, 15) }),
                  new(new CellAddress(new PeriodKey(Period), 1001, 12), 3, new CellValueData { ValueBool = true }),
                  new(new CellAddress(new PeriodKey(Period), 1001, 13), 3, new CellValueData { ValueRegistryEntryId = 777 }),
              });

        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        // Доти всі три лягали в зріз як `"value":null` — нерозрізненно з порожньою клітинкою.
        var cells = SubmissionPayload.Read(Assert.Single(_snapshots).PayloadJson);
        Assert.Equal(
            [("2026-01-15T00:00:00.0000000", "date"), ("true", "bool"), ("777", "ref")],
            cells.Select(c => (c.Value, c.Type)).ToArray());
    }
}
