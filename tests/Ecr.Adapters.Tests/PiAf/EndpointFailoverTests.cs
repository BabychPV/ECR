using System.Net;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// D-462 (P2 аудиту PI): запасна адреса джерела (<c>SecondaryEndpoint</c>) обслуговує збір, коли основна не
/// відповідає на рівні ЗВ'ЯЗКУ; автентифікація й конфігурація не перемикають; у журналі — роль адреси, без
/// адреси й секрету.
/// </summary>
public sealed class EndpointFailoverTests
{
    private const string Secret = "Bearer-secret-value-D462";

    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Відмова_звязку_основної_переводить_на_запасну_до_кінця_екземпляра()
    {
        var log = new RecordingLogger<EndpointFailoverTests>();
        var failover = new EndpointFailover(log, "Test");
        var source = Source(secondary: "pi-b");
        var calls = new List<string>();

        Task<string> Operation(string endpoint, CancellationToken ct)
        {
            calls.Add(endpoint);
            return endpoint == "pi-a" ? throw new TimeoutException() : Task.FromResult(endpoint);
        }

        Assert.Equal("pi-b", await failover.RunAsync(source, Operation, ex => ex is TimeoutException, CancellationToken.None));
        Assert.Equal(EndpointFailover.Secondary, failover.LastServedBy);

        // Липке: друге звернення того самого прогону основну вже не чіпає.
        Assert.Equal("pi-b", await failover.RunAsync(source, Operation, ex => ex is TimeoutException, CancellationToken.None));
        Assert.Equal(["pi-a", "pi-b", "pi-b"], calls);

        var warning = Assert.Single(log.OfLevel(LogLevel.Warning));
        Assert.Contains("PIAF", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pi-a", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pi-b", warning.Message, StringComparison.Ordinal);
    }

