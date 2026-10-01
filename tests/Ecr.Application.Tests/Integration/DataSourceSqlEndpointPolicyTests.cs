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
    [InlineData("metadata")] // ent4 P3-4: голе коротке ім'я легітимне; на GCP його ловить розв'язання (тест нижче).
    [InlineData("tcp:af.corp.local,5461")]
    [InlineData("np:\\\\af-server\\pipe\\sql\\query")]
    [InlineData("Driver={PI SQL Client};Server=af.corp.local;Integrated Security=SSPI")]
    [InlineData("Driver={PI SQL Client};AF Server=10.1.2.3;AF Database=ECR")]
    [InlineData("DSN=PiSqlEcr")]
    public void Допустимі_формати_сервера_проходять(string address)
        => Assert.Equal(EndpointVerdict.Allowed, DataSourceEndpointPolicy.CheckSqlServerAddress(address));

    /// <summary>
    /// ent4 P2-1: адаптер читає адресу як рядок з'єднання ODBC — політика мусить дивитися на
    /// сервер(и) всередині нього, у будь-якій формі, а не на рядок цілком.
    /// </summary>
    [Theory]
    [InlineData("tcp:169.254.169.254,80")]
    [InlineData("TCP:169.254.169.254")]
    [InlineData("tcp:metadata.google.internal")]
    [InlineData("tcp:[fe80::1],1433")]
    [InlineData("np:\\\\169.254.169.254\\pipe\\sql\\query")]
    [InlineData("\\\\169.254.169.254\\pipe\\sql\\query")]
    [InlineData("Driver={PI SQL Client};Server=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=metadata.google.internal;Integrated Security=SSPI")]
    [InlineData("Driver={PI SQL Client};Server=tcp:169.254.169.254,80")]
    [InlineData("Driver={PI SQL Client};Server={tcp:169.254.169.254,80}")]
    [InlineData("Driver={PI SQL Client};Data Source=169.254.169.254\\PIAF")]
    [InlineData("Driver={PI SQL Client};Address=169.254.169.254,1433")]
    [InlineData("Driver={PI SQL Client};Addr=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Network Address=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};AF Server=169.254.169.254;AF Database=ECR")]
    [InlineData("Driver={PI SQL Client};Host=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=af.corp.local;Failover Partner=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};server = 169.254.169.254 ;")]
    [InlineData("Driver={PI SQL Client};Server=af.corp.local;Server=169.254.169.254")]
    // ent4 (Розробка): додаткові обходи ODBC — числові форми IPv4, lpc:, лапки, ;;, IPv4-mapped, \instance.
    [InlineData("2852039166")]
    [InlineData("0xA9FEA9FE")]
    [InlineData("0251.0376.0251.0376")]
    [InlineData("0xA9.0xFE.0xA9.0xFE")]
    [InlineData("169.254.43518")]
    [InlineData("169.16689662")]
    [InlineData("Driver={PI SQL Client};Server=2852039166")]
    [InlineData("Driver={PI SQL Client};Server=0xA9FEA9FE,1433")]
    [InlineData("Driver={PI SQL Client};Server=0251.0376.0251.0376")]
    [InlineData("lpc:169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=lpc:169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=np:\\\\169.254.169.254\\pipe\\sql\\query")]
    [InlineData("Driver={PI SQL Client};Server=tcp:169.254.169.254,1433")]
    [InlineData("Driver={PI SQL Client};Server=[fe80::1]")]
    [InlineData("Driver={PI SQL Client};Server=[fe80::1]:1433")]
    [InlineData("Driver={PI SQL Client};Server=::ffff:169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=[::ffff:169.254.169.254]:80")]
    [InlineData("Driver={PI SQL Client};Server=169.254.169.254\\instance")]
    [InlineData("Driver={PI SQL Client};;Server=169.254.169.254;;")]
    [InlineData("Driver={PI SQL Client};Server=\"169.254.169.254\"")]
    [InlineData("Driver={PI SQL Client};Server='169.254.169.254'")]
    [InlineData("Driver={PI SQL Client};SERVER=169.254.169.254;Database=x")]
    [InlineData("Server=169.254.169.254;Database=x")]
    [InlineData("Server=localhost;Server=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=169.254.169.254.")]
    [InlineData("Driver={PI SQL Client};Server= 169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=tcp: 169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=\\\\169.254.169.254\\pipe\\x")]
    [InlineData("Driver={PI SQL Client};Server=169.254.169.254:1433")]
    [InlineData("Driver={PI SQL Client};Server=[::ffff:a9fe:a9fe]")]
    [InlineData("Driver={PI SQL Client};Server=::ffff:a9fe:a9fe")]
    [InlineData("Driver={PI SQL Client};Server=0xa9fea9fe")]
    [InlineData("Driver={PI SQL Client};Server=metadata.google.internal.")]
    [InlineData("Driver={PI SQL Client};Server=fe80::1%eth0")]
    [InlineData("fe80::1%25eth0")]
    public void Обхід_через_рядок_з_єднання_чи_префікс_протоколу_відхиляється(string address)
        => Assert.Equal(EndpointVerdict.HostForbidden, DataSourceEndpointPolicy.CheckSqlServerAddress(address));

    [Theory]
    [InlineData("Driver={PI SQL Client;Server=af")]
    [InlineData("Driver={PI SQL Client};Server={af")]
    public void Рядок_з_єднання_що_не_розбирається_некоректний(string address)
        => Assert.Equal(EndpointVerdict.Malformed, DataSourceEndpointPolicy.CheckSqlServerAddress(address));

    [Theory]
    [InlineData("tcp:169.254.169.254,80", "169.254.169.254")]
    [InlineData("np:\\\\af-server\\pipe\\sql\\query", "af-server")]
    [InlineData("af-server\\PIAF", "af-server")]
    [InlineData("[fe80::1]:5461", "fe80::1")]
    [InlineData("fe80::1", "fe80::1")]
    public void Хост_сервера_без_протоколу_порту_й_екземпляра(string address, string host)
        => Assert.Equal(host, DataSourceEndpointPolicy.SqlHostOf(address));

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
    [InlineData("Driver={PI SQL Client};Server=169.254.169.254", "err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal")]
    [InlineData("Driver={PI SQL Client;Server=af", "err.ECR-REQ-0422.dataSourceEndpointMalformed")]
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

    /// <summary>ent4 P2-1/P3-4: ім'я всередині рядка з'єднання теж розв'язується й перевіряється.</summary>
    [Theory]
    [InlineData("Driver={PI SQL Client};Server=af-server;Integrated Security=SSPI")]
    [InlineData("Driver={PI SQL Client};AF Server=tcp:af-server,5461")]
    [InlineData("metadata")]
    public async Task Ім_я_в_рядку_з_єднання_що_розв_язується_у_linklocal_відхиляється(string endpoint)
    {
        Assert.NotNull(await TryCreate(endpoint, new FakeNetwork([], "169.254.169.254")));
        Assert.Null(await TryCreate(endpoint, new FakeNetwork([], "10.0.0.5")));
    }

    [Fact]
    public async Task Хост_поза_allowlist_лише_попередження_не_відмова()
        => Assert.Null(await TryCreate("af.other.local", new FakeNetwork(["*.corp.local"], "10.0.0.5")));
}
