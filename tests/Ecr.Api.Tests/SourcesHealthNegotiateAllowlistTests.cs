using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Negotiate-джерело без PiWebApi:AllowedHosts робить health-картку джерел Degraded.</summary>
public sealed class SourcesHealthNegotiateAllowlistTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("test", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    public async Task Нове_Negotiate_джерело_з_прогалиною_показує_обидві_причини()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "health.sources.negotiateNoAllowlist", "NEG={count}")
            .Add("en", "health.sources.gapsCount", "GAP={count}");
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SourceEntityStatus(
                Id: 1, Code: "pi", DisplayName: "pi", EntityPath: "pi",
                Transport: "PiWebApi", IsActive: true,
                LastRun: null, OldestGap: null,
                DataSourceId: 7, DataSourceCode: "PI",
                OnMissingInSource: RegistryMissingPolicy.MarkOrphaned,
                ValidFromAttribute: null, ValidToAttribute: null, ValidToInclusive: false),
        ]);
        store.FindDataSourceAsync(7, Arg.Any<CancellationToken>()).Returns(new DataSource(
            EcrCode.Create("PI"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI" }),
            ExternalTransport.PiWebApi,
            "https://pi.corp",
            "DataSource.PI"));

        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find("DataSource.PI").Returns("Negotiate");
        var network = Substitute.For<IEndpointNetwork>();
        network.AllowedHosts.Returns([]);

        var result = await new SourcesHealthCheck(store, catalog, user, secrets, network)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("GAP=1", result.Description);
        Assert.Contains("NEG=1", result.Description);
    }

    [Theory]
    [InlineData(null, false, HealthStatus.Degraded)]
    [InlineData("Negotiate", false, HealthStatus.Degraded)]
    [InlineData("Basic dXNlcjpwYXNz", false, HealthStatus.Healthy)]
    [InlineData("Negotiate", true, HealthStatus.Healthy)]
    public async Task Negotiate_БезAllowlist_Degraded(string? secret, bool allowlist, HealthStatus expected)
    {
        var catalog = new FakeUiStringCatalog().Add("en", "health.sources.negotiateNoAllowlist", "NEG={count}");
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        var store = Substitute.For<ICollectionStore>();
        store.ListSourceEntitiesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SourceEntityStatus(
                Id: 1, Code: "pi", DisplayName: "pi", EntityPath: "pi",
                Transport: "PiWebApi", IsActive: true,
                LastRun: new CollectionRunStatus(DateTime.UtcNow, "Succeeded", 1), OldestGap: null,
                DataSourceId: 7, DataSourceCode: "PI",
                OnMissingInSource: RegistryMissingPolicy.MarkOrphaned,
                ValidFromAttribute: null, ValidToAttribute: null, ValidToInclusive: false),
        ]);
        store.FindDataSourceAsync(7, Arg.Any<CancellationToken>()).Returns(new DataSource(
            EcrCode.Create("PI"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI" }),
            ExternalTransport.PiWebApi,
            "https://pi.corp",
            "DataSource.PI"));

        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find("DataSource.PI").Returns(secret);

        var network = Substitute.For<IEndpointNetwork>();
        network.AllowedHosts.Returns(allowlist ? ["pi.corp"] : []);

        var result = await new SourcesHealthCheck(store, catalog, user, secrets, network)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(expected, result.Status);

        if (expected == HealthStatus.Degraded)
        {
            Assert.Equal("NEG=1", result.Description);
        }
    }
}
