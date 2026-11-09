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
                Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<SummaryRestrictions?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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
            null,
            7,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Без_права_перегляду_документів_сховище_не_питають()
    {
        Profile(new AccessBuilder { UserId = 7 }.Grant(ResourceKind.Project, Mine, GrantLevel.Read));

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(projectId: null, 202601, default));

        await _store.DidNotReceiveWithAnyArgs().SummarizeAsync(default, default, default, default, default, default);
    }

    /// <summary>
    /// ✎ Period clarity (координатор 07.11): рік без місяця — зведення за роком, тією самою межею грантів; місяць
    /// важить більше за рік; без обох — 422 <c>documentStateNeedsPeriod</c>, рік поза межами — 422.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Рік_без_місяця_рахується_за_роком_тією_самою_межею()
    {
        Profile(new AccessBuilder { UserId = 7 }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, Mine, GrantLevel.Read));
        _store.SummarizeYearAsync(
                Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<SummaryRestrictions?>(),
                Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentListSummaryResponse(2, 0, 0, 0, 0));

        var year = await Handler().HandleAsync(projectId: Mine, periodKey: null, year: 2026, default);

        Assert.Equal(2, year.Draft);

        // ✎ g4: користувач іде і в зведення року — для «моїх» застарілих за рік.
        await _store.Received(1).SummarizeYearAsync(
            Mine, 2026, Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.Count == 1 && ids.Contains(Mine)),
            null, 7, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SummarizeAsync(default, default, default, default, default, default);

        // Місяць важить більше за рік.
        await Handler().HandleAsync(projectId: Mine, periodKey: 202603, year: 2026, default);
        await _store.Received(1).SummarizeAsync(
            Mine, 202603, Arg.Any<IReadOnlyCollection<int>?>(), null, 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Без_місяця_й_року_або_з_роком_поза_межами_422()
    {
        Profile(new AccessBuilder { UserId = 7 }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, Mine, GrantLevel.Read));

        var none = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(projectId: null, periodKey: null, year: null, default));
        Assert.Equal("err.ECR-REQ-0422.documentStateNeedsPeriod", none.Details!["messageKey"]);

        var range = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(projectId: null, periodKey: null, year: 1999, default));
        Assert.Equal("err.ECR-REQ-0422.documentYearOutOfRange", range.Details!["messageKey"]);
    }

    private GetDocumentListSummaryHandler Handler() => new(_store, _access, _user);

    private void Profile(AccessBuilder builder)
        => _access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(builder.Build());
}
