using System.Net;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>Політика адреси джерела PiSqlClient: ім'я сервера, а не URL; link-local заборонено.</summary>
public sealed class DataSourceSqlEndpointPolicyTests
{
    [Theory]
    [InlineData("ftp://af.corp.local")]
    [InlineData("http://af.corp.local")]
    [InlineData("https://af.corp.local:5461")]
    [InlineData("tcp://af")]
    public void Адреса_зі_схемою_відхиляється(string address)
        => Assert.Equal(EndpointVerdict.Scheme, DataSourceEndpointPolicy.CheckSqlServerAddress(address));

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1,5461")]
    [InlineData("169.254.1.1:5461")]
    [InlineData("fe80::1")]
    [InlineData("[fe80::1]:5461")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("metadata.google.internal")]
    [InlineData("METADATA.GOOGLE.INTERNAL.")]
    [InlineData("metadata.google.internal,1433")]
    [InlineData("metadata.google.internal:1433")]
    [InlineData("metadata.google.internal\\PIAF")]
    [InlineData("metadata.goog")]
    [InlineData("metadata")]
    public void LinkLocal_літерал_відхиляється(string address)
        => Assert.Equal(EndpointVerdict.HostForbidden, DataSourceEndpointPolicy.CheckSqlServerAddress(address));

    [Theory]
    [InlineData("af-server")]
    [InlineData("af.corp.local")]
    [InlineData("af-server\\PIAF")]
    [InlineData("af-server,5461")]
    [InlineData("af.corp.local:5461")]
    [InlineData("10.1.2.3")]
    [InlineData("10.1.2.3,5461")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost\\SQLEXPRESS")]
    [InlineData("[::1]:5461")]
    [InlineData("192.168.0.9:5461")]
    [InlineData("metadata.corp.local")]
    [InlineData("google.internal")]
    public void Допустимі_формати_сервера_проходять(string address)
        => Assert.Equal(EndpointVerdict.Allowed, DataSourceEndpointPolicy.CheckSqlServerAddress(address));

    private sealed class FakeNetwork(IReadOnlyList<string> allowed, params string[] resolved) : IEndpointNetwork
    {
        public IReadOnlyList<string> AllowedHosts { get; } = allowed;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPAddress>>([.. resolved.Select(IPAddress.Parse)]);
    }

    private static async Task<BusinessRuleException?> TryCreate(string endpoint, FakeNetwork net)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(11);
        access.BuildProfileAsync(11, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 11 }.Permission("Integration.Manage").Build());

        var handler = new SaveDataSourceHandler(
            Substitute.For<IDataSourceStore>(), Substitute.For<ISecretProvider>(), access,
            Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), user, Substitute.For<IClock>(), net);

        try
        {
            await handler.CreateAsync(
                "SQL1", new Dictionary<string, string> { ["uk"] = "AF" }, ExternalTransport.PiSqlClient,
                endpoint, null, null, null, null, default);
            return null;
        }
        catch (BusinessRuleException e)
        {
            return e;
        }
    }

    [Theory]
    [InlineData("https://af.corp.local", "err.ECR-REQ-0422.dataSourceEndpointSqlScheme")]
    [InlineData("169.254.169.254", "err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal")]
    public async Task Обробник_відмовляє_422_з_ключем_і_полем(string address, string key)
    {
        var refused = await TryCreate(address, new FakeNetwork([]));

        Assert.NotNull(refused);
        Assert.Equal(key, refused.Details!["messageKey"]);
        Assert.Equal("endpoint", refused.Details!["field"]);
    }

    [Fact]
    public async Task Ім_я_що_розв_язується_у_linklocal_відхиляється_loopback_і_приватне_дозволені()
    {
        Assert.NotNull(await TryCreate("af-server", new FakeNetwork([], "169.254.169.254")));
        Assert.Null(await TryCreate("af-server\\PIAF", new FakeNetwork([], "127.0.0.1", "10.0.0.5")));
        Assert.Null(await TryCreate("af-server,5461", new FakeNetwork([])));
    }

    [Fact]
    public async Task Хост_поза_allowlist_лише_попередження_не_відмова()
        => Assert.Null(await TryCreate("af.other.local", new FakeNetwork(["*.corp.local"], "10.0.0.5")));
}
