using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Розбір фільтрів <c>state</c>/<c>mine</c> переліку документів (<c>BE-09b</c>).</summary>
public sealed class ListDocumentsFilterTests
{
    private const int UserId = 7;
    private const int Period = 202601;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListDocumentsFilterTests()
    {
        _user.UserId.Returns(UserId);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }
                .Permission(ListDocumentsHandler.Permission)
                .Grant(ResourceKind.Project, 10, GrantLevel.Read)
                .Build());
        _documents.ListAsync(
                Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(), Arg.Any<DocumentListFilter>(),
                Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<DocumentSummary>([], NextCursor: null, TotalCount: null));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [InlineData("approved", false, DocumentStatus.Approved, null)]
    [InlineData(" Rejected ", true, DocumentStatus.Rejected, UserId)]
    [InlineData("", true, null, UserId)]
    [InlineData(null, false, null, null)]
    public async Task Фільтр_доходить_до_сховища_іменем_стану_і_поточним_користувачем(
        string? state, bool mine, DocumentStatus? expectedState, int? expectedUser)
    {
        await Handler().HandleAsync(null, Period, state, mine, new CursorRequest(50), default);

        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(),
            new DocumentListFilter(expectedState, expectedUser),
            Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [InlineData("Closed")]
    [InlineData("2")]
    [InlineData("99")]
    public async Task Невідомий_стан_422_а_не_мовчазне_усі(string state)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(null, Period, state, false, new CursorRequest(50), default));

        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.documentState", refused.Details!["messageKey"]);
        Assert.Equal(state, refused.Details!["state"]);
        await _documents.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, null!, null, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Стан_без_періоду_422_бо_поза_періодом_він_не_визначений()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(null, periodKey: null, "Draft", false, new CursorRequest(50), default));

        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.documentStateNeedsPeriod", refused.Details!["messageKey"]);
    }

    private ListDocumentsHandler Handler() => new(_documents, _access, _user);
}
