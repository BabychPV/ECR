using System.Globalization;
using System.Net;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Фейковий PI Web API для P7: <c>attributes?path=</c>, <c>streams/{webId}/recorded</c>
/// і <c>streamsets/recorded</c> з <c>maxCount</c> на КОЖЕН потік; рахує запити
/// й найбільшу кількість одночасних.
/// </summary>
/// <remarks>
/// Без <see cref="Data"/> кожен атрибут має точки на :00 і :30 кожної години
/// від <see cref="DataStart"/> на 120 діб. Значення точки — її порядковий номер
/// у потоці. WebId атрибута — <c>W-</c> + шлях (+ <c>x</c> до <see cref="WebIdPadding"/>).
/// </remarks>
internal sealed class FakePiWebApiServer : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly HashSet<string> refusedOnce = [];
    private int inFlight;

    public static DateTime DataStart { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public TimeSpan Delay { get; init; }

    public Dictionary<string, List<DateTime>>? Data { get; init; }

    public int WebIdPadding { get; init; }

    /// <summary>Шляхи, на які пошук атрибута відповідає 404.</summary>
    public HashSet<string> MissingPaths { get; } = [];

    /// <summary>Шляхи, перший пошук яких відповідає 400.</summary>
    public HashSet<string> RefuseFirstLookup { get; } = [];

    /// <summary>WebId, чий потік у <c>streamsets</c> приходить з <c>Errors</c>.</summary>
    public HashSet<string> FailingStreams { get; } = [];

    /// <summary><c>streamsets/recorded</c> відповідає 401.</summary>
    public bool RefuseStreamsets { get; set; }

    /// <summary>Шлях і запит кожного звернення, розекрановані.</summary>
    public List<string> Requests { get; } = [];

    public List<int> WebIdsPerStreamsetRequest { get; } = [];

    public int MaxInFlight { get; private set; }

    public static DataSource Source() => new(
        EcrCode.Create("PIAF"),
        new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
        ExternalTransport.PiWebApi,
        "https://pi.example",
        "PiAf.Primary");

    public static ISecretProvider Secrets()
    {
        var secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns(string.Empty);
        return secrets;
    }

    public int Count(string fragment)
    {
        lock (gate)
        {
            return Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;

        lock (gate)
        {
            Requests.Add(Uri.UnescapeDataString(uri.PathAndQuery.TrimStart('/')));
            MaxInFlight = Math.Max(MaxInFlight, ++inFlight);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Respond(uri);
        }
        finally
        {
            lock (gate)
            {
                inFlight--;
            }
        }
    }

    private HttpResponseMessage Respond(Uri uri)
    {
        var query = Query(uri);
        var path = uri.AbsolutePath.TrimStart('/');

        if (path == "attributes")
        {
            var attribute = query["path"][0];

            if (MissingPaths.Contains(attribute))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            lock (gate)
            {
                if (RefuseFirstLookup.Contains(attribute) && refusedOnce.Add(attribute))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }

            return Json($$"""{"WebId":"{{WebId(attribute)}}"}""");
        }

        var start = DateTime.Parse(query["startTime"][0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var end = DateTime.Parse(query["endTime"][0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var max = int.Parse(query["maxCount"][0], CultureInfo.InvariantCulture);

        if (path.StartsWith("streams/", StringComparison.Ordinal))
        {
            return Json(Values(path.Split('/')[1], start, end, max));
        }

        if (RefuseStreamsets)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var webIds = query["webId"];

        lock (gate)
        {
            WebIdsPerStreamsetRequest.Add(webIds.Count);
        }

        var streams = webIds.Select(w => FailingStreams.Contains(w)
            ? $$"""{"WebId":"{{w}}","Items":[],"Errors":["PI Point not found."]}"""
            : Values(w, start, end, max).Insert(1, "\"WebId\":\"" + w + "\","));

        return Json("{\"Items\":[" + string.Join(',', streams) + "]}");
    }

    private string WebId(string attribute)
        => "W-" + attribute + new string('x', Math.Max(0, WebIdPadding - attribute.Length - 2));

    private string Values(string webId, DateTime start, DateTime end, int max)
    {
        var attribute = webId[2..].TrimEnd('x');
        var stamps = Data is { } data
            ? data[attribute]
            : [.. Enumerable.Range(0, 120 * 48).Select(i => DataStart.AddMinutes(30 * i))];

        var items = stamps
            .Select((stamp, index) => (stamp, index))
            .Where(p => p.stamp >= start && p.stamp < end)
            .Take(max)
            .Select(p => $$"""{"Timestamp":"{{p.stamp:yyyy-MM-ddTHH:mm:ssZ}}","Value":{{p.index}}}""");

        return "{\"Items\":[" + string.Join(',', items) + "]}";
    }

    private static Dictionary<string, List<string>> Query(Uri uri)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);

            if (!result.TryGetValue(key, out var values))
            {
                result[key] = values = [];
            }

            values.Add(Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty));
        }

        return result;
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
