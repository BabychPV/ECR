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
/// ✎ 25.11: видалення НЕ-чернетки проєкту з усіма даними (людина: видалити активні HSE401_2026, TEST_COORD_2026,
/// HSE400_GOLD через інтерфейс). Право <c>Project.DeleteWithData</c>; код і причина; архів (B69) і залежні (B80) —
/// відмова; журнал безпеки з причиною.
/// </summary>
/// <remarks>
/// Мутаційні докази (вручну, RED → GREEN): (1) у <c>PurgeProjectHandler</c> право <c>Permission</c> замінити на
/// <c>Project.Manage</c> — червоніє <c>Без_права…</c>; (2) прибрати гілку архіву в <c>BlockerKey</c> — червоніють
/// <c>Архівований…</c> і <c>Період_в_архіві…</c>; (3) прибрати <c>Dependants.Count &gt; 0</c> — <c>Залежні…</c>;
/// (4) прибрати порівняння коду — <c>Код_не_збігся…</c>; (5) прибрати перевірку причини — <c>Без_причини…</c>;
/// (6) прибрати запис журналу безпеки — <c>Активний_проєкт_видаляється…</c>.
/// </remarks>
public sealed class PurgeProjectTests
{
    private const int UserId = 9;
    private const int ProjectId = 10;
    private const string Code = "HSE401_2026";
    private static readonly DateTime Now = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

