using System.Net;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Поточні значення атрибутів для синхронізації довідників
/// (<see cref="IExternalDataSource.ReadCurrentAsync"/>, FEATURE-REGISTRY-SYNC S1) і
/// відмова інтерпольованого запиту в PI Web API та SQL (борг HSE301 F4).
/// </summary>
/// <remarks>
/// PI Web API — підставний HTTP з мінімальними зразками відповідей
/// (<c>attributes?path=</c>, <c>streams/{webId}/value</c>). PI SQL Client і SQL
/// перевіряються на відмовах ДО з'єднання: живого RTQP у контурі розробки немає,
/// а SQL-джерело поточних значень не читає за задумом.
/// <para>
/// Мутаційні докази (прогнано 2026-09-28 у власному worktree, кожна мутація
/// окремо, після — відкат):
/// М1 — у <c>PiWebApiDataSource.ReadCurrentAsync</c> замість відмови шляху додати
/// точку «0, Good» (відсутній атрибут як нуль) → червоніє
/// <see cref="PiWebApi_відсутній_атрибут_це_відмова_по_шляху_а_не_нуль"/>;
/// М2 — <c>Quality(item)</c> у <c>CurrentPoint</c> замінити на <c>"Good"</c> → червоніють
/// <see cref="PiWebApi_статичний_атрибут_читається_значення_якість_час"/> і
/// <see cref="PiWebApi_текстовий_атрибут_без_одиниці_у_відповіді_бере_одиницю_атрибута"/>;
/// М3 — прибрати перевірку <c>Kind != Raw</c> у <c>PiWebApiDataSource.ReadAsync</c> →
/// червоніє <see cref="PiWebApi_інтерпольований_запит_відмовляє_0422_і_не_чіпає_PI"/>;
/// М4 — те саме в <c>SqlDataSource.ReadAsync</c> → червоніє
/// <see cref="Sql_інтерпольований_запит_відмовляє_0422_до_джерела"/>;
/// М5 — у <c>PiSqlClientDataSource.ReadCurrentAsync</c> підставити типовий текст
/// замість <c>ConfiguredOrRefuse</c> → червоніє
/// <see cref="PiSqlClient_без_ключа_CurrentValueQuery_відмовляє_0422_до_джерела"/>;
/// М6 — типова реалізація порту повертає порожній знімок замість відмови →
/// червоніє <see cref="Sql_поточні_значення_не_читає_відмова_0422"/>;
/// М7 — атрибут несе власний <c>Id</c> замість GUID елемента → червоніє
/// <see cref="PiWebApi_каталог_дає_GUID_елемента_як_ExternalId"/>.
/// ⚠ Мутаційно НЕ доведено: <c>ElementId</c> у каталозі PI SQL Client
/// (<c>OdbcDataReader</c> ззовні не зробиш) і читання рядка поточного значення
/// RTQP із налаштованим текстом — живого RTQP у контурі немає.
/// </para>
/// </remarks>
public sealed class CurrentValueReadTests
{
    private const string Plant = @"\\PISRV\ECR\Plant";

