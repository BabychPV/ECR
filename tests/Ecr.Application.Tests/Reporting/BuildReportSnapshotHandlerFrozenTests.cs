// tests/Ecr.Application.Tests/Reporting/BuildReportSnapshotHandlerFrozenTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// R7-Y7 / Y7-01: побудова за періодом із поданим поточним зрізом відмовляє <c>409</c>
/// одразу, а не відповідає <c>202</c> і ставить задачу, яка однаково впаде з тим самим вердиктом.
/// </summary>
/// <remarks>
/// Тести CI, локально не запускались. Мутація: прибрати перевірку
/// <c>FindFreshFrozenCurrentAsync</c> у <c>BuildReportSnapshotHandler.HandleAsync</c> —
/// перший тест червоний (задачу поставлено, винятку немає).
/// </remarks>
public sealed class BuildReportSnapshotHandlerFrozenTests
{
    private const int Builder = 9;
    private const int Project = 4;
    private const int Period = 202603;
    private const int Version = 31;
    private const long Frozen = 501;

    private readonly IReportDefinitionStore _definitions = Substitute.For<IReportDefinitionStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IReportSnapshotBuilder _snapshots = Substitute.For<IReportSnapshotBuilder>();

    public BuildReportSnapshotHandlerFrozenTests()
    {
        _user.UserId.Returns(Builder);
        _access.BuildProfileAsync(Builder, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = Builder,
            SecurityStamp = "s",
            Permissions = new HashSet<string>([BuildReportSnapshotHandler.Permission], StringComparer.Ordinal),
            Grants = new Dictionary<string, GrantLevel> { [$"{ResourceKind.Project}:{Project}"] = GrantLevel.Read },
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });
        _definitions.FindCurrentVersionAsync("IEC", Arg.Any<CancellationToken>())
                    .Returns(new ReportVersionRef(Version, ReportDefinitionSpec.RulesJson(null)));
        _jobs.EnqueueAsync<IReportSnapshotJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
             .Returns("job-1");
    }

    private BuildReportSnapshotHandler Handler() => new(_definitions, _jobs, _access, _user, _snapshots);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Поданий_поточний_зріз_дає_409_одразу_і_задача_не_ставиться()
    {
        _snapshots.FindFreshFrozenCurrentAsync(Version, Project, new PeriodKey(Period), Arg.Any<CancellationToken>())
                  .Returns(Frozen);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync("IEC", Project, Period, parameters: null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportImmutable, ex.ErrorCode);
        Assert.Equal("err.ECR-RPT-0409.periodSubmittedRebuild", ex.Details!["messageKey"]);
        Assert.Equal("501", ex.Details["snapshotId"]);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IReportSnapshotJob>(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Без_поданого_зрізу_побудова_ставиться_в_чергу()
    {
        _snapshots.FindFreshFrozenCurrentAsync(Version, Project, new PeriodKey(Period), Arg.Any<CancellationToken>())
                  .Returns((long?)null);

        var jobId = await Handler().HandleAsync("IEC", Project, Period, parameters: null, CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    /// <summary>Відмова гонки в побудові (R7-Y8 / Y8-01) — та сама: код і ключ тексту.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Відмова_для_задачі_несе_ECR_RPT_0409_і_ключ_тексту()
    {
        var ex = BuildReportSnapshotHandler.FrozenRefusal(Frozen, Project, periodKey: null);

        Assert.Equal(ErrorCodes.ReportImmutable, ex.ErrorCode);
        Assert.Equal(ErrorCodes.ReportImmutable, SafeErrorText.CodeOf(ex));
        Assert.Equal("err.ECR-RPT-0409.periodSubmittedRebuild", ex.Details!["messageKey"]);
    }
}