    private readonly IProjectPurgeStore _purge = Substitute.For<IProjectPurgeStore>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public PurgeProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(UserId);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        Rights(PurgeProjectHandler.Permission);
    }

    private void Rights(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = UserId }.Grant(ResourceKind.Project, ProjectId, GrantLevel.Manage);
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private void Facts(
        ProjectStatus status = ProjectStatus.Active,
        bool archiving = false,
        IReadOnlyList<int>? archived = null,
        IReadOnlyList<ProjectPurgeDependant>? dependants = null,
        IReadOnlyList<ProjectPurgeDependant>? eventMaps = null,
        IReadOnlyList<string>? warnings = null)
        => _purge.ReadFactsAsync(ProjectId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectPurgeFacts(
                Code, status, archiving, 2,
                [new ProjectPurgeDocument(101, "HSE401-A", "HSE400", 40), new ProjectPurgeDocument(102, "HSE401-B", "HSE400", 2)],
                new ProjectPurgeCounts(42, 7, 2, 1, 5, 4, 3, 12),
                [202601, 202602],
                archived ?? [],
                dependants ?? [],
                eventMaps ?? [],
                [202602],
                warnings ?? []));

    private PurgeProjectHandler Handler() => new(_purge, _gate, _access, _user, _uow, _audit, _clock);

    private static PurgeProjectRequest Request(string? code = Code, string? reason = "тестовий проєкт, дані не потрібні")
        => new(code, reason);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Активний_проєкт_видаляється_з_даними_і_журнал_безпеки_має_причину_й_підсумок()
    {
        Facts();
        _purge.PurgeAsync(ProjectId, Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>()).Returns(new ProjectPurgeResult(42, 3));

        await Handler().HandleAsync(ProjectId, Request(), CancellationToken.None);

        await _purge.Received(1).PurgeAsync(
            ProjectId, Arg.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new long[] { 101, 102 })), Arg.Any<CancellationToken>());
        await _gate.Received(1).EnterStructureAsync(101, true, Arg.Any<CancellationToken>());
        await _gate.Received(1).EnterStructureAsync(102, true, Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == PurgeProjectHandler.DeletedEventType
                && e.ChangedByUserId == UserId
                && e.DetailsJson!.Contains(Code)
                && e.DetailsJson.Contains("HSE401-A")
                && e.DetailsJson.Contains("\"cells\":42")
                && e.DetailsJson.Contains("202602")
                && e.DetailsJson.Contains("\"approvedMonthsDeletedWithoutReopen\":[202602]")
                && e.DetailsJson.Contains("\"cancelledJobs\":3")),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e => System.Text.Json.JsonDocument.Parse(e.DetailsJson!).RootElement
                .GetProperty("reason").GetString() == "тестовий проєкт, дані не потрібні"),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityId == ProjectId && r.Operation == "Delete" && r.ChangeReason != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_DeleteWithData_відмова_403_навіть_з_Project_Manage_і_грантом()
    {
        Rights("Project.Manage");
        Facts();

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(ProjectId, Request(), CancellationToken.None));
        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().PreviewAsync(ProjectId, CancellationToken.None));
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(ProjectStatus.Archived, false)]
    [InlineData(ProjectStatus.Active, true)]
    public async Task Архівований_або_той_що_архівується_проєкт_не_видаляється(ProjectStatus status, bool archiving)
    {
        Facts(status, archiving);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, Request(), CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal(PurgeProjectHandler.ArchivedKey, error.Details!["messageKey"]);
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Період_в_архіві_B69_відмова_з_переліком_періодів()
    {
        Facts(archived: [202501, 202502]);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, Request(), CancellationToken.None));

        Assert.Equal(PurgeProjectHandler.ArchivedKey, error.Details!["messageKey"]);
        Assert.Equal("202501, 202502", error.Details["periods"]);
        Assert.Equal(PurgeProjectHandler.ArchivedKey, (await Handler().PreviewAsync(ProjectId, CancellationToken.None)).Blocker);
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Залежні_документи_інших_проєктів_B80_відмова_з_переліком()
    {
        Facts(dependants: [new ProjectPurgeDependant(501, "LAND-2026", "LANDUI2026", "SourceBinding:HSE400_COMPOSITION:2026")]);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, Request(), CancellationToken.None));

        Assert.Equal(PurgeProjectHandler.DependantsKey, error.Details!["messageKey"]);
        Assert.Equal("LANDUI2026/LAND-2026", error.Details["dependants"]);
        var preview = await Handler().PreviewAsync(ProjectId, CancellationToken.None);
        Assert.Equal(PurgeProjectHandler.DependantsKey, preview.Blocker);
        Assert.Single(preview.Dependants);
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// ✎ R3 C2-b: мапа подій на ВЛАСНОМУ документі — своя причина («приберіть у Sources»), а не «читають інші проєкти».
    /// Мутація: прибрати гілку <c>EventMaps.Count &gt; 0</c> у <c>BlockerKey</c> — червоніє (відмови немає).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Мапа_подій_на_власному_документі_окрема_відмова_а_не_залежні_інших_проєктів()
    {
        Facts(eventMaps: [new ProjectPurgeDependant(101, "HSE401-A", Code, "SourceEventMap")]);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(ProjectId, Request(), CancellationToken.None));

        Assert.Equal(PurgeProjectHandler.EventMapKey, error.Details!["messageKey"]);
        Assert.Equal("HSE401-A", error.Details["documents"]);
        var preview = await Handler().PreviewAsync(ProjectId, CancellationToken.None);
        Assert.Equal(PurgeProjectHandler.EventMapKey, preview.Blocker);
        Assert.Empty(preview.Dependants);
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    /// <remarks>✎ R3 C2-g: попередження (інструменти tools/hse400) показується, але видалення НЕ блокує.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Попередження_не_блокує_видалення()
    {
        Facts(warnings: [ProjectPurgeWarnings.Hse400Tools]);
        _purge.PurgeAsync(ProjectId, Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>()).Returns(new ProjectPurgeResult(1, 0));

        var preview = await Handler().PreviewAsync(ProjectId, CancellationToken.None);
        Assert.Null(preview.Blocker);
        Assert.Equal([ProjectPurgeWarnings.Hse400Tools], preview.Warnings);

        await Handler().HandleAsync(ProjectId, Request(), CancellationToken.None);
        await _purge.Received(1).PurgeAsync(ProjectId, Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("hse401_2026")]
    [InlineData("HSE401")]
    [InlineData(null)]
    public async Task Код_не_збігся_точно_422_і_нічого_не_видалено(string? typed)
    {
        Facts();

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(ProjectId, Request(code: typed), CancellationToken.None));

        Assert.Equal("ECR-PRD-0422", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0422.purgeCodeMismatch", error.Details!["messageKey"]);
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Без_причини_422_і_нічого_не_видалено(string? reason)
    {
        Facts();

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(ProjectId, Request(reason: reason), CancellationToken.None));

        Assert.Equal("err.ECR-PRD-0422.purgeReasonRequired", error.Details!["messageKey"]);
        await _purge.DidNotReceive().PurgeAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Попередній_перегляд_віддає_документи_кількості_й_місяці_без_причини_відмови()
    {
        Facts();

        var preview = await Handler().PreviewAsync(ProjectId, CancellationToken.None);

        Assert.Equal(Code, preview.Code);
        Assert.Equal("Active", preview.Status);
        Assert.Equal(["HSE401-A", "HSE401-B"], preview.Documents.Select(d => d.BusinessKey));
        Assert.Equal(42, preview.Counts.Cells);
        Assert.Equal([202601, 202602], preview.DataMonths);
        Assert.Null(preview.Blocker);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Проєкту_немає_404()
    {
        _purge.ReadFactsAsync(ProjectId, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((ProjectPurgeFacts?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => Handler().PreviewAsync(ProjectId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(ProjectId, Request(), CancellationToken.None));
    }
}