    private const string Requirement = "ФВ-8.11";

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_статичний_атрибут_читається_значення_якість_час()
    {
        var path = Plant + "|Capacity";
        var pi = new RoutingHandler()
            .On("/piwebapi/attributes?path=" + Uri.EscapeDataString(path),
                """{"WebId":"A-CAP","Name":"Capacity","DefaultUnitsName":"cubic meter"}""")
            .On("/piwebapi/streams/A-CAP/value",
                """{"Timestamp":"2026-09-01T06:30:00Z","Value":1250.75,"UnitsAbbreviation":"m3","Good":true,"Questionable":true}""");

        var result = await Adapter(pi).ReadCurrentAsync(1, [path], CancellationToken.None);

        var value = Assert.Single(result.Values);
        Assert.Empty(result.Failures);
        Assert.Equal(path, value.SourcePath);
        Assert.Equal(1250.75m, value.ValueNumeric);
        Assert.Null(value.ValueString);
        Assert.Equal("m3", value.SourceUnitSymbol);
        Assert.Equal("Questionable", value.Quality);
        Assert.Equal(new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc), value.Timestamp);
        Assert.Equal(DateTimeKind.Utc, value.Timestamp.Kind);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_текстовий_атрибут_без_одиниці_у_відповіді_бере_одиницю_атрибута()
    {
        var path = Plant + "|Tag";
        var pi = new RoutingHandler()
            .On("/piwebapi/attributes?path=" + Uri.EscapeDataString(path),
                """{"WebId":"A-TAG","DefaultUnitsName":"percent"}""")
            .On("/piwebapi/streams/A-TAG/value",
                """{"Timestamp":"2026-09-01T00:00:00Z","Value":"FL-101","Good":false}""");

        var value = Assert.Single((await Adapter(pi).ReadCurrentAsync(1, [path], CancellationToken.None)).Values);

        Assert.Null(value.ValueNumeric);
        Assert.Equal("FL-101", value.ValueString);
        Assert.Equal("percent", value.SourceUnitSymbol);
        Assert.Equal("Bad", value.Quality);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_відсутній_атрибут_це_відмова_по_шляху_а_не_нуль()
    {
        var present = Plant + "|Capacity";
        var missing = Plant + "|Gone";
        var noWebId = Plant + "|Broken";
        var pi = new RoutingHandler()
            .On("/piwebapi/attributes?path=" + Uri.EscapeDataString(present), """{"WebId":"A-CAP"}""")
            .On("/piwebapi/streams/A-CAP/value", """{"Timestamp":"2026-09-01T00:00:00Z","Value":5}""")
            .On("/piwebapi/attributes?path=" + Uri.EscapeDataString(noWebId), """{"Name":"Broken"}""");

        // Шлях `missing` — 404 від підставного PI (маршруту немає).
        var result = await Adapter(pi).ReadCurrentAsync(
            1, [missing, present, noWebId, present], CancellationToken.None);

        Assert.Equal([present], result.Values.Select(v => v.SourcePath));
        Assert.Equal(
            [(missing, "ECR-INT-0404", "err.ECR-INT-0404.sourcePathNotFound"),
             (noWebId, "ECR-INT-0404", "err.ECR-INT-0404.sourcePathNotFound")],
            result.Failures.Select(f => (f.SourcePath, f.ErrorCode, f.MessageKey)));

        // Відсутній атрибут не читає значення: жодного `streams/…/value` для нього.
        Assert.Single(pi.Requests, r => r.Contains("/streams/", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_значення_без_мітки_часу_це_відмова_шляху_0503()
    {
        var path = Plant + "|Capacity";
        var pi = new RoutingHandler()
            .On("/piwebapi/attributes?path=" + Uri.EscapeDataString(path), """{"WebId":"A-CAP"}""")
            .On("/piwebapi/streams/A-CAP/value", """{"Value":5}""");

        var result = await Adapter(pi).ReadCurrentAsync(1, [path], CancellationToken.None);

        Assert.Empty(result.Values);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(("ECR-INT-0503", "err.ECR-INT-0503.currentValueUnreadable"), (failure.ErrorCode, failure.MessageKey));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_інтерпольований_запит_відмовляє_0422_і_не_чіпає_PI()
    {
        var pi = new RoutingHandler();
        var request = new CollectionRequest(
            1, 7, Plant + "|Flow", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, 100,
            SourceQueryKind.Interpolated, TimeSpan.FromMinutes(1));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Adapter(pi).ReadAsync(request, CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotSupported", error.Details!["messageKey"]);
        Assert.Equal("Interpolated", error.Details["queryKind"]);
        Assert.Equal("PiWebApi", error.Details["transport"]);
        Assert.Empty(pi.Requests);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_каталог_дає_GUID_елемента_як_ExternalId()
    {
        var pi = new RoutingHandler()
            .On("/piwebapi/assetdatabases/D0DB/elements?searchFullHierarchy=false",
                "{\"Items\":[{\"WebId\":\"E-P\",\"Id\":\"6f1c0e9a-0000-4000-8000-000000000001\",\"Name\":\"Plant\"}]}")
            .On("/piwebapi/elements?path=" + Uri.EscapeDataString(Plant),
                """{"WebId":"E-P","Id":"6f1c0e9a-0000-4000-8000-000000000001","Name":"Plant"}""")
            .On("/piwebapi/elements/E-P/attributes?searchFullHierarchy=false",
                """{"Items":[{"WebId":"A-F","Id":"aaaaaaaa-0000-4000-8000-000000000002","Name":"Flow","Type":"Double"}]}""");

        var adapter = Adapter(pi);
        var element = Assert.Single(await adapter.BrowseAsync(1, null, CancellationToken.None));
        var attribute = Assert.Single(await adapter.AttributesAsync(1, Plant, CancellationToken.None));

        Assert.Equal("6f1c0e9a-0000-4000-8000-000000000001", element.ExternalId);

        // Атрибут несе GUID свого ЕЛЕМЕНТА, не власний: ключ запису довідника — елемент.
        Assert.Equal("6f1c0e9a-0000-4000-8000-000000000001", attribute.ExternalId);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_без_ключа_CurrentValueQuery_відмовляє_0422_до_джерела()
    {
        var store = Substitute.For<ICollectionStore>();
        var adapter = new PiSqlClientDataSource(
            store, Substitute.For<ISecretProvider>(), Substitute.For<ISecretProvider>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadCurrentAsync(1, ["EL|Capacity"], CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
        Assert.Equal("CurrentValue", error.Details["queryKind"]);
        Assert.Equal(PiSqlClientDataSource.CurrentValueQueryKey, error.Details["configKey"]);
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_з_ключем_CurrentValueQuery_іде_до_джерела()
    {
        // Контраст до попереднього: з налаштованим текстом відмови конфігурації
        // немає — далі звична відмова «джерела немає» (підставне сховище).
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((DataSource?)null);
        var settings = Substitute.For<ISecretProvider>();
        settings.Find(PiSqlClientDataSource.CurrentValueQueryKey).Returns("SELECT 1 -- {template} {attribute}");

        var adapter = new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), settings);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadCurrentAsync(1, ["EL|Capacity"], CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        await store.Received(1).FindDataSourceAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Sql_поточні_значення_не_читає_відмова_0422()
    {
        var store = Substitute.For<ICollectionStore>();
        IExternalDataSource adapter = new SqlDataSource(store, Substitute.For<ISecretProvider>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadCurrentAsync(1, ["STREAM-1"], CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.currentValueNotSupported", error.Details!["messageKey"]);
        Assert.Equal("Sql", error.Details["transport"]);
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Sql_інтерпольований_запит_відмовляє_0422_до_джерела()
    {
        var store = Substitute.For<ICollectionStore>();
        var adapter = new SqlDataSource(store, Substitute.For<ISecretProvider>());
        var request = new CollectionRequest(
            1, 7, "STREAM-1", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, 100,
            SourceQueryKind.Interpolated, TimeSpan.FromMinutes(1));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadAsync(request, CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotSupported", error.Details!["messageKey"]);
        Assert.Equal("Sql", error.Details["transport"]);
        Assert.Equal("Interpolated", error.Details["queryKind"]);
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }

    private static PiWebApiDataSource Adapter(RoutingHandler pi)
    {
        var dataSource = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://pi.example/piwebapi",
            "PiAf.Primary");
        dataSource.Configure(null, "D0DB", 1);

        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);

        return new PiWebApiDataSource(new HttpClient(pi), store, Substitute.For<ISecretProvider>());
    }

    /// <summary>Підставний PI: відповідь за шляхом+запитом; невідомий запит — 404.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> routes = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public RoutingHandler On(string pathAndQuery, string json)
        {
            routes[pathAndQuery] = json;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.AbsoluteUri);

            var response = uri.Host == "pi.example" && routes.TryGetValue(uri.PathAndQuery, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            return Task.FromResult(response);
        }
    }
}
