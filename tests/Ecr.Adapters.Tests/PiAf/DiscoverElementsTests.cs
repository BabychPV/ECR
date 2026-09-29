using System.Data;
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
/// Перелік елементів для синку довідника (<see cref="IExternalDataSource.DiscoverElementsAsync"/>,
/// <c>D-212</c> PR-1) і текст дати в значенні PI SQL Client.
/// </summary>
/// <remarks>
/// PI SQL Client перевіряється розбором рядків (<see cref="PiSqlClientDataSource.ReadElementsAsync"/>
/// над <see cref="DataTableReader"/>) і відмовою ДО з'єднання: живого RTQP у контурі розробки немає.
/// <para>
/// Мутаційні докази (прогнано 2026-09-29 у власному worktree, кожна окремо, після — відкат):
/// М1 — у <c>ReadElementsAsync</c> адреса читання = <c>ElementPath</c> замість імені → червоніє
/// <see cref="PiSqlClient_перелік_повний_адреса_читання_ім_я_елемента"/>;
/// М2 — <c>ConfiguredOrRefuse</c> замінити типовим текстом → червоніє
/// <see cref="PiSqlClient_без_ключа_ElementListQuery_відмовляє_0422_до_джерела"/>;
/// М3 — у <c>PiWebApiDataSource.DiscoverElementsAsync</c> адреса = ім'я → червоніє
/// <see cref="PiWebApi_перелік_адреса_читання_шлях"/>;
/// М4 — прибрати гілку <c>DateTime</c> у <c>Value()</c> → червоніє
/// <see cref="PiSqlClient_дата_в_значенні_ISO_незалежно_від_культури"/>.
/// </para>
/// </remarks>
public sealed class DiscoverElementsTests
{
    private const string Requirement = "ФВ-8.11";

    private const string G1 = "6f1c0e9a-0000-4000-8000-000000000001";

