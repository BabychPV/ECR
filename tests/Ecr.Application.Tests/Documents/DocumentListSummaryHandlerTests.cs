using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Зведення переліку документів: право і межа видимості (<c>BE-09</c>).</summary>
public sealed class DocumentListSummaryHandlerTests
{
    private const int Mine = 10;
    private const int Foreign = 11;

    private readonly IDocumentListSummaryStore _store = Substitute.For<IDocumentListSummaryStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public DocumentListSummaryHandlerTests()
    {
        _user.UserId.Returns(7);
        _store.SummarizeAsync(
                Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentListSummaryResponse(1, 0, 0, 0, 0));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Зведення_не_лічить_документів_чужого_проєкту()
    {
        Profile(new AccessBuilder { UserId = 7 }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, Mine, GrantLevel.Read)
            .Grant(ResourceKind.Project, Foreign, GrantLevel.Read)
            .Deny(ResourceKind.Project, Foreign));

        await Handler().HandleAsync(projectId: null, 202601, default);

        // ⛔ Межа йде в ЗАПИТ і є тією самою, що в переліку: лише `Mine`,
        // заборонений `Foreign` — ні, і `null` («без межі») — теж ні.
        await _store.Received(1).SummarizeAsync(
            null,
            202601,
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.Count == 1 && ids.Contains(Mine)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Без_права_перегляду_документів_сховище_не_питають()
    {
        Profile(new AccessBuilder { UserId = 7 }.Grant(ResourceKind.Project, Mine, GrantLevel.Read));

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(projectId: null, 202601, default));

        await _store.DidNotReceiveWithAnyArgs().SummarizeAsync(default, default, default, default);
    }

    private GetDocumentListSummaryHandler Handler() => new(_store, _access, _user);

    private void Profile(AccessBuilder builder)
        => _access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(builder.Build());
}
