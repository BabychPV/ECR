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

/// <summary>Політика адреси джерела PI Web API: SSRF і витік службових облікових даних.</summary>
public sealed class DataSourceEndpointPolicyTests
{
    [Theory]
    [InlineData("ftp://pi.corp.local/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://pi.corp.local")]
    public void Схема_не_http_відхиляється(string address)
        => Assert.Equal(EndpointVerdict.Scheme, DataSourceEndpointPolicy.CheckAddress(address, false, null));

    [Theory]
    [InlineData("")]
    [InlineData("pi-server")]
    [InlineData("http://")]
    [InlineData("https://svc@pi.corp.local/x")]
    public void Порожня_чи_некоректна_адреса_відхиляється(string address)
        => Assert.Equal(EndpointVerdict.Malformed, DataSourceEndpointPolicy.CheckAddress(address, false, null));

    [Theory]
    [InlineData("http://localhost/piwebapi")]
    [InlineData("http://LOCALHOST:5000/")]
    [InlineData("http://a.localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.1.2.3/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f.1/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::]/")]
    [InlineData("http://224.0.0.1/")]
    public void Блоклист_loopback_linklocal_metadata_unspecified_відхиляється_у_будь_якому_режимі(string address)
    {
        Assert.Equal(EndpointVerdict.HostForbidden, DataSourceEndpointPolicy.CheckAddress(address, false, null));
        Assert.Equal(EndpointVerdict.HostForbidden, DataSourceEndpointPolicy.CheckAddress(address, true, null));
    }

    [Theory]
    [InlineData("http://10.1.2.3/piwebapi")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    public void Приватний_IP_літерал_заборонений_лише_для_Negotiate(string address)
    {
        Assert.Equal(EndpointVerdict.HostForbidden, DataSourceEndpointPolicy.CheckAddress(address, true, null));
        Assert.Equal(EndpointVerdict.Allowed, DataSourceEndpointPolicy.CheckAddress(address, false, null));
    }

    [Theory]
    [InlineData("https://pi.corp.local/piwebapi")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("https://8.8.8.8/")]
    public void Дозволені_адреси_проходять_для_Negotiate(string address)
        => Assert.Equal(EndpointVerdict.Allowed, DataSourceEndpointPolicy.CheckAddress(address, true, null));

    [Theory]
    [InlineData("https://pi.corp.local/x", EndpointVerdict.Allowed)]
    [InlineData("https://PI.Corp.Local/x", EndpointVerdict.Allowed)]
    [InlineData("https://a.b.corp2.local/x", EndpointVerdict.Allowed)]
    [InlineData("https://evilcorp.local/x", EndpointVerdict.HostNotAllowed)]
    [InlineData("https://corp2.local/x", EndpointVerdict.HostNotAllowed)]
    [InlineData("https://pi.corp.local.evil.com/x", EndpointVerdict.HostNotAllowed)]
    [InlineData("http://pi.corp.local@evil.com/x", EndpointVerdict.Malformed)]
    [InlineData("https://evil.com/pi.corp.local", EndpointVerdict.HostNotAllowed)]
    public void Allowlist_точний_збіг_і_суфікс(string address, EndpointVerdict expected)
        => Assert.Equal(expected, DataSourceEndpointPolicy.CheckAddress(address, false, ["pi.corp.local", "*.corp2.local"]));

    [Fact]
    public void Порожній_allowlist_не_обмежує()
        => Assert.Equal(
            EndpointVerdict.Allowed, DataSourceEndpointPolicy.CheckAddress("https://any.host/x", false, []));

    [Fact]
    public void Wildcard_не_збігається_з_самим_доменом()
        => Assert.Equal(
            EndpointVerdict.HostNotAllowed,
            DataSourceEndpointPolicy.CheckAddress("https://corp.local/x", false, ["*.corp.local"]));

    // ── Через обробник: розв'язання імені й ключі відмов ──

    private sealed class FakeNetwork(IReadOnlyList<string> allowed, params string[] resolved) : IEndpointNetwork
    {
        public IReadOnlyList<string> AllowedHosts { get; } = allowed;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPAddress>>([.. resolved.Select(IPAddress.Parse)]);
    }

    private static async Task<BusinessRuleException?> TryCreate(string endpoint, string? secondary, FakeNetwork net)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        var store = Substitute.For<IDataSourceStore>();
        var secrets = Substitute.For<ISecretProvider>();
        user.UserId.Returns(11);
        access.BuildProfileAsync(11, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 11 }.Permission("Integration.Manage").Build());

        var handler = new SaveDataSourceHandler(
            store, secrets, access, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), user,
            Substitute.For<IClock>(), net);

        try
        {
            await handler.CreateAsync(
                "SRC", new Dictionary<string, string> { ["uk"] = "Джерело" }, ExternalTransport.PiWebApi,
                endpoint, secondary, null, null, null, default);
            return null;
        }
        catch (BusinessRuleException e)
        {
            return e;
        }
    }

    [Theory]
    [InlineData("ftp://x/", "err.ECR-REQ-0422.dataSourceEndpointScheme")]
    [InlineData("http://169.254.169.254/", "err.ECR-REQ-0422.dataSourceEndpointHostForbidden")]
    [InlineData("pi", "err.ECR-REQ-0422.dataSourceEndpointMalformed")]
    public async Task Обробник_називає_ключ_і_поле(string address, string key)
    {
        var refused = await TryCreate(address, null, new FakeNetwork([]));

        Assert.NotNull(refused);
        Assert.Equal(key, refused.Details!["messageKey"]);
        Assert.Equal("endpoint", refused.Details!["field"]);
    }

    [Fact]
    public async Task Запасна_адреса_теж_перевіряється()
    {
        var refused = await TryCreate("https://pi.corp.local/x", "http://127.0.0.1/", new FakeNetwork([]));

        Assert.Equal("secondaryEndpoint", refused!.Details!["field"]);
    }

    [Fact]
    public async Task Ім_я_що_розв_язується_у_linklocal_відхиляється_а_приватне_ім_я_дозволене()
    {
        var bad = await TryCreate("https://pi.corp.local/x", null, new FakeNetwork([], "10.0.0.5", "169.254.169.254"));
        Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointHostForbidden", bad!.Details!["messageKey"]);

        var loop = await TryCreate("https://pi.corp.local/x", null, new FakeNetwork([], "::ffff:127.0.0.1"));
        Assert.NotNull(loop);

        var ok = await TryCreate("https://pi.corp.local/x", null, new FakeNetwork([], "10.0.0.5"));
        Assert.Null(ok);
    }

    [Fact]
    public async Task Хост_поза_allowlist_відхиляється_обробником()
    {
        var refused = await TryCreate("https://pi.other.local/x", null, new FakeNetwork(["*.corp.local"], "10.0.0.5"));

        Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointHostNotAllowed", refused!.Details!["messageKey"]);
    }
}