    /// <remarks>⛔ МУТАЦІЯ: прибрати фільтр <c>isConnectionFailure</c> у <c>EndpointFailover</c> — автентифікація йде на запасну.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Відмова_не_звязку_не_перемикає_і_летить_як_є()
    {
        var failover = new EndpointFailover(new RecordingLogger<EndpointFailoverTests>(), "Test");
        var calls = new List<string>();

        Task<string> Operation(string endpoint, CancellationToken ct)
        {
            calls.Add(endpoint);
            throw new SourceAuthenticationException("ECR-INT-0502", "denied", null);
        }

        await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => failover.RunAsync(Source(secondary: "pi-b"), Operation, _ => false, CancellationToken.None));
        Assert.Equal(["pi-a"], calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Без_запасної_адреси_відмова_звязку_летить_як_є()
    {
        var failover = new EndpointFailover(new RecordingLogger<EndpointFailoverTests>(), "Test");

        await Assert.ThrowsAsync<TimeoutException>(
            () => failover.RunAsync<string>(
                Source(secondary: "  "), (_, _) => throw new TimeoutException(), _ => true, CancellationToken.None));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    [InlineData("network", true)]
    [InlineData("status", false)]
    [InlineData("auth", false)]
    [InlineData("timeout", true)]
    [InlineData("errorStatus", false)]
    [InlineData("tooLarge", false)]
    public void Web_API_відмова_звязку_класифікується_за_типом_і_ключем(string kind, bool expected)
    {
        Exception error = kind switch
        {
            "network" => new HttpRequestException("connection refused"),
            "status" => new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway),
            "auth" => new SourceAuthenticationException("ECR-INT-0502", "denied", null),
            "timeout" => Rule("err.ECR-INT-0503.piWebApiTimeout"),
            "errorStatus" => Rule("err.ECR-INT-0503.piWebApiErrorStatus"),
            _ => Rule("err.ECR-INT-0503.piWebApiResponseTooLarge"),
        };

        Assert.Equal(expected, PiWebApiDataSource.IsConnectionFailure(error));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    [InlineData("err.ECR-INT-0503.connectFailed", true)]
    [InlineData("err.ECR-INT-0503.connectionStringBroken", false)]
    [InlineData("err.ECR-INT-0503.endpointForbidden", false)]
    [InlineData("err.ECR-INT-0503.sourceMissing", false)]
    public void PI_SQL_перемикає_лише_не_зєднується(string messageKey, bool expected)
        => Assert.Equal(expected, PiSqlClientDataSource.IsConnectFailure(Rule(messageKey)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public void PI_SQL_автентифікація_не_перемикає()
        => Assert.False(PiSqlClientDataSource.IsConnectFailure(
            new SourceAuthenticationException("ECR-INT-0502", "denied", new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0502.credentialsRefused",
            })));

    /// <summary>
    /// Наскрізно через адаптер PI Web API: основна адреса не з'єднується (три спроби), читання йде із запасної
    /// тим самим секретом, а наступне читання основну вже не пробує.
    /// </summary>
    [Fact(Timeout = 60_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Web_API_мертва_основна_читання_із_запасної_і_основна_більше_не_пробується()
    {
        var handler = new RoutingHandler(primaryFails: true);
        var log = new RecordingLogger<PiWebApiDataSource>();
        var adapter = Adapter(handler, log);

        var request = new CollectionRequest(1, 42, @"\\AF\DB\E1|Flow", From, From.AddHours(1), 100);
        var result = await adapter.ReadAsync(request, CancellationToken.None);

        Assert.Equal(3, result.Points.Count);
        Assert.Equal(EndpointFailover.Secondary, adapter.LastServedBy);
        Assert.Equal(PiWebApiDataSource.MaxAttempts, handler.Hosts.Count(h => h == "pi-a.example"));

        // Секрет поїхав на запасну тим самим заголовком — і не потрапив у журнал.
        Assert.All(handler.Authorizations.Where(a => a is not null), a => Assert.Contains(Secret, a!, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Records, r => r.Message.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Records, r => r.Message.Contains("pi-b.example", StringComparison.Ordinal));

        var primaryCalls = handler.Hosts.Count(h => h == "pi-a.example");
        await adapter.ReadAsync(request with { FromUtc = From.AddHours(1), ToUtc = From.AddHours(2) }, CancellationToken.None);
        Assert.Equal(primaryCalls, handler.Hosts.Count(h => h == "pi-a.example"));
    }

    /// <remarks>⛔ МУТАЦІЯ: <c>IsConnectionFailure</c> → <c>true</c> для будь-якого винятку — 401 іде на запасну.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-462")]
    public async Task Web_API_401_основної_не_перемикає_на_запасну()
    {
        var handler = new RoutingHandler(primaryFails: false, primaryStatus: HttpStatusCode.Unauthorized);
        var adapter = Adapter(handler, new RecordingLogger<PiWebApiDataSource>());

        await Assert.ThrowsAsync<SourceAuthenticationException>(() => adapter.ReadAsync(
            new CollectionRequest(1, 42, @"\\AF\DB\E1|Flow", From, From.AddHours(1), 100), CancellationToken.None));

        Assert.DoesNotContain("pi-b.example", handler.Hosts);
    }

    private static PiWebApiDataSource Adapter(RoutingHandler handler, ILogger<PiWebApiDataSource> log)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Source("https://pi-b.example/piwebapi", "https://pi-a.example/piwebapi", ExternalTransport.PiWebApi));

        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns(Secret);

        return new PiWebApiDataSource(new HttpClient(handler), store, secrets, clients: null, logger: log);
    }

    private static DataSource Source(string? secondary, string primary = "pi-a", ExternalTransport transport = ExternalTransport.PiSqlClient)
    {
        var source = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            transport,
            primary,
            "PiAf.Primary");
        source.Configure(secondary, catalog: null, maxParallel: 4);
        return source;
    }

    private static BusinessRuleException Rule(string messageKey)
        => new("ECR-INT-0503", "refused", new Dictionary<string, object?> { ["messageKey"] = messageKey });

    /// <summary>Обробник HTTP за хостом: основна (<c>pi-a</c>) не з'єднується або відповідає статусом; запасна віддає дані.</summary>
    private sealed class RoutingHandler(bool primaryFails, HttpStatusCode primaryStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            Authorizations.Add(request.Headers.Authorization?.ToString());

            if (host == "pi-a.example")
            {
                return primaryFails
                    ? throw new HttpRequestException("No connection could be made (pi-a.example:443)")
                    : Task.FromResult(new HttpResponseMessage(primaryStatus));
            }

            var path = request.RequestUri.AbsolutePath;
            var json = path.EndsWith("/attributes", StringComparison.Ordinal)
                ? """{"WebId":"W-1","DefaultUnitsName":"m3/h"}"""
                : """{"Items":[{"Timestamp":"2026-01-01T00:00:00Z","Value":1},{"Timestamp":"2026-01-01T00:20:00Z","Value":2},{"Timestamp":"2026-01-01T00:40:00Z","Value":3}]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
