using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// FP-1 (прохід «від першого обличчя» 2026-10-11, P1): роль «Переглядач звітів»
/// (<c>ReportViewer</c>: <c>Report.ViewRegulatory</c>, без <c>Document.View</c>) читає перелік
/// проєктів для екрана зрізів, а не отримує 403 на <c>GET /projects</c>.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути в <see cref="ListProjectsHandler"/> перевірку лише
/// <c>Document.View</c> (прибрати <c>|| ReportPermission</c> і умову входу) — червоніють
/// обидва тести про видимість проєктів (виняток «потрібне право Document.View»).
/// </remarks>
public sealed class ListProjectsReportViewerTests
{
    private const int UserId = 9;

    private static readonly int[] ReportViewerProjects = [10, 11];
    private static readonly int[] OperatorProjects = [10];

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IProjectStore _projects = Substitute.For<IProjectStore>();

    public ListProjectsReportViewerTests()
    {
        _user.UserId.Returns(UserId);

        _projects
            .ListAsync(Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ProjectSummary>([], null, 0));
    }

    private void Profile(AccessBuilder builder)
        => _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Переглядач_звітів_без_Document_View_бачить_лише_проєкти_з_грантом_Read()
    {
        Profile(new AccessBuilder { UserId = UserId }
            .Permission("Report.ViewRegulatory")
            .Grant(ResourceKind.Project, 10, GrantLevel.Read)
            .Grant(ResourceKind.Project, 11, GrantLevel.Read)

            // Заборона виграє на будь-якому рівні (ФВ-6.6).
            .Grant(ResourceKind.Project, 12, GrantLevel.Read)
            .Deny(ResourceKind.Project, 12)

            // Не проєкт і без гранта — у перелік не потрапляє.
            .Grant(ResourceKind.Sheet, 20, GrantLevel.Read));

        var handler = new ListProjectsHandler(_projects, _access, _user);

        await handler.HandleAsync(new CursorRequest(), CancellationToken.None);

        await _projects.Received(1).ListAsync(
            Arg.Any<CursorRequest>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(ReportViewerProjects)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Оператор_із_Document_View_бачить_перелік_як_і_раніше()
    {
        Profile(new AccessBuilder { UserId = UserId }
            .Permission("Document.View")
            .Grant(ResourceKind.Project, 10, GrantLevel.Read));

        var handler = new ListProjectsHandler(_projects, _access, _user);

        await handler.HandleAsync(new CursorRequest(), CancellationToken.None);

        await _projects.Received(1).ListAsync(
            Arg.Any<CursorRequest>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(OperatorProjects)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Без_жодного_з_двох_прав_перелік_відмовляє_403()
    {
        // ⚠ Розширення не відкриває перелік кожному: роль без `Document.View` і без
        // `Report.ViewRegulatory` (наприклад, лише `Calculation.View`) і далі отримує відмову.
        Profile(new AccessBuilder { UserId = UserId }
            .Permission("Calculation.View")
            .Grant(ResourceKind.Project, 10, GrantLevel.Read));

        var handler = new ListProjectsHandler(_projects, _access, _user);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(new CursorRequest(), CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.Equal("Document.View", error.Details!["permission"]);
    }
}
