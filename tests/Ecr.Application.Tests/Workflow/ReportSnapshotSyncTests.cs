// tests/Ecr.Application.Tests/Workflow/ReportSnapshotSyncTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Зміна стану аркушів доходить до зрізів звітності (<c>H-23b</c>).
/// </summary>
/// <remarks>
/// ⛔ На позначці «подано» тримається головна гарантія <c>ER-C-11</c>:
/// <b>поданий зріз не перераховується взагалі</b> (ФВ-9.17). Поки її не
/// ставив ніхто, гарантія була написана і не діяла жодного разу — зріз, за
/// яким звіт уже пішов регуляторові, спокійно перебудовувався з іншими
/// числами.
/// </remarks>
public sealed class ReportSnapshotSyncTests
{
    private const long Document = 700;
    private const int Project = 3;
    private const int Period = 202601;
    private const long SnapshotId = 55;
    private const int UserId = 9;

    private readonly IReportSnapshotBuilder _snapshots = Substitute.For<IReportSnapshotBuilder>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();

    public ReportSnapshotSyncTests()
    {
        _documents.FindProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(Project);

        _snapshots.ListAsync(Project, Period, Arg.Any<CancellationToken>())
            .Returns([Summary(nameof(SnapshotStatus.Draft), isCurrent: true)]);
    }

    private ReportSnapshotSync Sync() => new(_snapshots, _documents);

    private static ReportSnapshotSummary Summary(string status, bool isCurrent)
        => new(
            SnapshotId,
            ReportVersionId: 1,
            ProjectId: Project,
            PeriodKey: Period,
            Status: status,
            IsCurrent: isCurrent,
            RowCount: 10,
            ContentHash: null,
            BuiltAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public async Task Подання_останнього_аркуша_морозить_поточний_зріз()
    {
        // ⛔ Регресія: виклик прибрали — і зріз лишається перебудовуваним
        // назавжди. Ніщо не падає: числа просто одного дня стають іншими в
        // документі, який уже подали.
        _snapshots.RefreshStatusAsync(SnapshotId, Arg.Any<CancellationToken>())
            .Returns(SnapshotStatus.Submitted);

        await Sync().MarkSubmittedAsync(Document, new PeriodKey(Period), UserId, CancellationToken.None);

        await _snapshots.Received(1).MarkSubmittedAsync(SnapshotId, UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public async Task Подання_одного_аркуша_з_кількох_зріз_НЕ_морозить()
    {
        // ⚠ Умова замороження — не «подали цей аркуш», а «у періоді не
        // лишилося неподаних». Заморозити раніше означало б зробити зріз
        // іммутабельним посеред заповнення: решта аркушів дійшла б до нього
        // вже після заморозки і не потрапила б у звіт ніколи.
        _snapshots.RefreshStatusAsync(SnapshotId, Arg.Any<CancellationToken>())
            .Returns(SnapshotStatus.Draft);

        await Sync().MarkSubmittedAsync(Document, new PeriodKey(Period), UserId, CancellationToken.None);

        await _snapshots.DidNotReceive().MarkSubmittedAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public async Task Затвердження_перераховує_статус_зрізу_але_не_морозить_його()
    {
        // ⛔ Статус зрізу успадковується від даних (`D-65`). Без перерахунку
        // регуляторна вʼюха назавжди тримає стан на момент побудови: аркуші
        // затвердили, а звіт лишився чернетковим і в перелік не потрапив.
        _snapshots.RefreshStatusAsync(SnapshotId, Arg.Any<CancellationToken>())
            .Returns(SnapshotStatus.Approved);

        await Sync().RefreshAsync(Document, new PeriodKey(Period), CancellationToken.None);

        await _snapshots.Received(1).RefreshStatusAsync(SnapshotId, Arg.Any<CancellationToken>());

        // ⚠ Затвердження — не подання. Заморожує саме подання: воно і є актом,
        // після якого числа пішли назовні.
        await _snapshots.DidNotReceive().MarkSubmittedAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23b")]
    public async Task Уже_поданий_зріз_повторно_не_чіпається()
    {
        // ⚠ Інакше кожна наступна зміна стану аркуша била б у доменну відмову
        // «зріз іммутабельний» — тобто нормальний хід подій виглядав би як
        // помилка, і його навчилися б гасити.
        _snapshots.ListAsync(Project, Period, Arg.Any<CancellationToken>())
            .Returns([Summary(nameof(SnapshotStatus.Submitted), isCurrent: true)]);

        await Sync().RefreshAsync(Document, new PeriodKey(Period), CancellationToken.None);

        await _snapshots.DidNotReceive().RefreshStatusAsync(
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
