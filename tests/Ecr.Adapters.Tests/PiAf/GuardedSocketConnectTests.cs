using System.Net;
using Ecr.Adapters.PiAf;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>DNS-rebinding: IP перевіряється при підключенні, а не лише при збереженні адреси.</summary>
public sealed class GuardedSocketConnectTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task ІмяРозв_язалосьУЗаборонену_ВідмоваДоВідкриттяСокета(string ip)
    {
        var opened = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await GuardedSocketConnect.ConnectAsync(
            "pi.corp",
            443,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(ip) }),
            (_, _, _) => { opened++; return Task.FromResult<Stream>(new MemoryStream()); },
            CancellationToken.None));

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task ОднаЗаборонена_СередДозволених_ВідмоваЦілком()
    {
        var opened = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await GuardedSocketConnect.ConnectAsync(
            "pi.corp",
            443,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3"), IPAddress.Parse("169.254.169.254") }),
            (_, _, _) => { opened++; return Task.FromResult<Stream>(new MemoryStream()); },
            CancellationToken.None));

        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.5.5")]
    [InlineData("203.0.113.7")]
    public async Task ПриватніІПублічні_ДляІменіДозволені(string ip)
    {
        IPAddress? opened = null;

        await using var stream = await GuardedSocketConnect.ConnectAsync(
            "pi.corp",
            443,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(ip) }),
            (address, _, _) => { opened = address; return Task.FromResult<Stream>(new MemoryStream()); },
            CancellationToken.None);

        Assert.Equal(IPAddress.Parse(ip), opened);
    }

    [Theory]
    [InlineData(PiWebApiAuthMode.Negotiate)]
    [InlineData(PiWebApiAuthMode.Basic)]
    public async Task ПервиннийОбробник_НеЗ_єднуєтьсяЗLoopback_НавітьЯкЩоСлухачЄ(PiWebApiAuthMode mode)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pending = listener.AcceptTcpClientAsync();

        using var http = new HttpClient(PiWebApiAuthentication.CreatePrimaryHandler(mode));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync(new Uri($"http://localhost:{port}/piwebapi")));

        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task НульАдрес_Відмова()
        => await Assert.ThrowsAsync<HttpRequestException>(async () => await GuardedSocketConnect.ConnectAsync(
            "pi.corp",
            443,
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            (_, _, _) => Task.FromResult<Stream>(new MemoryStream()),
            CancellationToken.None));
}
