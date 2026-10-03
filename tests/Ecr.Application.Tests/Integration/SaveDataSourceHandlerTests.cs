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

/// <summary>
/// L3-05 (<c>D-279</c>, HU-11 Q4=A): джерело типу <c>Sql</c> проходить ту саму політику адреси, що й
/// PiSqlClient (<c>D-245</c>), плюс заборонені параметри SqlClient.
/// </summary>
/// <remarks>
/// ⛔ Що було: <c>ValidateAsync</c> мав гілки лише для PiWebApi і PiSqlClient, тож
/// <c>Server=169.254.169.254;Integrated Security=true</c> для Sql зберігався, і збір ішов службовим
/// обліковим записом на metadata-адресу; <c>AttachDBFilename=\\host\share\x.mdf</c> — теж.
/// </remarks>
public sealed class SaveDataSourceHandlerTests
{
    [Fact]
    public async Task Create_Sql_LinkLocal_422()
    {
        var refused = await TryCreate("Server=169.254.169.254;Integrated Security=true", new FakeNetwork());

        Assert.NotNull(refused);
        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal", refused.Details!["messageKey"]);
        Assert.Equal("endpoint", refused.Details!["field"]);
    }

    /// <summary>Обходи, які перелічив рецензент: синоніми ключа, префікси, числові форми, Failover Partner.</summary>
    [Theory]
    [InlineData("Data Source=169.254.169.254;Integrated Security=true")]
    [InlineData("Address=tcp:169.254.169.254,1433")]
    [InlineData("Addr=169.254.169.254\\SQL")]
    [InlineData("Network Address=np:\\\\169.254.169.254\\pipe\\sql\\query")]
    [InlineData("server = 2852039166 ;Integrated Security=true")]
    [InlineData("Server=0xA9FEA9FE")]
    [InlineData("Server=0251.0376.0251.0376")]
    [InlineData("Server=169.254.43518")]
    [InlineData("Server=[fe80::1],1433")]
    [InlineData("Server=::ffff:169.254.169.254")]
    [InlineData("Server=admin:169.254.169.254")]
    [InlineData("Server=lpc:169.254.169.254")]
    [InlineData("Server=metadata.google.internal")]
    [InlineData("Server=\"169.254.169.254\"")]
    [InlineData("Server=flert;Failover Partner=169.254.169.254")]
    [InlineData("Server=flert;Server=169.254.169.254")]
    [InlineData("169.254.169.254")]
    public async Task Create_Sql_обхід_LinkLocal_422(string endpoint)
    {
        var refused = await TryCreate(endpoint, new FakeNetwork());

        Assert.NotNull(refused);
        Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal", refused.Details!["messageKey"]);
    }

    [Theory]
    [InlineData("Server=flert;AttachDBFilename=\\\\attacker\\share\\x.mdf")]
    [InlineData("Server=flert;AttachDbFileName=C:\\x.mdf")]
    [InlineData("Server=flert;Extended Properties=\\\\attacker\\share\\x.mdf")]
    [InlineData("Server=flert;Initial File Name=x.mdf")]
    [InlineData("Server=flert;User Instance=true")]
    [InlineData("Server=flert;Enclave Attestation Url=https://169.254.169.254/attest")]
    [InlineData("Server=flert;Server Certificate=\\\\attacker\\share\\c.cer")]
    public async Task Create_Sql_заборонений_параметр_422(string endpoint)
    {
        var refused = await TryCreate(endpoint, new FakeNetwork());

        Assert.NotNull(refused);
        Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointSqlForbiddenOption", refused.Details!["messageKey"]);
        Assert.Equal("endpoint", refused.Details!["field"]);
    }

    [Fact]
    public async Task Create_Sql_запасна_адреса_теж_перевіряється()
    {
        var refused = await TryCreate("Server=flert", new FakeNetwork(), "Server=169.254.169.254");

        Assert.NotNull(refused);
        Assert.Equal("secondaryEndpoint", refused.Details!["field"]);
    }

    [Fact]
    public async Task Create_Sql_ім_я_що_розв_язується_у_LinkLocal_422_приватне_дозволене()
    {
        Assert.NotNull(await TryCreate("Server=flert;Database=Vol", new FakeNetwork("169.254.169.254")));
        Assert.NotNull(await TryCreate("Server=flert;Failover Partner=spare", new FakeNetwork("fe80::1")));
        Assert.Null(await TryCreate("Server=flert;Database=Vol", new FakeNetwork("10.0.0.5", "127.0.0.1")));
    }

    [Theory]
    [InlineData("Server=flert;Database=Vol;Integrated Security=true")]
    [InlineData("Data Source=tcp:flert.corp.local,1433;Initial Catalog=Vol")]
    [InlineData("Server=localhost\\SQLEXPRESS;TrustServerCertificate=true")]
    [InlineData("Server=10.1.2.3,1433;User Instance=false")]
    [InlineData("Server=flert;Server Certificate=C:\\certs\\flert.cer")]
    public async Task Create_Sql_звичайний_рядок_з_єднання_проходить(string endpoint)
        => Assert.Null(await TryCreate(endpoint, new FakeNetwork("10.0.0.5")));

    private sealed class FakeNetwork(params string[] resolved) : IEndpointNetwork
    {
        public IReadOnlyList<string> AllowedHosts { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPAddress>>([.. resolved.Select(IPAddress.Parse)]);
    }

    private static async Task<BusinessRuleException?> TryCreate(
        string endpoint, FakeNetwork net, string? secondary = null)
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
                "FLERT", new Dictionary<string, string> { ["uk"] = "FLERT" }, ExternalTransport.Sql,
                endpoint, secondary, null, null, null, default);
            return null;
        }
        catch (BusinessRuleException e)
        {
            return e;
        }
    }
}
