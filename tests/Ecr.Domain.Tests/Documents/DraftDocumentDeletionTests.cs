// tests/Ecr.Domain.Tests/Documents/DraftDocumentDeletionTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>Видаляти можна лише чернетку (рішення людини 2026-09-21).</summary>
public sealed class DraftDocumentDeletionTests
{
    private static readonly DateTime Now = new(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

    private static ApprovalState Sheet(int sheetDefId) => new(documentId: 700, sheetDefId, periodKey: 202601);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Документ_без_станів_і_документ_з_усіма_Draft_без_історії_видаляються()
    {
        DraftDocumentDeletion.EnsureDraft([], hasWorkflowHistory: false);
        DraftDocumentDeletion.EnsureDraft([Sheet(1), Sheet(2)], hasWorkflowHistory: false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поданий_і_погоджений_аркуш_блокують_і_називають_найвагомішу_причину()
    {
        var submitted = Sheet(1);
        submitted.Submit(7, Now);
        var approved = Sheet(2);
        approved.Submit(7, Now);
        approved.Approve(8, Now);

        var error = Assert.Throws<DomainException>(
            () => DraftDocumentDeletion.EnsureDraft([Sheet(3), submitted, approved], hasWorkflowHistory: true));

        // 409 дає саме суфікс коду (`ExceptionHandlingMiddleware`), а не клас винятку.
        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.deleteNotDraft", error.Details!["messageKey"]);
        Assert.Equal("Approved", error.Details["reason"]);
        Assert.Equal("2", error.Details["sheetDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відкликаний_назад_у_Draft_аркуш_уже_не_чернетка()
    {
        // Статус знову Draft, але подання бачили погоджувачі — про це каже історія.
        var recalled = Sheet(1);
        recalled.Submit(7, Now);
        recalled.Recall("помилка", firstStepId: null);

        var error = Assert.Throws<DomainException>(
            () => DraftDocumentDeletion.EnsureDraft([recalled], hasWorkflowHistory: true));

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.deleteHasHistory", error.Details!["messageKey"]);
    }
}
