using System.Net;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Режими автентифікації PI Web API: Basic | Bearer | Negotiate.
/// </summary>
/// <remarks>
/// Режим задається значенням секрету джерела (без міграції):
/// <c>Basic …</c>, <c>Bearer …</c>/значення без пробілу, <c>Negotiate</c> або
/// відсутній секрет. Negotiate іде окремим клієнтом, чий первинний обробник
/// має <c>UseDefaultCredentials</c> (обліковий запис служби, D-34).
/// <para>
/// ⛔ Мутаційні перевірки (зроблено вручну перед комітом):
/// <c>UseDefaultCredentials = true</c> → <c>false</c> у
/// <see cref="PiWebApiAuthentication.CreatePrimaryHandler"/> валить
/// <see cref="CreatePrimaryHandler_ЛишеNegotiateБереОбліковіДаніПроцесу"/> і
/// DI-тест; повернення <c>http</c> замість клієнта Negotiate в
/// <c>PiWebApiDataSource.Authorize</c> валить тести маршрутизації, 401/403
/// Negotiate і DI-наскрізний; перейменована реєстрація іменованого клієнта в
/// <c>AddPiAfAdapters</c> валить DI-тест обробника.
/// </para>
/// </remarks>
public sealed class PiWebApiAuthenticationTests
{
    private const string ElementsSuccessJson = """{"Items":[]}""";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Negotiate")]
    [InlineData("negotiate")]
    [InlineData(" NEGOTIATE ")]
    public void ModeOf_NegotiateАбоВідсутнійСекрет_ДаєNegotiate(string? secret)
        => Assert.Equal(PiWebApiAuthMode.Negotiate, PiWebApiAuthentication.ModeOf(secret));

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz", PiWebApiAuthMode.Basic)]
    [InlineData("basic dXNlcjpwYXNz", PiWebApiAuthMode.Basic)]
    [InlineData("Bearer eyJhbGciOi", PiWebApiAuthMode.Bearer)]
    [InlineData("eyJhbGciOi", PiWebApiAuthMode.Bearer)]
    [InlineData("Negotiate abc", PiWebApiAuthMode.Bearer)]
    public void ModeOf_СекретІзЗначенням_ДаєЗаголовковийРежим(string secret, PiWebApiAuthMode expected)
        => Assert.Equal(expected, PiWebApiAuthentication.ModeOf(secret));

    [Fact]
    public void HeaderOf_Negotiate_НеДаєЗаголовка()
    {
        Assert.Null(PiWebApiAuthentication.HeaderOf("Negotiate"));
        Assert.Null(PiWebApiAuthentication.HeaderOf(null));
    }

    [Theory]
    [InlineData(PiWebApiAuthMode.Negotiate, true)]
    [InlineData(PiWebApiAuthMode.Basic, false)]
    [InlineData(PiWebApiAuthMode.Bearer, false)]
    public void CreatePrimaryHandler_ЛишеNegotiateБереОбліковіДаніПроцесу(PiWebApiAuthMode mode, bool expected)
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(PiWebApiAuthentication.CreatePrimaryHandler(mode));

        Assert.Equal(expected, handler.Credentials is not null);
        if (expected)
        {
            Assert.Same(System.Net.CredentialCache.DefaultCredentials, handler.Credentials);
        }

        Assert.False(handler.PreAuthenticate);

        // Перевірка IP у момент підключення (DNS-rebinding) — у всіх режимах.
        Assert.NotNull(handler.ConnectCallback);

