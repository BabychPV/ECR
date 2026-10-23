using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Search;
using Ecr.Application.Security;
using Ecr.Application.Workflow.ApprovalQueue;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// D-444 (AN-131): бюджет запитів. Учасник документа з'являється в черзі, зведенні, фільтрі стану й пошуку, але
/// користувач БЕЗ надань (ознака профілю <see cref="AccessProfile.HasDocumentParticipations"/> = <c>false</c>) не платить
/// за це жодного звернення: надання не читаються, сховища отримують рівно попередні аргументи.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, не в коміті): прибрати перевірку <c>profile.HasDocumentParticipations</c> перед
/// <c>ActiveParticipationsAsync</c> у <c>ListMyApprovalQueueHandler</c>, <c>SearchHandler</c> чи
/// <c>DocumentSheetVisibility.ParticipantScopesAsync</c> → червоніє відповідний тест (виклик надань є). Навпаки —
/// не передати надання учасника в сховище черги → червоніє <c>Учасник_передає_свої_документи_у_вибірку_черги</c>.
/// </remarks>
public sealed class ParticipantReachBudgetTests
{
    private const int UserId = 7;
    private const int ProjectId = 10;
    private const int Period = 202601;

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IApprovalQueueStore _queue = Substitute.For<IApprovalQueueStore>();

    public ParticipantReachBudgetTests()
    {
        _user.UserId.Returns(UserId);
        _queue.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(
            [new ApprovalQueueProject(ProjectId, "P10", 1, "T1", ProjectStatus.Active, false)]);
        _queue.ListActiveRoutesAsync(Arg.Any<CancellationToken>()).Returns(
            new ApprovalQueueRoutes([], new Dictionary<int, string>()));
        _queue.ListSubmittedAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<bool>(), Arg.Any<ApprovalQueueCursor?>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalQueueCandidate>());
    }

    [Fact]
    [Trait("Decision", "D-444")]
    public async Task Без_надань_черга_не_читає_надань_і_вибірка_як_раніше()
    {
        Profile(participant: false);

        await QueueHandler().HandleAsync(null, null, default);

        await _access.DidNotReceiveWithAnyArgs().ActiveParticipationsAsync(default!, default);
        await _queue.Received(1).ListSubmittedAsync(
            Arg.Is<IReadOnlyCollection<int>>(p => p.Count == 1 && p.Contains(ProjectId)), Arg.Any<bool>(),
            Arg.Any<ApprovalQueueCursor?>(), Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<long>>(d => d.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-444")]
    public async Task Учасник_передає_свої_документи_у_вибірку_черги()
    {
        Profile(participant: true);
        _access.ActiveParticipationsAsync(Arg.Any<AccessProfile>(), Arg.Any<CancellationToken>()).Returns(
            new Dictionary<long, IReadOnlyList<ParticipationGrant>>
            {
                [55] = [new ParticipationGrant(GrantLevel.Approve, null, null, null)],
            });

        await QueueHandler().HandleAsync(null, null, default);

        await _queue.Received(1).ListSubmittedAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<bool>(), Arg.Any<ApprovalQueueCursor?>(), Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<long>>(d => d.Count == 1 && d.Contains(55L)), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-444")]
    public async Task Без_надань_пошук_не_читає_надань()
    {
        Profile(participant: false);
        var store = Substitute.For<ISearchStore>();
        store.SearchAsync(default!, default!, default, default).ReturnsForAnyArgs(Array.Empty<SearchRow>());

        await new SearchHandler(store, _access, _user).HandleAsync("DOC", 10, default);

        await _access.DidNotReceiveWithAnyArgs().ActiveParticipationsAsync(default!, default);
        await store.Received(1).SearchAsync(
            "DOC", Arg.Is<SearchScope>(s => s.ParticipatedDocuments == null), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-444")]
    public async Task Без_надань_фільтр_стану_не_читає_надань_і_не_додає_документів_учасника()
    {
        Profile(participant: false);
        var documents = Substitute.For<IDocumentStore>();
        documents.ListAsync(
                Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(), Arg.Any<DocumentListFilter>(),
                Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<DocumentSummary>([], NextCursor: null, TotalCount: null));
        var clock = Substitute.For<IClock>();
        var handler = new ListDocumentsHandler(
            documents, _access, _user, Substitute.For<IDocumentListSummaryStore>(), clock);

        await handler.HandleAsync(null, Period, "Draft", mine: false, hasLateEdits: null, new CursorRequest(50), default);

        await _access.DidNotReceiveWithAnyArgs().ActiveParticipationsAsync(default!, default);
        await _access.DidNotReceiveWithAnyArgs().ReadScopesAsync(default!, default!, default);
        await documents.Received(1).ListAsync(
            Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(),
            Arg.Is<DocumentListFilter>(f => f.Participation == null && f.State == DocumentStatus.Draft),
            Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    private ListMyApprovalQueueHandler QueueHandler()
        => new(_queue, _access, Substitute.For<IMetadataCache>(), _user);

    private void Profile(bool participant)
        => _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId, Participant = participant }
                .Permission(ListDocumentsHandler.Permission)
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Approve)
                .Build());
}
