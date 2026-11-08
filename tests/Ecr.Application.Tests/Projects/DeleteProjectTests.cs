using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>
/// Видалення проєкту (UAT 08.11, сценарій c): лише чернетка без документів і без історії; грант Manage; аудит.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати перевірку <c>Status != Draft</c> у <c>DeleteProjectHandler.Refuse</c> — червоніє
/// <c>Активний_проєкт_не_видаляється…</c>; прибрати <c>DocumentCount &gt; 0</c> — <c>Чернетка_з_документами…</c>;
/// прибрати перевірку гранта — <c>Без_гранта_Manage…</c>; прибрати запис журналу — <c>Порожня_чернетка…</c>.
/// </remarks>
public sealed class DeleteProjectTests
{
    private const int UserId = 9;
    private const int ProjectId = 10;
    private static readonly DateTime Now = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    private readonly IProjectStore _projects = Substitute.For<IProjectStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public DeleteProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(UserId);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        Grant(GrantLevel.Manage);
    }

    private void Grant(GrantLevel level)
        => _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, ProjectId, level)
                .Build());

    private void Facts(ProjectStatus status, int documents = 0, int history = 0)
        => _projects.GetDeletionFactsAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns(new ProjectDeletionFacts("PAVLO_TEST", status, 2, documents, history));

    private DeleteProjectHandler Handler() => new(_projects, _access, _user, _uow, _audit, _clock);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Порожня_чернетка_видаляється_і_пише_аудит()
    {
        Facts(ProjectStatus.Draft);

        await Handler().HandleAsync(ProjectId, CancellationToken.None);

        await _projects.Received(1).DeleteAsync(ProjectId, Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "Project" && r.Operation == "Delete" && r.EntityId == ProjectId
                && r.ChangedByUserId == UserId && r.OldJson!.Contains("PAVLO_TEST")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Активний_проєкт_не_видаляється_і_відмова_називає_стан_і_документи()
    {
        Facts(ProjectStatus.Active, documents: 92);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0409.deleteNotDraft", error.Details!["messageKey"]);
        Assert.Equal("Active", error.Details["status"]);
        Assert.Equal("92", error.Details["documentCount"]);
        await _projects.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Чернетка_з_документами_не_видаляється()
    {
        Facts(ProjectStatus.Draft, documents: 1);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, CancellationToken.None));

        Assert.Equal("err.ECR-PRD-0409.deleteHasDocuments", error.Details!["messageKey"]);
        await _projects.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Чернетка_з_історією_не_видаляється()
    {
        Facts(ProjectStatus.Draft, history: 3);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, CancellationToken.None));

        Assert.Equal("err.ECR-PRD-0409.deleteHasHistory", error.Details!["messageKey"]);
        await _projects.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_Manage_на_проєкт_відмова_403_і_нічого_не_видалено()
    {
        Grant(GrantLevel.Write);
        Facts(ProjectStatus.Draft);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(ProjectId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        await _projects.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Проєкту_немає_404()
    {
        _projects.GetDeletionFactsAsync(ProjectId, Arg.Any<CancellationToken>()).Returns((ProjectDeletionFacts?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(ProjectId, CancellationToken.None));
    }
}