        // Жоден режим не слідує перенаправленню (D-241 п.5, L3-08): для Negotiate —
        // облікові дані служби, для Basic/Bearer — обхід PiWebApi:AllowedHosts.
        // Мутація: повернути AllowAutoRedirect = true у будь-якій гілці.
        Assert.False(handler.AllowAutoRedirect);
    }

    /// <summary>
    /// DI реєструє два клієнти: типізований (заголовкові режими) без облікових
    /// даних процесу і Negotiate — з ними; обидва з таймаутом 30 с (Q-250).
    /// </summary>
    [Theory]
    [InlineData(PiWebApiAuthentication.NegotiateClientName, true)]
    [InlineData(nameof(PiWebApiDataSource), false)]
    public void AddPiAfAdapters_ПервиннийОбробникКлієнтаВідповідаєРежиму(string clientName, bool defaultCredentials)
    {
        var services = new ServiceCollection();
        services.AddPiAfAdapters();
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.Equal(defaultCredentials, Assert.IsType<SocketsHttpHandler>(handler).Credentials is not null);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName).Timeout);
    }

    [Fact]
    public async Task Negotiate_ЗапитІдеКлієнтомNegotiate_БезЗаголовкаAuthorization()
    {
        var header = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);
        var negotiate = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(header, negotiate, secretValue: "Negotiate");

        await sut.DiscoverAsync(1, CancellationToken.None);

        Assert.Empty(header.Requests);
        var request = Assert.Single(negotiate.Requests);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz", "Basic")]
    [InlineData("Bearer eyJhbGciOi", "Bearer")]
    public async Task ЗаголовковийРежим_ЗапитІдеТипізованимКлієнтомІзЗаголовком(string secret, string scheme)
    {
        var header = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);
        var negotiate = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(header, negotiate, secret);

        await sut.DiscoverAsync(1, CancellationToken.None);

        Assert.Empty(negotiate.Requests);
        Assert.Equal(scheme, Assert.Single(header.Requests).Headers.Authorization?.Scheme);
    }

    /// <summary>
    /// 401/403 у режимі Negotiate — той самий <see cref="SourceAuthenticationException"/>
    /// без повторів, що й у заголовкових (<c>H-20</c>): службовий обліковий запис
    /// без прав у PI — помилка налаштування, а не недоступність джерела.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Negotiate_401або403_КидаєSourceAuthenticationException_БезПовторів(HttpStatusCode status)
    {
        var header = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);
        var negotiate = new RecordingHandler(status);
        var sut = CreateSut(header, negotiate, secretValue: null);

        var thrown = await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => sut.DiscoverAsync(1, CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceAuthenticationRefused, thrown.ErrorCode);
        Assert.Single(negotiate.Requests);
        Assert.Empty(header.Requests);
    }

    /// <summary>
    /// Наскрізь через DI: адаптер, зібраний контейнером, справді отримує
    /// фабрику й шле Negotiate-запит клієнтом <see cref="PiWebApiAuthentication.NegotiateClientName"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Без цього тесту необов'язковий параметр фабрики міг би тихо лишитися
    /// <c>null</c> у продукті, і Negotiate пішов би типізованим клієнтом без
    /// облікових даних процесу — рівно той <c>401</c>, заради якого зміна.
    /// </remarks>
    [Fact]
    public async Task AddPiAfAdapters_NegotiateЗапитАдаптераЙдеІменованимКлієнтом()
    {
        var header = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);
        var negotiate = new RecordingHandler(HttpStatusCode.OK, ElementsSuccessJson);

        var services = new ServiceCollection();
        services.AddPiAfAdapters();
        services.AddHttpClient<PiWebApiDataSource>().ConfigurePrimaryHttpMessageHandler(() => header);
        services.AddHttpClient(PiWebApiAuthentication.NegotiateClientName)
            .ConfigurePrimaryHttpMessageHandler(() => negotiate);
        services.AddSingleton(Store());
        services.AddSingleton(Secrets(null));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var sut = scope.ServiceProvider.GetRequiredService<PiWebApiDataSource>();
        await sut.DiscoverAsync(1, CancellationToken.None);

        Assert.Empty(header.Requests);
        Assert.Single(negotiate.Requests);
    }

    private static PiWebApiDataSource CreateSut(
        RecordingHandler header, RecordingHandler negotiate, string? secretValue)
    {
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(PiWebApiAuthentication.NegotiateClientName).Returns(_ => new HttpClient(negotiate));

        return new PiWebApiDataSource(new HttpClient(header), Store(), Secrets(secretValue), clients);
    }

    private static ICollectionStore Store()
    {
        var store = Substitute.For<ICollectionStore>();
        var dataSource = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://pi.example",
            "PiAf.Primary");
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);
        return store;
    }

    private static ISecretProvider Secrets(string? value)
    {
        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns(value);
        return secrets;
    }

    /// <summary>Завжди одна й та сама відповідь; записує кожен запит.</summary>
    private sealed class RecordingHandler(HttpStatusCode status, string? json = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(status);
            if (json is not null)
            {
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
