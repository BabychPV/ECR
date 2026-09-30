using System.Net;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>Межа розміру тіла відповіді PI Web API: завелика відповідь — відмова збору без повторів.</summary>
public sealed class PiWebApiResponseLimitTests
{
    private const string Small = "{\"Items\":[]}";

    [Fact]
    public async Task ТілоБільшеМежі_БезContentLength_ВідмоваЗКлючемБезПовторів()
    {
        var handler = new CountingHandler(() => new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Padded(200)))));
        var sut = Create(handler, limit: 100);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => sut.ReadAsync(Request(), CancellationToken.None));

        Assert.Equal("ECR-INT-0503", ex.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.piWebApiResponseTooLarge", ex.Details!["messageKey"]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ContentLengthБільшеМежі_ВідмоваЗКлюч()
    {
        var handler = new CountingHandler(() =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Padded(200)));
            return content;
        });
        var sut = Create(handler, limit: 100);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => sut.ReadAsync(Request(), CancellationToken.None));

        Assert.Equal("err.ECR-INT-0503.piWebApiResponseTooLarge", ex.Details!["messageKey"]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ТілоВМежах_ЧитаєтьсяЯкЗвичайно()
    {
        var handler = new CountingHandler(() => new StringContent(Small, Encoding.UTF8, "application/json"));
        var sut = Create(handler, limit: 10_000);

        var result = await sut.ReadAsync(Request(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void ТиповаМежа_50МБ() => Assert.Equal(50L * 1024 * 1024, PiWebApiDataSource.DefaultMaxResponseBytes);

    private static string Padded(int length) => "{\"Items\":[],\"Pad\":\"" + new string('x', length) + "\"}";

    private static CollectionRequest Request()
        => new(1, 42, "tag", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, MaxPoints: 5_000);

    private static PiWebApiDataSource Create(HttpMessageHandler handler, long limit)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://pi.example",
            "PiAf.Primary"));
        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns((string?)null);

        return new PiWebApiDataSource(new HttpClient(handler), store, secrets) { MaxResponseBytes = limit };
    }

    private sealed class CountingHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
        }
    }
}
