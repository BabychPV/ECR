// tests/Ecr.Api.Tests/CellSourceApiTests.cs
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;
using Xunit.Abstractions;
using static Ecr.Api.Tests.PiRawDataStand;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>D-463</c> «Звідки це значення»: <c>/documents/{id}/tables/{instanceId}/cell-source[/points|/series]</c> —
/// доступ рівно до комірки (видимість, проєкт, Deny, рядок, чужий мапінг) з уніфікованою 404, вікно періоду,
/// події покриття лише даних тега, і сталий бюджет звернень до БД.
/// </summary>
/// <remarks>
/// Мутації (локально): (1) прибрати перевірку <c>CanReadColumn</c> у <c>CellSourceHandler.ResolveAsync</c> —
/// червоніє рядок «Deny на колонку»; (2) брати <c>mapId</c> без звірки з мапінгами комірки — червоніє «чужий мапінг»;
/// (3) не обрізати точки межами періоду — червоніє <c>pointCount</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellSourceApiTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();
    private static readonly string[] ComparedFields = ["status", "errorCode", "messageKey", "title", "detail", "documentId"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Читач_комірки_бачить_тег_згортку_вікно_періоду_й_лише_точки_періоду()
    {
        var s = await ArrangeAsync(sql);

        // 20 точок щогодини від початку періоду + 5 до нього + 5 після: у комірку лягають лише 20.
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc.AddHours(-5), TimeSpan.FromHours(1), 5);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc, TimeSpan.FromHours(1), 20);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodToUtc, TimeSpan.FromHours(1), 5);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Document.View"], [s.Doc.ProjectId]));

        var response = await client.GetAsync(new Uri(s.CellSourceUrl(), UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} {body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(PeriodFromUtc, root.GetProperty("periodFromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(PeriodToUtc, root.GetProperty("periodToUtc").GetDateTime().ToUniversalTime());

        var source = Assert.Single(root.GetProperty("sources").EnumerateArray());
        Assert.Equal(s.MapId, source.GetProperty("mapId").GetInt32());
        Assert.Equal(s.Field, source.GetProperty("sourcePath").GetString());
        Assert.Equal("Sum", source.GetProperty("aggregation").GetString());
        Assert.Equal(20, source.GetProperty("pointCount").GetInt64());
        Assert.Equal(s.RunId, source.GetProperty("lastCollection").GetProperty("lastRunId").GetInt64());

        // Точки: лише вікно періоду, новіші першими; графік — та сама кількість.
        var points = JsonDocument.Parse(await client.GetStringAsync(new Uri(
            s.CellSourceUrl().Replace("/cell-source?", "/cell-source/points?", StringComparison.Ordinal) + $"&mapId={s.MapId}&limit=500",
            UriKind.Relative))).RootElement.GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("timestamp").GetDateTime().ToUniversalTime()).ToList();
        Assert.Equal(20, points.Count);
        Assert.All(points, t => Assert.InRange(t, PeriodFromUtc, PeriodToUtc.AddTicks(-1)));
        Assert.Equal(PeriodFromUtc.AddHours(19), points[0]);

        var series = JsonDocument.Parse(await client.GetStringAsync(new Uri(
            s.CellSourceUrl().Replace("/cell-source?", "/cell-source/series?", StringComparison.Ordinal) + $"&mapId={s.MapId}&buckets=10",
            UriKind.Relative))).RootElement;
        Assert.Equal(20, series.GetProperty("totalPoints").GetInt64());
        Assert.InRange(series.GetProperty("buckets").GetArrayLength(), 1, 10);

        // Видима комірка без мапінгу — 200 і порожньо (приховувати нічого).
        var plain = JsonDocument.Parse(await client.GetStringAsync(new Uri(
            s.CellSourceUrl(columnId: s.Doc.ColumnDefIds[2]), UriKind.Relative))).RootElement;
        Assert.Equal(0, plain.GetProperty("sources").GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Невидима_комірка_дає_ту_саму_404_що_й_неіснуючий_документ()
    {
        var s = await ArrangeAsync(sql);
        var other = await ArrangeAsync(sql);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc, TimeSpan.FromHours(1), 3);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignInAsync(app, await UserAsync(sql, ["Document.View"], [s.Doc.ProjectId]));
        using var deniedColumn = await SignInAsync(app, await UserAsync(
            sql, ["Document.View"], [s.Doc.ProjectId], [(ResourceKind.Column, s.MappedColumnId)]));
        using var deniedTable = await SignInAsync(app, await UserAsync(
            sql, ["Document.View"], [s.Doc.ProjectId], [(ResourceKind.Table, s.Doc.TableDefId)]));
        using var noView = await SignInAsync(app, await UserAsync(sql, ["Integration.Manage"], [s.Doc.ProjectId]));

        // Еталон — неіснуючий документ.
        const long Missing = 9_000_000_000_101;
        var expected = await NotFoundAsync(reader, s.CellSourceUrl(documentId: Missing), Missing);

        var cases = new (string Name, HttpClient Client, string Url, long RouteDocument)[]
        {
            ("документ іншого проєкту", reader, other.CellSourceUrl(), other.Doc.DocumentId),
            ("екземпляр чужого документа", reader, s.CellSourceUrl(instanceId: other.Doc.TableInstanceId), s.Doc.DocumentId),
            ("Deny на колонку", deniedColumn, s.CellSourceUrl(), s.Doc.DocumentId),
            ("Deny на таблицю", deniedTable, s.CellSourceUrl(), s.Doc.DocumentId),
            ("неіснуючий рядок", reader, s.CellSourceUrl(rowKey: "NOPE_ROW"), s.Doc.DocumentId),
            ("колонка чужої таблиці", reader, s.CellSourceUrl(columnId: other.MappedColumnId), s.Doc.DocumentId),
            ("чужий мапінг у точках", reader,
                s.CellSourceUrl().Replace("/cell-source?", "/cell-source/points?", StringComparison.Ordinal) + $"&mapId={other.MapId}",
                s.Doc.DocumentId),
            ("чужий мапінг у графіку", reader,
                s.CellSourceUrl().Replace("/cell-source?", "/cell-source/series?", StringComparison.Ordinal) + $"&mapId={other.MapId}",
                s.Doc.DocumentId),
        };

        foreach (var (name, client, url, route) in cases)
        {
            Assert.True(
                expected == await NotFoundAsync(client, url, route),
                $"{name}: тіло 404 відрізняється від неіснуючого документа.\n{app.ErrorsText}");
        }

        // Без Document.View — 403 (право, як у зрізу), а не дані.
        Assert.Equal(HttpStatusCode.Forbidden, (await noView.GetAsync(new Uri(s.CellSourceUrl(), UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Події_покриття_лише_даних_цього_тега_за_період()
    {
        var s = await ArrangeAsync(sql);
        var period = s.Doc.PeriodKey.Value;

        await AddEventAsync(sql, s.EntityId, period, CollectionCoverage.PartialCoverage,
            Envelope("coverageEvents.partialCoverage", ("field", s.Field), ("mapId", s.MapId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("percentGood", "60")));
        await AddEventAsync(sql, s.EntityId, period, CollectionCoverage.SkippedNoData,
            Envelope("coverageEvents.noData", ("field", "other|path"), ("mapId", "999999")));
        await AddEventAsync(sql, s.EntityId, period, CollectionCoverage.ConflictKeptManual,
            Envelope("coverageEvents.keptManual", ("cell", $"{s.RowKey}:C2")));
        await AddEventAsync(sql, s.EntityId, period + 1, CollectionCoverage.SkippedNoData,
            Envelope("coverageEvents.noData", ("field", s.Field), ("mapId", "1")));

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Document.View"], [s.Doc.ProjectId]));

        var source = JsonDocument.Parse(await client.GetStringAsync(new Uri(s.CellSourceUrl(), UriKind.Relative)))
            .RootElement.GetProperty("sources")[0];
        var status = Assert.Single(source.GetProperty("events").EnumerateArray()).GetProperty("status").GetString();
        Assert.Equal(CollectionCoverage.PartialCoverage, status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Звернення_до_БД_не_ростуть_з_кількістю_точок()
    {
        var s = await ArrangeAsync(sql);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc, TimeSpan.FromHours(1), 10);

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300);
        app.Server.PreserveExecutionContext = true;
        using var client = await SignInAsync(app, await UserAsync(sql, ["Document.View"], [s.Doc.ProjectId]));

        using var counter = new SqlClientCommandCounter(new CommandTally(), _ => Measuring.Value is { Value: true });
        var urls = new[]
        {
            s.CellSourceUrl(),
            s.CellSourceUrl().Replace("/cell-source?", "/cell-source/points?", StringComparison.Ordinal) + $"&mapId={s.MapId}&limit=500",
            s.CellSourceUrl().Replace("/cell-source?", "/cell-source/series?", StringComparison.Ordinal) + $"&mapId={s.MapId}&buckets=500",
        };

        async Task<int[]> MeasureAsync()
        {
            var result = new int[urls.Length];
            for (var i = 0; i < urls.Length; i++)
            {
                counter.Tally.Reset();
                var flag = new StrongBox<bool>(true);
                Measuring.Value = flag;
                try
                {
                    var response = await client.GetAsync(new Uri(urls[i], UriKind.Relative));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
                finally
                {
                    flag.Value = false;
                    Measuring.Value = null;
                }

                result[i] = counter.Tally.Snapshot().Total;
            }

            return result;
        }

        await MeasureAsync(); // розігрів: профіль, знімок метаданих, план
        var few = await MeasureAsync();
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc.AddSeconds(30), TimeSpan.FromMinutes(10), 3000);
        var many = await MeasureAsync();

        output.WriteLine($"D-463 звернення (summary/points/series): 10 точок = {string.Join("/", few)}; 3010 точок = {string.Join("/", many)}");
        Assert.Equal(few, many);
        Assert.All(few, n => Assert.InRange(n, 1, 15));
    }

    private static string Envelope(string key, params (string Name, string Value)[] parameters)
        => Ecr.Application.Ports.JobProgressMessageCodec.Encode(new Ecr.Application.Ports.JobProgressMessageEnvelope(
            key, parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)));

    /// <summary>Тіло 404 з id документа маршруту, заміненим на заступник.</summary>
    private static async Task<string> NotFoundAsync(HttpClient client, string url, long routeDocument)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url}: {response.StatusCode} {body}");

        var root = JsonDocument.Parse(body).RootElement;
        var parts = ComparedFields
            .Select(n => root.TryGetProperty(n, out var v) ? $"{n}={v}" : $"{n}=<немає>");
        return System.Text.RegularExpressions.Regex.Replace(
            string.Join("|", parts),
            $@"(?<!\d){routeDocument.ToString(System.Globalization.CultureInfo.InvariantCulture)}(?!\d)",
            "<doc>");
    }
}
