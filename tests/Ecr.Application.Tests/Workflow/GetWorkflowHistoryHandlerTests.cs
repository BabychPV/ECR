// tests/Ecr.Application.Tests/Workflow/GetWorkflowHistoryHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary><c>BE-11b</c>: читання журналу переходів — доступ і форма відповіді.</summary>
public sealed class GetWorkflowHistoryHandlerTests
{
    private const long DocumentId = 501;
    private const int PeriodKeyValue = 202601;
    private const int UserId = 7;
    private static readonly DateTime At = new(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Чужий_документ_не_віддає_історії_і_відповідає_як_неіснуючий()
    {
        // ⚠ Право `Document.View` Є, гранта на проєкт НЕМАЄ — і в журналі лежить
        // подія. Без перевірки доступу до ЦЬОГО документа вона б і повернулася.
        var workflow = StoreWith(Event(ApprovalAction.Submit, UserId, "Olena Koval"));
        var handler = Handler(workflow, new AccessBuilder { UserId = UserId }.Permission("Document.View"));

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DocumentId, PeriodKeyValue, CancellationToken.None)).ConfigureAwait(true);

        // Той самий код і ключ, що в `GET /documents/{id}` на неіснуючий документ.
        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0404.document", error.Details!["messageKey"]);

        await workflow.DidNotReceiveWithAnyArgs()
                      .GetHistoryAsync(default, default, default, default).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Події_віддаються_в_порядку_сховища_з_іменем_а_системні_як_system()
    {
        var workflow = StoreWith(
            Event(ApprovalAction.Reject, UserId, "Olena Koval", reason: "total mismatch"),
            Event(ApprovalAction.Reopen, byUserId: null, displayName: null),
            Event(ApprovalAction.Submit, byUserId: 99, displayName: null));

        var handler = Handler(workflow, Reader());

        var history = await handler.HandleAsync(DocumentId, PeriodKeyValue, CancellationToken.None)
                                   .ConfigureAwait(true);

        Assert.Equal(["Reject", "Reopen", "Submit"], history.Select(e => e.Action));

        // Ім'я — відображуване; `null` — система; зниклий користувач — НЕ система.
        Assert.Equal(["Olena Koval", "system", "#99"], history.Select(e => e.ByDisplayName));

        var rejected = history[0];
        Assert.Equal(("S1", "Submitted", "Rejected", "total mismatch", At),
            (rejected.SheetCode, rejected.FromState, rejected.ToState, rejected.Reason, rejected.At));

        await workflow.Received(1).GetHistoryAsync(
            DocumentId, new PeriodKey(PeriodKeyValue), GetWorkflowHistoryHandler.MaxEvents,
            Arg.Any<CancellationToken>()).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_Document_View_відмова_403()
    {
        var handler = Handler(StoreWith(), new AccessBuilder { UserId = UserId });

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(DocumentId, PeriodKeyValue, CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);

        // Названа в обробнику константа — саме те право, якого справді забракло.
        Assert.Equal(GetWorkflowHistoryHandler.Permission, error.Details!["permission"]);
    }

    // ────────────────────────────── збірка ────────────────────────────

    private static AccessBuilder Reader()
        => new AccessBuilder { UserId = UserId }
            .Permission("Document.View")
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);

    private static ApprovalEventRecord Event(
        ApprovalAction action, int? byUserId, string? displayName, string? reason = null)
        => new("S1", DocumentStatus.Submitted, DocumentStatus.Rejected, action, byUserId, displayName, At, reason, null);

    private static IWorkflowStore StoreWith(params ApprovalEventRecord[] events)
    {
        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetHistoryAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(events);
        return workflow;
    }

    private static GetWorkflowHistoryHandler Handler(IWorkflowStore workflow, AccessBuilder profile)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile.Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var documents = Substitute.For<IDocumentStore>();
        documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
                 .Returns(new DocumentSummary(
                     DocumentId, AccessBuilder.ProjectId, "DOC-501", At, 1,
                     new Dictionary<string, string>(StringComparer.Ordinal)));

        return new GetWorkflowHistoryHandler(new GetDocumentHandler(documents, access, user), workflow);
    }
}
