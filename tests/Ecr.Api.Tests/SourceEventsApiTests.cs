// tests/Ecr.Api.Tests/SourceEventsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// API подій джерела на РЕАЛЬНОМУ HTTP і SQL Server (HSE301 A6): права, каталог і проба без запису, CRUD мапінгу
/// зі слідом у журналі, таблиця подій з фільтрами й курсором, постановка синхронізації.
/// </summary>
/// <remarks>
/// ⚠ Джерело подій — підробка (<see cref="FakeEventSource"/>) там, де потрібна відповідь; без підробки справжній
/// адаптер PI Web API не читає подій і відмовляє 422. Що коду <c>EF-…</c> і зв'язки створює сама синхронізація —
/// доводить <c>SourceEventSyncJobTests</c>; тут зв'язки кладуться напряму, щоб перевірити ЧИТАННЯ.
///
/// Мутаційні докази (A6), кожен — точковою правкою:
/// <list type="bullet">
/// <item>у <c>SourceEventMapSupport.RequireProjectManage</c> замінити <c>&lt; Manage</c> на <c>&lt; Read</c> — червоніє
/// <see cref="CRUD_мапінгу_на_реальній_базі_із_заміною_полів_слідом_і_забороною_видалення_зі_зв_язками"/>;</item>
/// <item>у <c>ListSourceEventsHandler</c> не відсікати мапінги невидимих документів
/// (<c>read.IsAllowed || …</c>) — червоніє
/// <see cref="Таблиця_подій_фільтри_порядок_пояс_і_курсор_а_невидиме_не_існує"/>;</item>
/// <item>у <c>ProbeSourceEventsHandler</c> пускати й <c>Integration.View</c> — червоніє
/// <see cref="Проба_і_каталог_з_підробкою_віддають_події_і_нічого_не_пишуть"/>;</item>
/// <item>у <c>UpdateSourceEventMapHandler</c> прибрати <c>store.ReleaseFields</c> — заміна полів падає в EF
/// («розірваний обов'язковий зв'язок», усі FK — <c>Restrict</c>), червоніє CRUD-тест;</item>
/// <item>у <c>SyncSourceEventsHandler</c> прибрати перевірку активного мапінгу — червоніє
/// <see cref="Синхронізація_вимагає_активного_мапінгу_ставить_одну_задачу_на_ціль_сутності"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class SourceEventsApiTests(SqlServerFixture sql)
{
    private const string Password = "Src-Events-Api-2026!";

    private static readonly DateTime L1Start = new(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Без_права_403_на_кожному_адміністративному_маршруті_а_таблиця_подій_порожня()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ["System.ViewHealth"], null, null);

        HttpResponseMessage[] denied =
        [
            await client.GetAsync(new Uri($"/api/v1/data-sources/{stand.SourceId}/event-templates", UriKind.Relative)),
            await client.PostAsJsonAsync(new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative), new { template = "X" }),
            await client.PostAsJsonAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events/sync", UriKind.Relative), new { }),
            await client.GetAsync(new Uri("/api/v1/source-event-maps", UriKind.Relative)),
            await client.GetAsync(new Uri("/api/v1/source-event-maps/1", UriKind.Relative)),
            await client.PostAsJsonAsync(new Uri("/api/v1/source-event-maps", UriKind.Relative), Create(stand, stand.EntityId)),
            await client.PutAsJsonAsync(new Uri("/api/v1/source-event-maps/1", UriKind.Relative), new { volumeMode = "None", isActive = true, fields = Array.Empty<object>() }),
            await client.DeleteAsync(new Uri("/api/v1/source-event-maps/1", UriKind.Relative)),
        ];

        Assert.All(denied, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        // Таблиця подій — не адміністративна: без гранта на документ вона просто порожня, без оракула.
        var events = await client.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Empty((await JsonAsync(events)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Справжній_адаптер_без_запиту_подій_дає_422_ключем_а_сміття_у_пробі_422_і_нічого_не_пишеться()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], null, null);
        var before = await CountsAsync();

        var templates = await manager.GetAsync(new Uri($"/api/v1/data-sources/{stand.SourceId}/event-templates", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, templates.StatusCode);
        Assert.Equal("ECR-INT-0422", (await JsonAsync(templates)).GetProperty("errorCode").GetString());

        var probe = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative), new { template = "FlareEvent" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, probe.StatusCode);
        Assert.Equal("ECR-INT-0422", (await JsonAsync(probe)).GetProperty("errorCode").GetString());

        var invalid = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative),
            new { template = "FlareEvent", maxEvents = 500 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.probeEventsInvalid", (await JsonAsync(invalid)).GetProperty("messageKey").GetString());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await manager.GetAsync(new Uri("/api/v1/data-sources/999999/event-templates", UriKind.Relative))).StatusCode);

        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Проба_і_каталог_з_підробкою_віддають_події_і_нічого_не_пишуть()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var faked = app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IExternalDataSource>();
            services.AddSingleton<IExternalDataSource>(new FakeEventSource());
        }));
        using var manager = await SignedInAsync(faked, ["Integration.Manage"], null, null);
        using var viewer = await SignedInAsync(faked, ["Integration.View"], null, null);
        var before = await CountsAsync();

        var templates = await viewer.GetAsync(new Uri($"/api/v1/data-sources/{stand.SourceId}/event-templates", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
        var first = (await JsonAsync(templates)).EnumerateArray().First();
        Assert.Equal("FlareEvent", first.GetProperty("templateName").GetString());
        Assert.Equal("Category", first.GetProperty("attributes")[0].GetProperty("name").GetString());

        // View читає каталог, але не ходить у джерело за подіями.
        var probeUri = new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync(probeUri, new { template = "FlareEvent" })).StatusCode);

        var probe = await manager.PostAsJsonAsync(
            probeUri,
            new { template = "FlareEvent", attributes = new[] { new { name = "Category", scope = "Event" } }, maxEvents = 5 });
        Assert.True(probe.StatusCode == HttpStatusCode.OK, $"{probe.StatusCode}: {app.ErrorsText}");
        var body = await JsonAsync(probe);
        var eventJson = Assert.Single(body.GetProperty("events").EnumerateArray());
        Assert.Equal("E-1", eventJson.GetProperty("eventId").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());

        // Проба — лише читання: ні зв'язків, ні збирання, ні рядків.
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    public async Task CRUD_мапінгу_на_реальній_базі_із_заміною_полів_слідом_і_забороною_видалення_зі_зв_язками()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Write);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        var maps = new Uri("/api/v1/source-event-maps", UriKind.Relative);

        // Без гранта Manage на проєкт документа — 403, і нічого не записано.
        var denied = await writer.PostAsJsonAsync(maps, Create(stand, stand.EntityId));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", (await JsonAsync(denied)).GetProperty("messageKey").GetString());
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE SourceEntityId = {stand.EntityId}"));

        // Без $end — 422 з ключем домену; нічого не записано.
        var noEnd = await manager.PostAsJsonAsync(maps, Create(stand, stand.EntityId, withEnd: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noEnd.StatusCode);
        Assert.Equal("err.ECR-INT-0422.eventMapStartEndRequired", (await JsonAsync(noEnd)).GetProperty("messageKey").GetString());

        var created = await manager.PostAsJsonAsync(maps, Create(stand, stand.EntityId));
        Assert.True(created.StatusCode == HttpStatusCode.OK, $"{created.StatusCode}: {app.ErrorsText}");
        var id = (await JsonAsync(created)).GetProperty("id").GetInt32();
        Assert.Equal(4, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventFieldMap WHERE SourceEventMapId = {id}"));

        // Другий мапінг тієї самої трійки — 409.
        var duplicate = await manager.PostAsJsonAsync(maps, Create(stand, stand.EntityId));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("err.ECR-INT-0409.eventMapExists", (await JsonAsync(duplicate)).GetProperty("messageKey").GetString());

        // Читання: перелік за сутністю й один мапінг.
        var listed = await manager.GetAsync(new Uri($"/api/v1/source-event-maps?sourceEntityId={stand.EntityId}", UriKind.Relative));
        Assert.Equal(id, Assert.Single((await JsonAsync(listed)).EnumerateArray()).GetProperty("id").GetInt32());
        var one = await JsonAsync(await manager.GetAsync(new Uri($"/api/v1/source-event-maps/{id}", UriKind.Relative)));
        Assert.Equal("Category", one.GetProperty("fields").EnumerateArray().Last().GetProperty("sourceAttribute").GetString());

        // Повна заміна тих самих колонок (унікальний індекс «мапінг+колонка»): режим, пауза, звуження, відповідність.
        var one_uri = new Uri($"/api/v1/source-event-maps/{id}", UriKind.Relative);
        var updated = await manager.PutAsJsonAsync(one_uri, Update(stand, stand.V8EntryId));
        Assert.True(updated.StatusCode == HttpStatusCode.OK, $"{updated.StatusCode}: {app.ErrorsText}");
        var after = await JsonAsync(updated);
        Assert.Equal(("RowWindow", false, "Flare"), (
            after.GetProperty("volumeMode").GetString(),
            after.GetProperty("isActive").GetBoolean(),
            after.GetProperty("filterAttribute").GetString()));
        Assert.Equal(4, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventFieldMap WHERE SourceEventMapId = {id}"));
        Assert.Equal(
            1,
            await ScalarAsync(
                "SELECT COUNT(*) FROM ext.SourceEventValueMap v JOIN ext.SourceEventFieldMap f ON f.Id = v.SourceEventFieldMapId "
                + $"WHERE f.SourceEventMapId = {id} AND v.SourceValue = N'зима'"));

        // Запис чужого довідника — 404, а мапінг лишається таким, яким був.
        var foreign = await manager.PutAsJsonAsync(one_uri, Update(stand, stand.ForeignEntryId));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("err.ECR-INT-0405.registryEntry", (await JsonAsync(foreign)).GetProperty("messageKey").GetString());

        // Без обов'язкового $end — 422, поля не порожніють і не змінюються.
        var broken = await manager.PutAsJsonAsync(
            one_uri,
            new
            {
                volumeMode = "None",
                isActive = true,
                fields = new[] { Field(stand.StartColumn, "$start") },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, broken.StatusCode);
        Assert.Equal(4, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventFieldMap WHERE SourceEventMapId = {id}"));

        // Мапінг зі зв'язком не видаляється — 409; вихід — пауза.
        var linkId = await AddLinkAsync(id, "E-DEL", L1Start, written: false);
        var refused = await manager.DeleteAsync(one_uri);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var refusal = await JsonAsync(refused);
        Assert.Equal("err.ECR-INT-0409.eventMapHasLinks", refusal.GetProperty("messageKey").GetString());
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE Id = {id}"));

        await ExecuteAsync($"DELETE FROM ext.SourceEventLink WHERE Id = {linkId}");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(one_uri)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(one_uri)).StatusCode);
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventFieldMap WHERE SourceEventMapId = {id}"));

        // Слід у журналі структурних змін: створення, зміна (старий і новий стан), видалення.
        var audit = await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.SourceEventMap", id);
        Assert.Equal(["CreateSourceEventMap", "UpdateSourceEventMap", "DeleteSourceEventMap"], audit.Select(a => a.Operation));
        Assert.Null(audit[0].OldJson);
        Assert.Contains("\"isActive\":true", audit[1].OldJson, StringComparison.Ordinal);
        Assert.Contains("\"isActive\":false", audit[1].NewJson, StringComparison.Ordinal);
        Assert.Null(audit[2].NewJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Таблиця_подій_фільтри_порядок_пояс_і_курсор_а_невидиме_не_існує()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);

        // Мапінг — прямо в базі (створення через API перевіряє інший тест).
        var mapId = await AddMapAsync(stand);
        await AddLinkAsync(mapId, "L1", L1Start, written: true, kept: "[\"VOLUME\"]");
        await AddLinkAsync(mapId, "L2", new DateTime(2026, 1, 30, 10, 0, 0, DateTimeKind.Utc), written: false, open: true);
        await AddLinkAsync(mapId, "L3", new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc), written: true, missing: true);
        await AddLinkAsync(mapId, "L4", new DateTime(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc), written: false, closed: true);
        await AddLinkAsync(mapId, "L5", new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc), written: true, unmapped: "[{\"column\":\"CATEGORY\",\"value\":\"V99\"}]");

        using var reader = await SignedInAsync(app, [], stand.ProjectId, GrantLevel.Read);
        var basePath = $"/api/v1/sources/{stand.EntityId}/source-events";

        var all = await JsonAsync(await reader.GetAsync(new Uri(basePath, UriKind.Relative)));
        Assert.Equal(5, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(["L4", "L2", "L1", "L3", "L5"], Ids(all));

        // Час у поясі проєкту (+05:00) поруч із UTC: 19:00Z 31 січня — вже лютий за поясом.
        var l4 = all.GetProperty("items")[0];
        Assert.Equal("Asia/Atyrau", l4.GetProperty("timeZoneId").GetString());
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(5)), l4.GetProperty("startLocal").GetDateTimeOffset());
        Assert.Equal("PeriodClosed", l4.GetProperty("status").GetString());
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 15, 0, TimeSpan.FromHours(5)), l4.GetProperty("endLocal").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("items")[1].GetProperty("endLocal").ValueKind);

        var l1 = all.GetProperty("items")[2];
        Assert.Equal(("EF-L1", 202601, stand.InstanceId, stand.DocumentId), (
            l1.GetProperty("rowKey").GetString(),
            l1.GetProperty("periodKey").GetInt32(),
            l1.GetProperty("tableInstanceId").GetInt64(),
            l1.GetProperty("documentId").GetInt64()));
        Assert.Equal("PLANT/L1", l1.GetProperty("primaryElement").GetString());
        Assert.Equal("VOLUME", l1.GetProperty("keptManual")[0].GetString());
        Assert.Equal("V99", all.GetProperty("items")[4].GetProperty("unmapped")[0].GetProperty("value").GetString());

        // Фільтри: стан (кілька), період, вікно за початком події.
        Assert.Equal(["L3"], Ids(await Query(reader, $"{basePath}?status=Missing")));
        Assert.Equal(["L2", "L3"], Ids(await Query(reader, $"{basePath}?status=Missing&status=Open")));
        Assert.Equal(["L1", "L3", "L5"], Ids(await Query(reader, $"{basePath}?periodKey=202601")));
        Assert.Equal(
            ["L2", "L1"],
            Ids(await Query(reader, $"{basePath}?fromUtc=2026-01-28T00:00:00Z&toUtc=2026-01-31T00:00:00Z")));
        Assert.Equal(["L5"], Ids(await Query(reader, $"{basePath}?status=Unmapped")));

        // Курсор: три сторінки по два, повний обхід без повторів, totalCount не змінюється.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Query(reader, $"{basePath}?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            seen.AddRange(Ids(page));
            Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(["L4", "L2", "L1", "L3", "L5"], seen);

        // Невидиме не існує: без гранта — порожньо, а прямий mapId — 404 (так само, як неіснуючий).
        using var stranger = await SignedInAsync(app, [], null, null);
        var empty = await JsonAsync(await stranger.GetAsync(new Uri(basePath, UriKind.Relative)));
        Assert.Equal((0, 0), (empty.GetProperty("items").GetArrayLength(), empty.GetProperty("totalCount").GetInt32()));
        var hidden = await stranger.GetAsync(new Uri($"{basePath}?mapId={mapId}", UriKind.Relative));
        var missing = await stranger.GetAsync(new Uri($"{basePath}?mapId=999999", UriKind.Relative));
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (hidden.StatusCode, missing.StatusCode));

        // Розмір сторінки поза межами — 422.
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await reader.GetAsync(new Uri($"{basePath}?limit=501", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Синхронізація_вимагає_активного_мапінгу_ставить_одну_задачу_на_ціль_сутності()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        var sync = new Uri($"/api/v1/sources/{stand.EntityId}/source-events/sync", UriKind.Relative);

        // Без активного мапінгу — 422 з ключем, задача не ставиться.
        var noMap = await manager.PostAsJsonAsync(sync, new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noMap.StatusCode);
        Assert.Equal("err.ECR-INT-0422.eventSyncNoMap", (await JsonAsync(noMap)).GetProperty("messageKey").GetString());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await manager.PostAsJsonAsync(new Uri("/api/v1/sources/999999/source-events/sync", UriKind.Relative), new { })).StatusCode);

        await AddMapAsync(stand);
        var accepted = await manager.PostAsJsonAsync(sync, new { });
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, $"{accepted.StatusCode}: {app.ErrorsText}");
        Assert.False(string.IsNullOrWhiteSpace((await JsonAsync(accepted)).GetProperty("jobId").GetString()));
    }

    // ── стенд ────────────────────────────────────────────────────────────────

    private static object Field(int column, string attribute, string kind = "Direct", object[]? values = null)
        => new { targetColumnDefId = column, sourceAttribute = attribute, attributeScope = "Event", valueKind = kind, values };

    private static object Create(Stand stand, int entityId, bool withEnd = true)
    {
        var fields = new List<object> { Field(stand.StartColumn, "$start") };
        if (withEnd)
        {
            fields.Add(Field(stand.EndColumn, "$end"));
        }

        fields.Add(Field(stand.NameColumn, "$name"));
        fields.Add(Field(stand.CategoryColumn, "Category", "LookupByCode"));

        return new
        {
            sourceEntityId = entityId,
            documentId = stand.DocumentId,
            tableDefId = stand.TableDefId,
            volumeMode = "EventAttribute",
            fields,
        };
    }

    private static object Update(Stand stand, long entryId)
        => new
        {
            volumeMode = "RowWindow",
            isActive = false,
            filterAttribute = "Flare",
            filterScope = "PrimaryElement",
            filterValue = "FL-370",
            fields = new[]
            {
                Field(stand.StartColumn, "$start"),
                Field(stand.EndColumn, "$end"),
                Field(stand.NameColumn, "$name"),
                Field(stand.CategoryColumn, "Category", "ValueMap", [new { sourceValue = "зима", registryEntryId = entryId }]),
            },
        };

    private static string[] Ids(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sourceEventId").GetString()!)];

    private static async Task<JsonElement> Query(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private sealed record Stand(
        int ProjectId,
        int TableDefId,
        long DocumentId,
        long InstanceId,
        int SourceId,
        int EntityId,
        int StartColumn,
        int EndColumn,
        int NameColumn,
        int CategoryColumn,
        long V8EntryId,
        long ForeignEntryId,
        Func<ValueTask> Cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cleanup();
    }

    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            periodKey: 202601, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var registry = new RegistryDef(EcrCode.Create($"A6_{tag}"), Name("categories"), isTemporal: false);
        var foreign = new RegistryDef(EcrCode.Create($"A6F_{tag}"), Name("foreign"), isTemporal: false);
        db.RegistryDefs.AddRange(registry, foreign);

        var source = new DataSource(
            EcrCode.Create($"A6SRC{tag}"), Name("HSE301-A6"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        await db.SaveChangesAsync(CancellationToken.None);

        var v8 = new RegistryEntry(registry.Id, EcrCode.Create("V8"), Name("V8"));
        var other = new RegistryEntry(foreign.Id, EcrCode.Create("X1"), Name("X1"));
        db.RegistryEntries.AddRange(v8, other);

        var entity = new SourceEntity(source.Id, $"FlareEvent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        // Третя колонка будівника (Decimal) — категорія: Lookup на власний довідник.
        await ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Lookup}, LookupRegistryDefId = {registry.Id} WHERE Id = {chain.ColumnDefIds[2]}");

        return new Stand(
            chain.ProjectId,
            chain.TableDefId,
            chain.DocumentId,
            chain.TableInstanceId,
            source.Id,
            entity.Id,
            start.Id,
            end.Id,
            chain.ColumnDefIds[0],
            chain.ColumnDefIds[2],
            v8.Id,
            other.Id,
            async () =>
            {
                // Активна сутність у спільній базі фарбувала б SourcesHealthCheck інших тестів.
                await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {entity.Id}");
                await ExecuteAsync($"UPDATE ext.DataSource SET IsActive = 0 WHERE Id = {source.Id}");
            });
    }

    private async Task<int> AddMapAsync(Stand stand)
    {
        await using var db = NewDb();
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == stand.TableDefId);
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == stand.TableDefId)
            .ToDictionaryAsync(c => c.Id);

        var map = SourceEventMap.Create(
            stand.EntityId,
            stand.DocumentId,
            table,
            [
                new(columns[stand.StartColumn], SourceEventMap.StartAttribute),
                new(columns[stand.EndColumn], SourceEventMap.EndAttribute),
            ],
            SourceEventVolumeMode.None);
        db.SourceEventMaps.Add(map);
        await db.SaveChangesAsync();

        return map.Id;
    }

    private async Task<long> AddLinkAsync(
        int mapId,
        string eventId,
        DateTime start,
        bool written,
        bool open = false,
        bool missing = false,
        bool closed = false,
        string? kept = null,
        string? unmapped = null)
    {
        await using var db = NewDb();
        var instance = await db.TableInstances.AsNoTracking()
            .Join(db.SourceEventMaps, t => t.DocumentId, m => m.DocumentId, (t, m) => new { t.Id, t.PeriodKeyValue, MapId = m.Id })
            .FirstAsync(x => x.MapId == mapId);
        var now = new DateTime(2026, 2, 5, 9, 0, 0, DateTimeKind.Utc);
        var observation = new SourceEventObservation(
            eventId, $"Flaring {eventId}", start, open ? null : start.AddMinutes(15), null, $"plant/{eventId}");

        SourceEventLink link;
        if (written)
        {
            link = SourceEventLink.FirstSeenWritten(
                mapId, observation, new SourceEventRowRef(instance.PeriodKeyValue, instance.Id, $"EF-{eventId}"), kept, unmapped, now);
            if (missing)
            {
                link.MarkMissing(now);
            }
        }
        else
        {
            link = SourceEventLink.FirstSeenUnwritten(
                mapId, observation, open ? SourceEventLinkStatus.Open : closed ? SourceEventLinkStatus.PeriodClosed : SourceEventLinkStatus.RowLimit, now);
        }

        db.SourceEventLinks.Add(link);
        await db.SaveChangesAsync();

        return link.Id;
    }

    /// <summary>Користувач із заданими функціональними правами і, якщо задано, грантом на проєкт.</summary>
    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, string[] permissions, int? projectId, GrantLevel? level)
        => await SignedInAsync(app.CreateClient(), permissions, projectId, level, app.ErrorsText);

    private async Task<HttpClient> SignedInAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string[] permissions, int? projectId, GrantLevel? level)
        => await SignedInAsync(app.CreateClient(), permissions, projectId, level, "(логи — на основній фабриці)");

    private async Task<HttpClient> SignedInAsync(
        HttpClient client, string[] permissions, int? projectId, GrantLevel? level, string errors)
    {
        var name = $"sev_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("A6 events test"));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            if (projectId is { } project && level is { } grant)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, project, grant));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {errors}");

        return client;
    }

    /// <summary>Лічильники всього, що проба чи каталог могли б записати.</summary>
    private async Task<int[]> CountsAsync()
        =>
        [
            await ScalarAsync("SELECT COUNT(*) FROM ext.SourceEventLink"),
            await ScalarAsync("SELECT COUNT(*) FROM ext.SourceEventMap"),
            await ScalarAsync("SELECT COUNT(*) FROM ext.RawDataPoint"),
            await ScalarAsync("SELECT COUNT(*) FROM itg.CollectionRun"),
            await ScalarAsync("SELECT COUNT(*) FROM doc.TableRow"),
        ];

    private async Task<int> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = text;

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Джерело подій із однією подією й одним шаблоном; у мережу не ходить.</summary>
    private sealed class FakeEventSource : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SourceEventTemplate>> DiscoverEventTemplatesAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SourceEventTemplate>>(
            [
                new SourceEventTemplate(
                    "FlareEvent",
                    [new SourceEventAttributeDescriptor("Category", SourceEventAttributeScope.Event, null, "String")]),
            ]);

        public Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct)
            => Task.FromResult(new SourceEventResult(
                [new SourceEvent("E-1", query.Template, "Flaring", query.FromUtc.AddDays(1), query.FromUtc.AddDays(1).AddMinutes(15), null, null, null, [])],
                false,
                null));
    }
}
