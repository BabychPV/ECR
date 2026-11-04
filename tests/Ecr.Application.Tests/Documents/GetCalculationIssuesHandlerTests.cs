// tests/Ecr.Application.Tests/Documents/GetCalculationIssuesHandlerTests.cs
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

/// <summary>
/// AN-69: <c>GET /documents/{id}/calculation-issues</c> — право <c>Document.View</c>, невидимий документ — 404,
/// рядки схованих таблиць у відповіді відсутні, копія аркуша передається сховищу як є.
/// </summary>
/// <remarks>
/// Мутація: прибрати фільтр <c>CanReadTable</c> у <see cref="GetCalculationIssuesHandler"/> — червоніє тест схованої
/// таблиці; прибрати <c>RequireVisibleAsync</c> — тест 404.
/// </remarks>
public sealed class GetCalculationIssuesHandlerTests
{
    private const long Document = 900;
    private const int Period = 202601;
    private static readonly DateTime Detected = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядки_схованої_таблиці_у_відповіді_відсутні()
    {
        var (handler, _) = Arrange(deny: true, visibleDocument: true);

        var result = await handler.HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("T1", item.TableCode);
        Assert.Equal(3, item.RowNumber);
        Assert.Equal("NoMatchingRule", item.Kind);
        Assert.Equal("M1", item.MethodologyCode);
        Assert.Equal(Detected, item.DetectedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_обмежень_віддаються_рядки_обох_таблиць()
    {
        var (handler, _) = Arrange(deny: false, visibleDocument: true);

        var result = await handler.HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Equal(["T1", "T2"], result.Items.Select(i => i.TableCode).Order().ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невидимий_документ_дає_404_і_сховище_не_читається()
    {
        var (handler, issues) = Arrange(deny: false, visibleDocument: false);

        await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(Document, Period, null, CancellationToken.None));

        await issues.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Копія_аркуша_передається_сховищу_і_повертається_у_відповіді()
    {
        var (handler, issues) = Arrange(deny: false, visibleDocument: true, copyId: 77);

        var result = await handler.HandleAsync(Document, Period, 77, CancellationToken.None);

        await issues.Received(1).ListAsync(Document, Period, 77, Arg.Any<CancellationToken>());
        Assert.All(result.Items, i => Assert.Equal(77, i.SheetCopyId));
    }

    private static (GetCalculationIssuesHandler Handler, ICalculationIssueStore Issues) Arrange(
        bool deny, bool visibleDocument, long? copyId = null)
    {
        var template = new TemplateBuilder();
        var visibleSheet = template.Sheet("S1");
        var visibleTable = template.Table(visibleSheet, "T1");
        var hiddenSheet = template.Sheet("S2");
        var hiddenTable = template.Table(hiddenSheet, "T2");
        var snapshot = template.Build();

        var builder = new AccessBuilder { UserId = 9 }
            .Permission(GetCalculationIssuesHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (deny)
        {
            builder.Deny(ResourceKind.Sheet, hiddenSheet.Id);
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(visibleDocument ? EditDecision.Allow() : EditDecision.Deny(EditDenyReason.DocumentSubmitted));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

        var issues = Substitute.For<ICalculationIssueStore>();
        issues.ListAsync(Document, Period, copyId, Arg.Any<CancellationToken>())
              .Returns(
              [
                  new CalculationIssueRow(visibleTable.Id, "T1", copyId, 3, "R3", "NoMatchingRule", "M1", Detected),
                  new CalculationIssueRow(hiddenTable.Id, "T2", copyId, 1, "R1", "NoMatchingRule", "M2", Detected),
              ]);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return (new GetCalculationIssuesHandler(issues, access, user), issues);
    }
}
