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

        var ex = await Assert.ThrowsAsync<SourceResponseTooLargeException>(() => sut.ReadAsync(Request(), CancellationToken.None));

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

        var ex = await Assert.ThrowsAsync<SourceResponseTooLargeException>(() => sut.ReadAsync(Request(), CancellationToken.None));

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

    /// <summary>
    /// I1-05: межа спрацьовує під час ЧИТАННЯ потоку, а не після того, як <c>HttpClient</c> уже зібрав усе тіло в пам'ять.
    /// </summary>
    /// <remarks>
    /// Мутація: повернути <c>client.SendAsync(message, ct)</c> (типове <c>ResponseContentRead</c>) - <c>HttpClient</c>
    /// вичитує весь мегабайт до перевірки межі, тест червоний.
    /// </remarks>
    [Fact]
    public async Task I1_05_тіло_без_ContentLength_не_вичитується_повністю_до_перевірки_межі()
    {
        const int total = 1_000_000;
        var stream = new ChunkedStream(total, chunk: 1_000);
        var handler = new CountingHandler(() => new StreamContent(stream));
        var sut = Create(handler, limit: 100);

        await Assert.ThrowsAsync<SourceResponseTooLargeException>(() => sut.ReadAsync(Request(), CancellationToken.None));

        // Межа 100 байт: рушій зупиняється на першому ж шматку, а не збирає мільйон.
        Assert.True(stream.Consumed < total / 10, $"Прочитано {stream.Consumed} із {total} байт.");
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

    /// <summary>Потік без довжини (не підтримує пошук): віддає <c>total</c> байт шматками й рахує прочитане.</summary>
    private sealed class ChunkedStream(int total, int chunk) : Stream
    {
        private int consumed;

        public int Consumed => Volatile.Read(ref consumed);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var give = Math.Min(Math.Min(buffer.Length, chunk), total - consumed);
            if (give <= 0)
            {
                return 0;
            }

            buffer[..give].Fill((byte)'x');
            consumed += give;
            return give;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
