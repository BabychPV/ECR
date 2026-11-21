// tests/Ecr.Application.Tests/Workflow/SubmissionSourceGateTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// D-6 (п. 11 і борг координатора): відповідь <c>submission-target</c> несе місяць документа-джерела HSE400, що ще не
/// затверджений, — саме для того періоду, подання (неподаний аркуш) чи затвердження (поданий аркуш, місяць річного
/// аркуша) якого сервер відхилить (<c>sourceMonthNotApproved</c>), а не для відкритого на екрані.
/// </summary>
/// <remarks>
/// ⛔ До фіксу «Submit»/«Approve» лишались доступними, хоча панель проблем розрахунку казала, що місяць джерела не
/// затверджено, і сервер відмовляв. Мутації: прибрати умову <c>SourceApprovedAt is null</c> — «повторно затверджене
/// джерело» червоний; позначати всі періоди — «лише свій період» червоний; не брати поданих — тести затвердження червоні.
/// </remarks>
public sealed class SubmissionSourceGateTests
{
    private const long Document = 600;
    private const int Jan = 202601;
    private const int Feb = 202602;
    private const int Mar = 202603;
    private const int Apr = 202604;

    private static readonly IReadOnlyList<SubmissionPeriodDto> Periods =
    [
        new(Jan, PeriodState.Grace, DocumentStatus.Draft, true),
        new(Feb, PeriodState.Open, DocumentStatus.Draft, true),
        new(Mar, PeriodState.Open, DocumentStatus.Submitted, false),
        new(Apr, PeriodState.Open, DocumentStatus.Approved, false),
    ];

    private static readonly IReadOnlyList<MonthColumnStatusDto> Months =
    [
        new(7, "JAN", 1, Jan, DocumentStatus.Draft),
        new(7, "MAR", 3, Mar, DocumentStatus.Submitted),
        new(8, "MAR_B", 3, Mar, DocumentStatus.Submitted),
        new(7, "APR", 4, Apr, DocumentStatus.Approved),
    ];

