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

/// <summary>
/// PI Web API: режим автентифікації НА РІВНІ ДЖЕРЕЛА — кілька джерел з різними
/// режимами в одному адаптері, повторне використання клієнта Negotiate,
/// відмова без витоку секрету.
/// </summary>
/// <remarks>
/// Доповнює <see cref="PiWebApiAuthenticationTests"/> (там: <c>ModeOf</c>,
/// <c>HeaderOf</c>, первинні обробники, DI, Negotiate 401/403). Тут лише те, чого
/// там немає. Без мережі й без SQL: транспорт — фейковий
/// <see cref="HttpMessageHandler"/>.
/// <para>
/// Мутації, які ці тести ловлять: <c>PiWebApiDataSource.Authorize</c> запам'ятовує
/// режим першого запиту (а не рахує його за секретом кожного) — валять
/// <see cref="ТриДжерелаЗРізнимиРежимами_КожнеСвоїмКлієнтомІЗаголовком"/> і
/// <see cref="ЗмінаСекретуМіжЗапитами_ПереходитьМіжКлієнтами"/>; прибрати
/// кешування <c>negotiateClient</c> — валить
/// <see cref="Negotiate_КлієнтАдаптераБерЕться_ЛишеОдинРаз"/>; вписати значення секрету
/// в текст виняткової ситуації — валить
/// <see cref="ВідмоваЗаголовковогоРежиму_НеВитікаєСекрет"/>.
/// </para>
/// </remarks>
public sealed class PiWebApiAuthModeTests
{
    private const string ElementsJson = """{"Items":[]}""";

    [Fact]
    public async Task ТриДжерелаЗРізнимиРежимами_КожнеСвоїмКлієнтомІЗаголовком()
    {
        var fixture = new Fixture()
            .Source("NEG", secret: null)
            .Source("BAS", secret: "Basic dXNlcjpwYXNz")
            .Source("BEA", secret: "Bearer eyJ.tok");

        await fixture.Sut.DiscoverAsync(fixture.IdOf("NEG"), CancellationToken.None);
        await fixture.Sut.DiscoverAsync(fixture.IdOf("BAS"), CancellationToken.None);
        await fixture.Sut.DiscoverAsync(fixture.IdOf("NEG"), CancellationToken.None);
        await fixture.Sut.DiscoverAsync(fixture.IdOf("BEA"), CancellationToken.None);

        Assert.Equal(2, fixture.Negotiate.Requests.Count);
        Assert.All(fixture.Negotiate.Requests, r => Assert.Null(r.Headers.Authorization));

        Assert.Equal(2, fixture.Header.Requests.Count);
        Assert.Equal("Basic", fixture.Header.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("dXNlcjpwYXNz", fixture.Header.Requests[0].Headers.Authorization?.Parameter);
        Assert.Equal("Bearer", fixture.Header.Requests[1].Headers.Authorization?.Scheme);
        Assert.Equal("eyJ.tok", fixture.Header.Requests[1].Headers.Authorization?.Parameter);
    }

    /// <summary>Режим — налаштування джерела «на момент запиту»: зміна секрету перемикає клієнта.</summary>
    [Fact]
    public async Task ЗмінаСекретуМіжЗапитами_ПереходитьМіжКлієнтами()
    {
        var fixture = new Fixture().Source("SRC", secret: "Bearer eyJ.tok");
        var id = fixture.IdOf("SRC");

        await fixture.Sut.DiscoverAsync(id, CancellationToken.None);
        fixture.SetSecret("SRC", null);
        await fixture.Sut.DiscoverAsync(id, CancellationToken.None);

        Assert.Equal("Bearer", Assert.Single(fixture.Header.Requests).Headers.Authorization?.Scheme);
        Assert.Null(Assert.Single(fixture.Negotiate.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task Negotiate_КлієнтАдаптераБерЕться_ЛишеОдинРаз()
    {
        var fixture = new Fixture().Source("NEG", secret: "Negotiate");
        var id = fixture.IdOf("NEG");

        await fixture.Sut.DiscoverAsync(id, CancellationToken.None);
        await fixture.Sut.DiscoverAsync(id, CancellationToken.None);

        Assert.Equal(2, fixture.Negotiate.Requests.Count);
        fixture.Factory.Received(1).CreateClient(PiWebApiAuthentication.NegotiateClientName);
    }

    /// <summary>Без фабрики (тести з одним обробником) Negotiate іде тим самим клієнтом, без заголовка.</summary>
    [Fact]
    public async Task БезФабрики_NegotiateІдеТипізованимКлієнтом_БезЗаголовка()
    {
        var typed = new StubHandler();
        var sut = new PiWebApiDataSource(new HttpClient(typed), Store(new Dictionary<int, DataSource>
        {
            [1] = NewSource("NEG"),
        }), Secrets(_ => null));

        await sut.DiscoverAsync(1, CancellationToken.None);

        Assert.Null(Assert.Single(typed.Requests).Headers.Authorization);
    }

    /// <summary>Відмова в автентифікації не повторюється й не містить значення секрету (ФВ-6.11).</summary>
    [Theory]
    [InlineData("Basic c3VwM3ItczNjcjN0", HttpStatusCode.Unauthorized)]
    [InlineData("Bearer sup3r-s3cret-tok", HttpStatusCode.Forbidden)]
    public async Task ВідмоваЗаголовковогоРежиму_НеВитікаєСекрет(string secret, HttpStatusCode status)
    {
        var fixture = new Fixture().Source("SRC", secret);
        fixture.Header.Status = status;

        var thrown = await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => fixture.Sut.DiscoverAsync(fixture.IdOf("SRC"), CancellationToken.None));

        Assert.Equal("err.ECR-INT-0502.piWebApiUnauthorized", thrown.Details?["messageKey"]);
        Assert.Equal(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), thrown.Details?["status"]);
        Assert.DoesNotContain(secret[(secret.IndexOf(' ', StringComparison.Ordinal) + 1)..], thrown.Message, StringComparison.Ordinal);

        // Один запит: 401/403 не повторюється (H-20).
        Assert.Single(fixture.Header.Requests);
        Assert.Empty(fixture.Negotiate.Requests);
    }

    private static DataSource NewSource(string code) => new(
        EcrCode.Create(code),
        new LocalizedText(new Dictionary<string, string> { ["uk"] = code }),
        ExternalTransport.PiWebApi,
        "https://pi.example",
        $"DataSource.{code}");

    private static ICollectionStore Store(Dictionary<int, DataSource> sources)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => sources[(int)call[0]]);
        return store;
    }