    private const string G2 = "6f1c0e9a-0000-4000-8000-000000000002";

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_перелік_повний_адреса_читання_ім_я_елемента()
    {
        using var table = Elements(withPath: true);
        table.Rows.Add(Guid.Parse(G1), "Stack1", @"\\AF\Db\Plant\Stack1");
        table.Rows.Add(Guid.Parse(G2), "Stack2", DBNull.Value);

        var result = await PiSqlClientDataSource.ReadElementsAsync(table.CreateDataReader(), 100, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            [(G1, "Stack1", @"\\AF\Db\Plant\Stack1", "Stack1"), (G2, "Stack2", null, "Stack2")],
            result.Elements.Select(e => (e.ExternalId, e.Name, e.Path, e.ReadAddress)));

        // ⛔ RTQP шукає елемент `WHERE e.Name = ?`: шлях атрибута для ReadCurrentAsync — «ім'я|атрибут».
        Assert.Equal("Stack1|Capacity", $"{result.Elements[0].ReadAddress}|Capacity");
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_без_ElementId_або_на_стелі_перелік_неповний()
    {
        using var noId = Elements(withPath: false);
        noId.Rows.Add(Guid.Parse(G1), "Stack1");
        noId.Rows.Add(DBNull.Value, "Stack2");

        var partial = await PiSqlClientDataSource.ReadElementsAsync(noId.CreateDataReader(), 100, CancellationToken.None);

        Assert.False(partial.IsComplete);
        Assert.Equal([G1], partial.Elements.Select(e => e.ExternalId));

        using var full = Elements(withPath: false);
        full.Rows.Add(Guid.Parse(G1), "Stack1");
        full.Rows.Add(Guid.Parse(G2), "Stack2");

        // Дійшли до стелі — хвіст міг лишитися непрочитаним.
        Assert.False((await PiSqlClientDataSource.ReadElementsAsync(full.CreateDataReader(), 2, CancellationToken.None)).IsComplete);
        Assert.True((await PiSqlClientDataSource.ReadElementsAsync(full.CreateDataReader(), 3, CancellationToken.None)).IsComplete);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_без_ключа_ElementListQuery_відмовляє_0422_до_джерела()
    {
        var store = Substitute.For<ICollectionStore>();
        var adapter = new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), Substitute.For<ISecretProvider>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverElementsAsync(1, @"\\AF\Db\Plant", CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
        Assert.Equal("ElementList", error.Details["queryKind"]);
        Assert.Equal(PiSqlClientDataSource.ElementListQueryKey, error.Details["configKey"]);
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_з_ключем_ElementListQuery_іде_до_джерела()
    {
        // Контраст до попереднього: з текстом відмови конфігурації немає — далі «джерела немає».
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((DataSource?)null);
        var settings = Substitute.For<ISecretProvider>();
        settings.Find(PiSqlClientDataSource.ElementListQueryKey).Returns("SELECT 1 AS ElementId WHERE ? IS NOT NULL");

        var adapter = new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), settings);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverElementsAsync(1, "Plant", CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        await store.Received(1).FindDataSourceAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_перелік_адреса_читання_шлях()
    {
        const string plant = @"\\PISRV\ECR\Plant";
        var pi = new RoutingHandler()
            .On("/piwebapi/elements?path=" + Uri.EscapeDataString(plant), """{"WebId":"E-P","Id":"p"}""")
            .On("/piwebapi/elements/E-P/elements?searchFullHierarchy=false",
                "{\"Items\":[{\"Id\":\"" + G1 + "\",\"Name\":\"Stack1\",\"Path\":\"" + plant.Replace(@"\", @"\\", StringComparison.Ordinal) + "\\\\Stack1\"}]}");

        var result = await WebApi(pi).DiscoverElementsAsync(1, plant, CancellationToken.None);

        Assert.True(result.IsComplete);
        var element = Assert.Single(result.Elements);
        Assert.Equal((G1, "Stack1", plant + @"\Stack1", plant + @"\Stack1"), (element.ExternalId, element.Name, element.Path, element.ReadAddress));
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiWebApi_елемент_без_Id_робить_перелік_неповним()
    {
        const string plant = @"\\PISRV\ECR\Plant";
        var pi = new RoutingHandler()
            .On("/piwebapi/elements?path=" + Uri.EscapeDataString(plant), """{"WebId":"E-P","Id":"p"}""")
            .On("/piwebapi/elements/E-P/elements?searchFullHierarchy=false",
                """{"Items":[{"Name":"Stack1","Path":"\\\\PISRV\\ECR\\Plant\\Stack1"}]}""");

        var result = await WebApi(pi).DiscoverElementsAsync(1, plant, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Elements);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task Транспорт_без_переліку_відмовляє_0422_а_не_порожнім_знімком()
    {
        var store = Substitute.For<ICollectionStore>();
        IExternalDataSource adapter = new SqlDataSource(store, Substitute.For<ISecretProvider>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverElementsAsync(1, "ROOT", CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotSupported", error.Details!["messageKey"]);
        Assert.Equal("ElementList", error.Details["queryKind"]);
        Assert.Equal("Sql", error.Details["transport"]);
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }

    [Fact]
    [Trait("Requirement", Requirement)]
    public async Task PiSqlClient_дата_в_значенні_ISO_незалежно_від_культури()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");

        try
        {
            using var table = new DataTable();
            table.Columns.Add("Ts", typeof(DateTime));
            table.Columns.Add("Val", typeof(DateTime)).DateTimeMode = DataSetDateTime.Utc;
            table.Rows.Add(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));

            var point = Assert.Single(await PiSqlClientDataSource.ReadPointsAsync(
                table.CreateDataReader(), "Stack1|Commissioned", 10, CancellationToken.None));

            Assert.Null(point.ValueNumeric);
            Assert.Equal("2026-03-04T05:06:07.0000000Z", point.ValueString);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }

    private static DataTable Elements(bool withPath)
    {
        var table = new DataTable();
        table.Columns.Add("ElementId", typeof(Guid));
        table.Columns.Add("ElementName", typeof(string));
        if (withPath)
        {
            table.Columns.Add("ElementPath", typeof(string));
        }

        return table;
    }

    private static PiWebApiDataSource WebApi(RoutingHandler pi)
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

        public RoutingHandler On(string pathAndQuery, string json)
        {
            routes[pathAndQuery] = json;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var response = uri.Host == "pi.example" && routes.TryGetValue(uri.PathAndQuery, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            return Task.FromResult(response);
        }
    }
}
