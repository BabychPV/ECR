// tests/Ecr.Application.Tests/Workflow/SubmissionSourceGateTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// D-6 (п. 11): періоди відповіді «Подати» несуть місяць документа-джерела HSE400, що ще не затверджений, — саме для
/// того періоду, подання якого сервер відхилить (<c>sourceMonthNotApproved</c>), а не для відкритого на екрані.
/// </summary>
/// <remarks>
/// ⛔ До фіксу «Submit» лишалась доступною, хоча панель проблем розрахунку казала, що місяць джерела не затверджено, і
/// сервер відмовляв. Мутація: прибрати умову <c>SourceApprovedAt is null</c> — тест «повторно затверджене джерело» червоний;
/// прибрати групування за <c>PeriodKey</c> (позначати всі) — тест «лише свій період» червоний.
/// </remarks>
public sealed class SubmissionSourceGateTests
{
    private const long Document = 600;
    private const int Jan = 202601;
    private const int Feb = 202602;
    private const int Mar = 202603;

    private static readonly IReadOnlyList<SubmissionPeriodDto> Periods =
    [
        new(Jan, PeriodState.Grace, DocumentStatus.Draft, true),
        new(Feb, PeriodState.Open, DocumentStatus.Draft, true),
        new(Mar, PeriodState.Open, DocumentStatus.Submitted, false),
    ];

    private static SourceIssueRow Row(string kind, int periodKey, int sourcePeriodKey, DateTime? sourceApprovedAt)
        => new(kind, periodKey, sourcePeriodKey % 100, sourcePeriodKey, 77, "HSE", "HSE-2026", sourceApprovedAt, null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Лише_свій_період_несе_незатверджений_місяць_джерела()
    {
        var reader = Reader(Row(ISourceIssueReader.SourceMonthNotApproved, Feb, Feb, null));

        var result = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, CancellationToken.None);

        Assert.Null(result.Single(p => p.PeriodKey == Jan).UnapprovedSourcePeriodKey);
        Assert.Equal(Feb, result.Single(p => p.PeriodKey == Feb).UnapprovedSourcePeriodKey);
        // Питаються лише періоди, які можна подати.
        await reader.Received(1).ListSourceIssuesAsync(
            Document, Arg.Is<IReadOnlyCollection<int>>(k => k.SequenceEqual(new[] { Jan, Feb })), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Повторно_затверджене_джерело_подання_не_блокує()
    {
        var reader = Reader(Row(ISourceIssueReader.SourceChangedAfterApproval, Jan, Jan, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, CancellationToken.None);

        Assert.All(result, p => Assert.Null(p.UnapprovedSourcePeriodKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Кілька_місяців_джерела_найраніший()
    {
        var reader = Reader(
            Row(ISourceIssueReader.SourceMonthNotApproved, Jan, 202603, null),
            Row(ISourceIssueReader.SourceMonthNotApproved, Jan, 202601, null));

        var result = await SubmissionSourceGate.ApplyAsync(reader, Document, Periods, CancellationToken.None);

        Assert.Equal(202601, result.Single(p => p.PeriodKey == Jan).UnapprovedSourcePeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-559")]
    public async Task Без_читача_чи_без_періодів_для_подання_нічого()
    {
        Assert.Same(Periods, await SubmissionSourceGate.ApplyAsync(null, Document, Periods, CancellationToken.None));

        var reader = Reader();
        IReadOnlyList<SubmissionPeriodDto> none = [new(Mar, PeriodState.Open, DocumentStatus.Submitted, false)];
        Assert.Same(none, await SubmissionSourceGate.ApplyAsync(reader, Document, none, CancellationToken.None));
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