    private static ISecretProvider Secrets(Func<string, string?> find)
    {
        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns(call => find((string)call[0]));
        return secrets;
    }

    /// <summary>Адаптер + два фейкові обробники: типізований (заголовкові режими) і Negotiate.</summary>
    private sealed class Fixture
    {
        private readonly Dictionary<int, DataSource> sources = [];
        private readonly Dictionary<string, int> ids = [];
        private readonly Dictionary<string, string?> secretValues = [];
        private PiWebApiDataSource? sut;

        public StubHandler Header { get; } = new();

        public StubHandler Negotiate { get; } = new();

        public IHttpClientFactory Factory { get; } = Substitute.For<IHttpClientFactory>();

        public PiWebApiDataSource Sut => sut ??= Build();

        public int IdOf(string code) => ids[code];

        public Fixture Source(string code, string? secret)
        {
            var id = ids.Count + 1;
            ids[code] = id;
            sources[id] = NewSource(code);
            secretValues[$"DataSource.{code}"] = secret;
            return this;
        }

        public void SetSecret(string code, string? secret) => secretValues[$"DataSource.{code}"] = secret;

        private PiWebApiDataSource Build()
        {
            Factory.CreateClient(PiWebApiAuthentication.NegotiateClientName)
                .Returns(_ => new HttpClient(Negotiate, disposeHandler: false));

            return new PiWebApiDataSource(
                new HttpClient(Header, disposeHandler: false),
                Store(sources),
                Secrets(name => secretValues.GetValueOrDefault(name)),
                Factory);
        }
    }

    /// <summary>Фейковий обробник: записує запити, віддає <see cref="Status"/> (за замовчуванням 200).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var response = new HttpResponseMessage(Status);
            if (Status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(ElementsJson, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