    private static SourceIssueRow Row(string kind, int periodKey, int sourcePeriodKey, DateTime? sourceApprovedAt)
        => new(kind, periodKey, sourcePeriodKey % 100, sourcePeriodKey, 77, "HSE", "HSE-2026", sourceApprovedAt, null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Лише_свій_період_несе_незатверджений_місяць_джерела()
    {
        var reader = Reader(Row(ISourceIssueReader.SourceMonthNotApproved, Feb, Feb, null));

        var (periods, months, _) = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, Months, null, CancellationToken.None);

        Assert.Null(periods.Single(p => p.PeriodKey == Jan).UnapprovedSourcePeriodKey);
        Assert.Equal(Feb, periods.Single(p => p.PeriodKey == Feb).UnapprovedSourcePeriodKey);
        Assert.All(months, m => Assert.Null(m.UnapprovedSourcePeriodKey));
        // Питаються періоди, які можна подати чи затвердити (подані), — одним пакетом; затверджені — ні.
        await reader.Received(1).ListSourceIssuesAsync(
            Document, Arg.Is<IReadOnlyCollection<int>>(k => k.SequenceEqual(new[] { Jan, Feb, Mar })), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Поданий_період_і_поданий_місяць_несуть_місяць_джерела_затвердження()
    {
        var reader = Reader(Row(ISourceIssueReader.SourceMonthNotApproved, Mar, Mar, null));

        var (periods, months, _) = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, Months, null, CancellationToken.None);

        Assert.Equal(Mar, periods.Single(p => p.PeriodKey == Mar).UnapprovedSourcePeriodKey);
        Assert.All(months.Where(m => m.PeriodKey == Mar), m => Assert.Equal(Mar, m.UnapprovedSourcePeriodKey));
        Assert.Null(months.Single(m => m.PeriodKey == Jan).UnapprovedSourcePeriodKey);
    }

    /// <summary>Звичайний документ (місячних колонок немає): поданий період сторінки — ціль «Approve» — теж питається.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Звичайний_документ_поданий_період_несе_місяць_джерела_затвердження()
    {
        var reader = Reader(Row(ISourceIssueReader.SourceMonthNotApproved, Mar, Mar, null));

        var (periods, _, _) = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, [], null, CancellationToken.None);

        Assert.Equal(Mar, periods.Single(p => p.PeriodKey == Mar).UnapprovedSourcePeriodKey);
        Assert.Null(periods.Single(p => p.PeriodKey == Apr).UnapprovedSourcePeriodKey);
        await reader.Received(1).ListSourceIssuesAsync(
            Document, Arg.Is<IReadOnlyCollection<int>>(k => k.SequenceEqual(new[] { Jan, Feb, Mar })), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// L2: звичайний документ, аркуш поданий у ЗАКРИТОМУ періоді сторінки (у <c>periods[]</c> його немає) — той самий пакет
    /// читає і його, відповідь несе місяць джерела для затвердження якоря.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Поданий_якір_у_закритому_періоді_несе_місяць_джерела_затвердження()
    {
        const int Closed = 202512;
        var reader = Reader(Row(ISourceIssueReader.SourceMonthNotApproved, Closed, Closed, null));

        var gated = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, [], Closed, CancellationToken.None);

        Assert.Equal(Closed, gated.AnchorUnapprovedSourcePeriodKey);
        Assert.All(gated.Periods, p => Assert.Null(p.UnapprovedSourcePeriodKey));
        await reader.Received(1).ListSourceIssuesAsync(
            Document, Arg.Is<IReadOnlyCollection<int>>(k => k.SequenceEqual(new[] { Closed, Jan, Feb, Mar })), Arg.Any<CancellationToken>());

        // Якір не поданий — питати нема чого, і поле порожнє.
        var none = await SubmissionSourceGate.ApplyAsync(
            Reader(Row(ISourceIssueReader.SourceMonthNotApproved, Closed, Closed, null)), Document, Periods, [], null, CancellationToken.None);
        Assert.Null(none.AnchorUnapprovedSourcePeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Повторно_затверджене_джерело_не_блокує()
    {
        var reader = Reader(
            Row(ISourceIssueReader.SourceChangedAfterApproval, Jan, Jan, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            Row(ISourceIssueReader.SourceChangedAfterApproval, Mar, Mar, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));

        var (periods, months, _) = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, Months, null, CancellationToken.None);

        Assert.All(periods, p => Assert.Null(p.UnapprovedSourcePeriodKey));
        Assert.All(months, m => Assert.Null(m.UnapprovedSourcePeriodKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Кілька_місяців_джерела_найраніший()
    {
        var reader = Reader(
            Row(ISourceIssueReader.SourceMonthNotApproved, Jan, 202603, null),
            Row(ISourceIssueReader.SourceMonthNotApproved, Jan, 202601, null));

        var (periods, _, _) = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, Months, null, CancellationToken.None);

        Assert.Equal(202601, periods.Single(p => p.PeriodKey == Jan).UnapprovedSourcePeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Без_читача_чи_без_періодів_для_подання_й_затвердження_нічого()
    {
        var (samePeriods, sameMonths, _) = await SubmissionSourceGate.ApplyAsync(null, Document, Periods, Months, null, CancellationToken.None);
        Assert.Same(Periods, samePeriods);
        Assert.Same(Months, sameMonths);

        var reader = Reader();
        IReadOnlyList<SubmissionPeriodDto> none = [new(Apr, PeriodState.Open, DocumentStatus.Approved, false)];
        IReadOnlyList<MonthColumnStatusDto> noMonths = [new(7, "APR", 4, Apr, DocumentStatus.Approved)];
        var (p, m, _) = await SubmissionSourceGate.ApplyAsync(reader, Document, none, noMonths, null, CancellationToken.None);
        Assert.Same(none, p);
        Assert.Same(noMonths, m);
        Assert.Empty(reader.ReceivedCalls());
    }

    private static ISourceIssueReader Reader(params SourceIssueRow[] rows)
    {
        var reader = Substitute.For<ISourceIssueReader>();
        reader.ListSourceIssuesAsync(Document, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(rows);

        return reader;
    }
}
