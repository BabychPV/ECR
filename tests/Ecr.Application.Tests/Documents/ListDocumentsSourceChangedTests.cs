using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ F5-R (D-570, рішення людини P-1122-4): рядок переліку документів несе затверджені періоди, чиє джерело змінилося після
/// затвердження (<c>sourceChangedPeriods</c>). Кандидати — без запитів: з періодом — усі аркуші затверджені; з роком —
/// клітинки смуги «Approved»; частково затверджений документ і незатверджені місяці читачеві не передаються.
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально): прибрати виклик <c>MarkSourceChangedAsync</c> з обробника — червоний перший тест; прибрати умову «усі
/// аркуші затверджені» — червоний другий.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Decision", "D-570")]
public sealed class ListDocumentsSourceChangedTests
{
    private const int UserId = 7;
    private const int Period = 202603;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly ISourceIssueReader _reader = Substitute.For<ISourceIssueReader>();

    public ListDocumentsSourceChangedTests()
    {
        _user.UserId.Returns(UserId);
        var profile = new AccessBuilder { UserId = UserId }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Build();
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile);
    }

    [Fact]
    public async Task Затверджений_документ_зі_зміненим_джерелом_позначено()
    {
        Page(Doc(1, "Approved"), Doc(2, "Approved"));
        _reader.ListSourceChangedPeriodsAsync(Arg.Any<IReadOnlyDictionary<long, IReadOnlyList<int>>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<int>> { [1] = [Period] });

        var result = await Handler(_reader).HandleAsync(null, Period, null, mine: false, hasLateEdits: null, new CursorRequest(50), default);

        Assert.Equal([Period], result.Items.Single(d => d.Id == 1).SourceChangedPeriods);
        Assert.Null(result.Items.Single(d => d.Id == 2).SourceChangedPeriods);
    }

    [Fact]
    public async Task Кандидати_лише_затверджені_періоди()
    {
        var year = Doc(3, "Draft") with
        {
            Sheets = [],
            PeriodStates = [new DocumentPeriodState(202601, "Approved"), new DocumentPeriodState(202602, "Submitted")],
        };
        Page(Doc(1, "Approved"), Doc(2, "Approved", "Submitted"), year);
        _reader.ListSourceChangedPeriodsAsync(Arg.Any<IReadOnlyDictionary<long, IReadOnlyList<int>>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<int>>());

        await Handler(_reader).HandleAsync(null, Period, null, mine: false, hasLateEdits: null, new CursorRequest(50), default);

        await _reader.Received(1).ListSourceChangedPeriodsAsync(
            Arg.Is<IReadOnlyDictionary<long, IReadOnlyList<int>>>(a =>
                a.Count == 2 && a[1].Count == 1 && a[1][0] == Period && a[3].Count == 1 && a[3][0] == 202601),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Без_затверджених_читача_не_питають_і_без_читача_позначки_немає()
    {
        Page(Doc(1, "Submitted"));
        await Handler(_reader).HandleAsync(null, Period, null, mine: false, hasLateEdits: null, new CursorRequest(50), default);
        Assert.Empty(_reader.ReceivedCalls());

        Page(Doc(1, "Approved"));
        var result = await Handler(null).HandleAsync(null, Period, null, mine: false, hasLateEdits: null, new CursorRequest(50), default);
        Assert.Null(Assert.Single(result.Items).SourceChangedPeriods);
    }

    private ListDocumentsHandler Handler(ISourceIssueReader? reader)
        => new(_documents, _access, _user, Substitute.For<IDocumentListSummaryStore>(), sourceIssues: reader);

    private static DocumentSummary Doc(long id, params string[] states)
        => new(
            id, AccessBuilder.ProjectId, $"D{id}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), states.Length,
            states.Select((s, i) => (Code: $"S{i}", State: s)).ToDictionary(x => x.Code, x => x.State),
            Sheets: [.. states.Select((s, i) => new DocumentSheetState($"S{i}", new LocalizedText(new Dictionary<string, string> { ["en"] = "S" }), s))]);

    private void Page(params DocumentSummary[] items)
        => _documents.ListAsync(
                Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(), Arg.Any<DocumentListFilter>(),
                Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<DocumentSummary>(items, NextCursor: null, TotalCount: null));
}
