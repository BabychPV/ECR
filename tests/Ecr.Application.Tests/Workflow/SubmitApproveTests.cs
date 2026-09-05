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

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanSubmitAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanApproveAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());

        _rows.GetOrphanFlagsAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(Document, Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>
              {
                  new(new CellAddress(new PeriodKey(Period), 1001, 11), 3,
                      new CellValueData { ValueNumeric = 12500m }),
              });
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

    private SubmitSheetHandler Submit()
        => new(_cells, _rows, _workflow, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _uow, _outbox, _user, _clock);

    private ApproveSheetHandler Approve() => new(_workflow, _access, _uow, _outbox, _user, _clock);

    private ReopenDocumentHandler Reopen() => new(_workflow, _access, _uow, _user, _clock);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Подання_кладе_подію_в_чергу_сповіщень()
    {
        // ⛔ `A7-31`. Черга `itg.NotificationOutbox`, відправник і задача, яка
        // її розбирає, існували від Етапу 5 — а покласти в неї подію не міг
        // НІХТО: у всій системі таблиця лише читалася. Жодне сповіщення не
        // надсилалося ніколи, і дізнатися про це можна було тільки з мовчання.
        await Submit().HandleAsync(Document, Water, Period, CancellationToken.None);

        await _outbox.Received(1).EnqueueAsync(
            "sheet.submitted",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
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
        Assert.Equal((byte)CalendarMode.Actual, snapshot.CalendarMode);
        Assert.Equal(Now, snapshot.SubmittedAt);
        Assert.Contains("12500", snapshot.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(64, snapshot.ContentHash.Length);
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

        _cells.ReadSliceAsync(Document, Arg.Any<CancellationToken>())
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
