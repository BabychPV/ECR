using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>D5: прогалини health-картки джерел рахуються унікальними сутностями, без подвоєння.</summary>
public sealed class SourcesHealthGapCountTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("test", Substitute.For<IHealthCheck>(), null, null),
    };

    private static SourceEntityStatus Entity(int id, DateTime? oldestGap)
        => new(
            Id: id, Code: $"e{id}", DisplayName: $"e{id}", EntityPath: $"e{id}",
            Transport: "PiSqlClient", IsActive: true,
            LastRun: null, OldestGap: oldestGap,
            DataSourceId: 7, DataSourceCode: "PI",
            OnMissingInSource: RegistryMissingPolicy.MarkOrphaned,
            ValidFromAttribute: null, ValidToAttribute: null, ValidToInclusive: false);

    private static async Task<HealthCheckResult> RunAsync(params SourceEntityStatus[] entities)
    {
        var catalog = new FakeUiStringCatalog().Add("en", "health.sources.gapsCount", "GAP={count}");
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");
        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns(entities);

        return await new SourcesHealthCheck(store, catalog, user)
            .CheckHealthAsync(Context, CancellationToken.None);
    }

    [Fact]
    public async Task Дві_активні_сутності_без_жодного_запуску_дають_2()
    {
        var result = await RunAsync(Entity(1, null), Entity(2, null));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("GAP=2", result.Description);
        Assert.Equal(2, result.Data["sourcesWithGaps"]);
    }

    [Fact]
    public async Task Сутність_із_прогалиною_що_ще_не_запускалась_рахується_один_раз()
    {
        var result = await RunAsync(Entity(1, DateTime.UtcNow.AddDays(-3)), Entity(2, null));

        Assert.Equal("GAP=2", result.Description);
        Assert.Equal(2, result.Data["sourcesWithGaps"]);
    }
}
