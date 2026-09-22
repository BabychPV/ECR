using System.Net;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Лінивий обхід каталогу PI Web API: корінь → елементи, елемент → прямі діти
/// й атрибути з одиницею, <c>Links.Next</c>. Відповіді — мінімальні зразки
/// формату PI Web API (<c>Items</c>, <c>WebId</c>, <c>Path</c>, <c>DefaultUnitsName</c>).
/// </summary>
public sealed class PiWebApiCatalogTests
{
    private const string Plant = @"\\PISRV\ECR\Plant";

    [Fact]
    public async Task Корінь_читає_елементи_бази_AF_без_повного_обходу()
    {
        var pi = new RoutingHandler()
            .On("/piwebapi/assetdatabases/D0DB/elements?searchFullHierarchy=false",
                "{\"Items\":[{\"WebId\":\"E-P\",\"Name\":\"Plant\",\"Path\":\"" + Json(Plant) + "\",\"HasChildren\":true}],\"Links\":{}}");

        var reader = Reader(pi);

        var root = await reader.BrowseAsync(1, null, CancellationToken.None);

        var plant = Assert.Single(root);
        Assert.Equal(("Plant", Plant, "Element"), (plant.Code, plant.EntityPath, plant.DataType));
        Assert.Single(pi.Requests);
    }

    [Fact]
    public async Task Елемент_дає_прямих_дітей_і_атрибути_з_одиницею()
    {
        var pi = new RoutingHandler()
            .On("/piwebapi/elements?path=" + Uri.EscapeDataString(Plant), """{"WebId":"E-P","Name":"Plant"}""")
            .On("/piwebapi/elements/E-P/elements?searchFullHierarchy=false",
                $$"""{"Items":[{"WebId":"E-B","Name":"Boiler1","Path":"{{Json(Plant)}}\\Boiler1"}]}""")
            .On("/piwebapi/elements/E-P/attributes?searchFullHierarchy=false",
                $$"""
                {"Items":[
                  {"WebId":"A-F","Name":"Flow","Path":"{{Json(Plant)}}|Flow","Type":"Double","DefaultUnitsName":"cubic meter per hour"},
                  {"WebId":"A-S","Name":"State","Path":"{{Json(Plant)}}|State","Type":"EnumerationValue","DefaultUnitsName":""}
                ]}
                """);

        var reader = Reader(pi);

        var children = await reader.BrowseAsync(1, Plant, CancellationToken.None);
        var attributes = await reader.AttributesAsync(1, Plant, CancellationToken.None);

        Assert.Equal(["Boiler1"], children.Select(c => c.Code));
        Assert.Equal(
            [("Flow", Plant + "|Flow", "cubic meter per hour", "Double"), ("State", Plant + "|State", null, "EnumerationValue")],
            attributes.Select(a => (a.Code, a.EntityPath, a.SourceUnitSymbol, a.DataType)));
        Assert.Contains(pi.Requests, r => r.EndsWith("/elements/E-P/attributes?searchFullHierarchy=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Пагінація_йде_за_Links_Next_лише_на_той_самий_хост()
    {
        var pi = new RoutingHandler()
            .On("/piwebapi/assetdatabases/D0DB/elements?searchFullHierarchy=false",
                """{"Items":[{"Name":"A"}],"Links":{"Next":"https://pi.example/piwebapi/assetdatabases/D0DB/elements?startIndex=1"}}""")
            .On("/piwebapi/assetdatabases/D0DB/elements?startIndex=1",
                """{"Items":[{"Name":"B"}],"Links":{"Next":"https://evil.example/piwebapi/x"}}""");

        var root = await Reader(pi).BrowseAsync(1, null, CancellationToken.None);

        Assert.Equal(["A", "B"], root.Select(r => r.Code));
        Assert.Equal(2, pi.Requests.Count);
    }

    private static string Json(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal);

    private static PiAfCatalogReader Reader(RoutingHandler pi)
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
        var secrets = Substitute.For<ISecretProvider>();

        var adapter = new PiWebApiDataSource(new HttpClient(pi), store, secrets);
        return new PiAfCatalogReader([adapter], store);
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
