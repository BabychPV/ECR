// tests/Ecr.Application.Tests/Documents/DeleteDocumentSourceTriggerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// B80 (N2a, T-C): видалення документа-джерела — роки читаються ДО видалення, постановка — ПІСЛЯ коміту; невдале
/// видалення нічого не ставить. Що саме ставить тригер — <c>SourceApprovalChangeTriggerTests</c> (Infrastructure).
/// </summary>
[Trait("Directive", "B80")]
public sealed class DeleteDocumentSourceTriggerTests
{
    private const long DocumentId = 4242;
    private const int ProjectId = 31;
    private const int UserId = 9;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IDocumentDeletionStore _deletion = Substitute.For<IDocumentDeletionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ISourceApprovalChangeTrigger _trigger = Substitute.For<ISourceApprovalChangeTrigger>();
    private readonly SourceDocumentDeletion _prepared = new(DocumentId, BindingIds: [7], Years: [2099]);

    public DeleteDocumentSourceTriggerTests()
    {
        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(DocumentId, ProjectId, "DOC-B80", DateTime.UnixEpoch, 1, new Dictionary<string, string>()));
        _deletion.LockFreezeFactsAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentFreezeFacts(ProjectStatus.Active, IsArchiving: false, []));
        _deletion.LockWorkflowFactsAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentWorkflowFacts([], HasWorkflowHistory: false));
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _trigger.PrepareDocumentDeletionAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(_prepared);
    }

    /// <summary>Мутація: прибрати виклик <c>OnSourceDocumentDeletedAsync</c> в обробнику — червоний.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Видалення_джерела_ставить_перерахунок_після_видалення_з_автором()
    {
        await Handler().HandleAsync(DocumentId, CancellationToken.None);

        Received.InOrder(() =>
        {
            _ = _trigger.PrepareDocumentDeletionAsync(DocumentId, Arg.Any<CancellationToken>());
            _ = _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
            _ = _deletion.DeleteAsync(DocumentId, Arg.Any<CancellationToken>());
            _ = _trigger.OnSourceDocumentDeletedAsync(_prepared, UserId, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// ✎ PSS-15 (огляд L-new-1, D-562): споживачів документа-джерела позначено ДО видалення, у тій самій транзакції, з
    /// автором і моментом видалення — інакше нічний перерахунок споживача не побачив би, що джерела вже немає.
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати виклик <c>TouchSourceConsumersAsync</c> в обробнику — червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait("Finding", "PSS-15")]
    public async Task Видалення_джерела_торкається_споживачів_до_видалення()
    {
        await Handler().HandleAsync(DocumentId, CancellationToken.None);

        Received.InOrder(() =>
        {
            _ = _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
            _ = _deletion.TouchSourceConsumersAsync(DocumentId, UserId, DateTime.UnixEpoch, Arg.Any<CancellationToken>());
            _ = _deletion.DeleteAsync(DocumentId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Невдале_видалення_нічого_не_ставить()
    {
        _deletion.DeleteAsync(DocumentId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("збій видалення"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(DocumentId, CancellationToken.None));

        await _trigger.DidNotReceiveWithAnyArgs().OnSourceDocumentDeletedAsync(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Не_джерело_нічого_не_ставить()
    {
        _trigger.PrepareDocumentDeletionAsync(DocumentId, Arg.Any<CancellationToken>()).Returns((SourceDocumentDeletion?)null);

        await Handler().HandleAsync(DocumentId, CancellationToken.None);

        await _deletion.Received(1).DeleteAsync(DocumentId, Arg.Any<CancellationToken>());
        await _trigger.DidNotReceiveWithAnyArgs().OnSourceDocumentDeletedAsync(default!, default, default);
    }

    private DeleteDocumentHandler Handler()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }
                .Permission(DeleteDocumentHandler.Permission)
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Write)
                .Build());

        var gate = Substitute.For<ISheetEditGate>();
        gate.EnterStructureAsync(DocumentId, true, Arg.Any<CancellationToken>()).Returns(1);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        return new DeleteDocumentHandler(
            _documents, _deletion, Substitute.For<IWorkflowStore>(), gate, access, _uow, Substitute.For<IAuditWriter>(),
            user, new TestClock(DateTime.UnixEpoch), sourceTrigger: _trigger);
    }
}
