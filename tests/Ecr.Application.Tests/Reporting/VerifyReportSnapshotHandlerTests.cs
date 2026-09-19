// tests/Ecr.Application.Tests/Reporting/VerifyReportSnapshotHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// Перевірка зрізу (BE-17): право, грант на проєкт, 404 і саме порівняння.
/// </summary>
public sealed class VerifyReportSnapshotHandlerTests
{
    private const int Viewer = 9;
    private const int Project = 4;
    private const long Snapshot = 77;

    private readonly IReportSnapshotBuilder _snapshots = Substitute.For<IReportSnapshotBuilder>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public VerifyReportSnapshotHandlerTests()
    {
        _user.UserId.Returns(Viewer);
        _snapshots.FindProjectIdAsync(Snapshot, Arg.Any<CancellationToken>()).Returns(Project);
        Grant(GrantLevel.Read);
    }

    private VerifyReportSnapshotHandler Handler() => new(_snapshots, _access, _user);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Однакові_суми_дають_збіг_і_обидві_суми_у_відповіді()
    {
        Hashes("AB12", "AB12");

        var result = await Handler().HandleAsync(Snapshot, CancellationToken.None);

        Assert.Equal(new SnapshotVerifyResponse(true, "AB12", "AB12", "current"), result);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Збіг_лише_за_старим_форматом_це_збіг_із_позначкою_legacy()
    {
        _snapshots.VerifyAsync(Snapshot, Arg.Any<CancellationToken>())
                  .Returns(new SnapshotHashes("AB12", "CD34", "AB12"));

        var result = await Handler().HandleAsync(Snapshot, CancellationToken.None);

        Assert.Equal(new SnapshotVerifyResponse(true, "AB12", "CD34", "legacy"), result);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Незбіг_за_обома_форматами_це_незбіг()
    {
        _snapshots.VerifyAsync(Snapshot, Arg.Any<CancellationToken>())
                  .Returns(new SnapshotHashes("AB12", "CD34", "EF56"));

        var result = await Handler().HandleAsync(Snapshot, CancellationToken.None);

        Assert.False(result.Matches);
        Assert.Null(result.MatchedFormat);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожня_збережена_сума_не_збігається_і_з_порожньою_старою()
    {
        _snapshots.VerifyAsync(Snapshot, Arg.Any<CancellationToken>())
                  .Returns(new SnapshotHashes(string.Empty, "CD34", string.Empty));

        var result = await Handler().HandleAsync(Snapshot, CancellationToken.None);

        Assert.False(result.Matches);
        Assert.Null(result.MatchedFormat);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Різні_суми_дають_незбіг()
    {
        Hashes("AB12", "CD34");

        var result = await Handler().HandleAsync(Snapshot, CancellationToken.None);

        Assert.False(result.Matches);
        Assert.Equal("AB12", result.Stored);
        Assert.Equal("CD34", result.Actual);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зріз_без_збереженої_суми_не_вважається_незмінним()
    {
        Hashes(string.Empty, "CD34");

        var result = await Handler().HandleAsync(Snapshot, CancellationToken.None);

        Assert.False(result.Matches);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Неіснуючий_зріз_дає_404()
    {
        _snapshots.FindProjectIdAsync(Snapshot, Arg.Any<CancellationToken>()).Returns((int?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Snapshot, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportNotFound, error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зріз_чужого_проєкту_дає_404_і_суму_не_перераховує()
    {
        // Право є, гранта на проєкт немає — той самий клас, що й Q-239.
        _access.BuildProfileAsync(Viewer, Arg.Any<CancellationToken>())
               .Returns(Profile([ListReportSnapshotsHandler.Permission], []));

        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Snapshot, CancellationToken.None));

        await _snapshots.DidNotReceive().VerifyAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_права_перегляду_звітності_відмова()
    {
        _access.BuildProfileAsync(Viewer, Arg.Any<CancellationToken>())
               .Returns(Profile([], new() { [$"{ResourceKind.Project}:{Project}"] = GrantLevel.Read }));

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(Snapshot, CancellationToken.None));

        await _snapshots.DidNotReceive().FindProjectIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    private void Hashes(string stored, string actual)
        => _snapshots.VerifyAsync(Snapshot, Arg.Any<CancellationToken>())
                     .Returns(new SnapshotHashes(stored, actual));

    private void Grant(GrantLevel level)
        => _access.BuildProfileAsync(Viewer, Arg.Any<CancellationToken>()).Returns(Profile(
            [ListReportSnapshotsHandler.Permission],
            new() { [$"{ResourceKind.Project}:{Project}"] = level }));

    private static AccessProfile Profile(
        IReadOnlyCollection<string> permissions, Dictionary<string, GrantLevel> grants) => new()
    {
        CacheKey = "p",
        UserId = Viewer,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
        Grants = grants,
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };
}
