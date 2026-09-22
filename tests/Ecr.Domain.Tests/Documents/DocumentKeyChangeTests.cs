// tests/Ecr.Domain.Tests/Documents/DocumentKeyChangeTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>ФВ-3.9: ключ змінюється, доки жоден аркуш не поданий і не погоджений.</summary>
public sealed class DocumentKeyChangeTests
{
    private static readonly DateTime Now = new(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

    private static ApprovalState Sheet(int sheetDefId) => new(documentId: 700, sheetDefId, periodKey: 202601);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-3.9")]
    public void Чернетка_і_відхилений_аркуш_не_блокують()
    {
        var rejected = Sheet(2);
        rejected.Submit(7, Now);
        rejected.Reject(8, "wrong", Now);

        DocumentKeyChange.EnsureChangeable([]);
        DocumentKeyChange.EnsureChangeable([Sheet(1), rejected]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-3.9")]
    public void Погоджений_аркуш_блокує_і_називається_першим()
    {
        var submitted = Sheet(1);
        submitted.Submit(7, Now);
        var approved = Sheet(2);
        approved.Submit(7, Now);
        approved.Approve(8, Now);

        var error = Assert.Throws<DomainException>(
            () => DocumentKeyChange.EnsureChangeable([Sheet(3), submitted, approved]));

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.rekeyLocked", error.Details!["messageKey"]);
        Assert.Equal("Approved", error.Details["reason"]);
        Assert.Equal("2", error.Details["sheetDefId"]);
    }
}
