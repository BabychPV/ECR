// tests/Ecr.Api.Tests/SourceEventsApiTests.Refusals.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмови, порожні стани й збої джерела подій на РЕАЛЬНОМУ HTTP без живого PI (TESTER-SCENARIOS Н-А2…Н-А5):
/// те, чого не покривають <see cref="Кожна_відмова_мапінгу_подій_має_свій_статус_і_ключ_і_нічого_не_пише"/> і
/// <see cref="Проба_поза_межами_вікна_й_ліміту_422_до_звернення_в_джерело"/>.
/// </summary>
/// <remarks>
/// Джерело — підробка зі сценарієм (<see cref="ScriptedEventSource"/>): порожній каталог, порожнє вікно, часткова
/// відповідь, обрив і зависання транспорту. Мутаційні докази (кожен — точковою правкою, лише локально):
/// <list type="bullet">
/// <item>у <c>SourceEventMapSupport.RequireShape</c> прибрати перевірку <c>Enum.IsDefined(volumeMode)</c> — POST з
/// <c>volumeMode = 99</c> знову дає 500, червоніє
/// <see cref="Форма_мапінгу_подій_поза_доменом_дає_422_а_не_500_і_нічого_не_пише"/>;</item>
/// <item>у <c>SourceEventReading.RunAsync</c> прибрати <c>catch (OperationCanceledException)</c> — зависле джерело дає
/// 500 замість <c>probeTimeout</c>, червоніє <see cref="Збій_джерела_подій_503_ключем_а_часткова_відповідь_200_з_кодом"/>;</item>
/// <item>у <c>ListSourceEventsHandler</c> кидати 404 й без права інтеграції — червоніє
/// <see cref="Порожні_стани_подій_200_з_порожнім_переліком_а_не_404_чи_500"/>.</item>
/// </list>
/// </remarks>
public sealed partial class SourceEventsApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А4")]
    public async Task Форма_мапінгу_подій_поза_доменом_дає_422_а_не_500_і_нічого_не_пише()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        var start = Field(stand.StartColumn, "$start");
        var end = Field(stand.EndColumn, "$end");
        var longText = new string('x', SourceEventMapLimits.Attribute + 1);

        object Map(object[]? fields, object? volumeMode = null, object? filterScope = null, string? filterAttribute = null, string? filterValue = null)
            => new
            {
                sourceEntityId = stand.EntityId,
                documentId = stand.DocumentId,
                tableDefId = stand.TableDefId,
                volumeMode = volumeMode ?? "None",
                filterAttribute,
                filterScope,
                filterValue,
                fields,
            };

        object Raw(int column, object? attribute, object scope, object kind, object[]? values = null)
            => new { targetColumnDefId = column, sourceAttribute = attribute, attributeScope = scope, valueKind = kind, values };

        var cases = new (string Name, object Body)[]
        {
            ("fields = null", Map(null)),
            ("порожній атрибут", Map([start, end, Raw(stand.NameColumn, "  ", "Event", "Direct")])),
            ("атрибут null", Map([start, end, Raw(stand.NameColumn, null, "Event", "Direct")])),
            ("атрибут задовгий", Map([start, end, Raw(stand.NameColumn, longText, "Event", "Direct")])),
            ("невідома область атрибута", Map([start, end, Raw(stand.NameColumn, "Name", 99, "Direct")])),
            ("невідомий вид значення", Map([start, end, Raw(stand.NameColumn, "Name", "Event", 99)])),
            ("невідомий режим об'єму", Map([start, end], volumeMode: 99)),
            ("невідома область звуження", Map([start, end], filterScope: 99, filterAttribute: "Flare", filterValue: "F-1")),
            ("задовгий атрибут звуження", Map([start, end], filterScope: "Event", filterAttribute: longText, filterValue: "F-1")),
            ("задовге значення звуження", Map([start, end], filterScope: "Event", filterAttribute: "Flare", filterValue: new string('v', SourceEventMapLimits.Value + 1))),
            (
                "порожнє значення відповідності",
                Map([start, end, Raw(stand.CategoryColumn, "Category", "Event", "ValueMap", [new { sourceValue = " ", registryEntryId = stand.V8EntryId }])])
            ),
            (
                "задовге значення відповідності",
                Map(
                [
                    start,
                    end,
                    Raw(stand.CategoryColumn, "Category", "Event", "ValueMap", [new { sourceValue = new string('v', SourceEventMapLimits.Value + 1), registryEntryId = stand.V8EntryId }]),
                ])
            ),
        };

        foreach (var (name, body) in cases)
        {
            var response = await manager.PostAsJsonAsync(EventMaps, body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"POST, {name}: {(int)response.StatusCode} {text}");
            Assert.Equal("err.ECR-REQ-0422.malformedRequest", JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
        }

        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE SourceEntityId = {stand.EntityId}"));

        // PUT — та сама форма; мапінг лишається як був (поля, режим, активність).
        var mapId = await AddMapAsync(stand);
        var one = new Uri($"/api/v1/source-event-maps/{mapId}", UriKind.Relative);
        var before = (await JsonAsync(await manager.GetAsync(one))).GetRawText();

        foreach (var (name, body) in cases)
        {
            var json = JsonSerializer.SerializeToElement(body);
            var update = new
            {
                volumeMode = json.GetProperty("volumeMode"),
                isActive = false,
                filterAttribute = json.GetProperty("filterAttribute"),
                filterScope = json.GetProperty("filterScope"),
                filterValue = json.GetProperty("filterValue"),
                fields = json.GetProperty("fields"),
            };

            var response = await manager.PutAsJsonAsync(one, update);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"PUT, {name}: {(int)response.StatusCode} {text}");
            Assert.Equal("err.ECR-REQ-0422.malformedRequest", JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
        }

        Assert.Equal(before, (await JsonAsync(await manager.GetAsync(one))).GetRawText());
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'ext.SourceEventMap' AND EntityId = {mapId}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А4")]
    public async Task Мапінг_на_неіснуючі_чи_невидимі_цілі_404_ключем_і_нічого_не_пише()
    {
        await using var stand = await ArrangeAsync();
        var foreign = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, columnCount: 2, rowCount: 0, rowMode: TableRowMode.Dynamic);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        object[] StartEnd() => [Field(stand.StartColumn, "$start"), Field(stand.EndColumn, "$end")];

        object Map(int? entity = null, long? document = null, int? table = null, object[]? fields = null)
            => new
            {
                sourceEntityId = entity ?? stand.EntityId,
                documentId = document ?? stand.DocumentId,
                tableDefId = table ?? stand.TableDefId,
                volumeMode = "None",
                fields = fields ?? StartEnd(),
            };

        var decimalColumn = await ScalarAsync(
            $"SELECT TOP 1 Id FROM cfg.ColumnDef WHERE TableDefId = {stand.TableDefId} AND DataType = {(int)CellDataType.Decimal} ORDER BY Id");

        await ExpectMapAsync(manager, Map(entity: 2147483000), 404, "err.ECR-INT-0404.sourceEntity");
        await ExpectMapAsync(manager, Map(document: 9_000_000_000_000), 404, "err.ECR-DOC-0404.document");

        // Документ проєкту без гранта — та сама 404, що й неіснуючий (S18), без projectId у відповіді.
        var hidden = await manager.PostAsJsonAsync(EventMaps, Map(document: foreign.DocumentId, table: foreign.TableDefId));
        var hiddenText = await hidden.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("err.ECR-DOC-0404.document", JsonDocument.Parse(hiddenText).RootElement.GetProperty("messageKey").GetString());
        Assert.False(JsonDocument.Parse(hiddenText).RootElement.TryGetProperty("projectId", out _), hiddenText);

        await ExpectMapAsync(manager, Map(table: 2147483000), 404, "err.ECR-TMPL-0404.table");
        await ExpectMapAsync(manager, Map(fields: [.. StartEnd(), Field(2147483000, "Volume")]), 404, "err.ECR-INT-0405.column");
        await ExpectMapAsync(
            manager,
            Map(fields: [.. StartEnd(), new { targetColumnDefId = decimalColumn, sourceAttribute = "Volume", attributeScope = "Event", valueKind = "Direct", sourceUnitId = 2147483000 }]),
            404,
            "err.ECR-UOM-0404.unitId");

        // Вимкнена сутність — та сама 404, що й неіснуюча.
        await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {stand.EntityId}");
        await ExpectMapAsync(manager, Map(), 404, "err.ECR-INT-0404.sourceEntity");

        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE SourceEntityId = {stand.EntityId}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А5")]
    public async Task Порожні_стани_подій_200_з_порожнім_переліком_а_не_404_чи_500()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var faked = app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IExternalDataSource>();
            services.AddSingleton<IExternalDataSource>(new ScriptedEventSource(
                templates: _ => Task.FromResult<IReadOnlyList<SourceEventTemplate>>([]),
                events: (_, _) => Task.FromResult(new SourceEventResult([], false, null))));
        }));
        using var manager = await SignedInAsync(faked, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        using var reader = await SignedInAsync(faked, [], stand.ProjectId, GrantLevel.Read);

        // Каталог без шаблонів — порожній перелік (не «не налаштовано»).
        var templates = await manager.GetAsync(new Uri($"/api/v1/data-sources/{stand.SourceId}/event-templates", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
        Assert.Equal(0, (await JsonAsync(templates)).GetArrayLength());

        // Вікно без подій — 200, порожньо, не обрізано, без коду часткової відмови; межі вікна — ті, що просили.
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var probe = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative),
            new { template = "FlareEvent", fromUtc = from, toUtc = from.AddDays(1) });
        Assert.True(probe.StatusCode == HttpStatusCode.OK, $"{probe.StatusCode}: {app.ErrorsText}");
        var body = await JsonAsync(probe);
        Assert.Equal(0, body.GetProperty("events").GetArrayLength());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("errorCode").ValueKind);
        Assert.Equal(from, body.GetProperty("fromUtc").GetDateTime().ToUniversalTime());

        // Мапінгів сутності немає — порожній перелік, як і за неіснуючою сутністю.
        foreach (var entity in new[] { stand.EntityId, 2147483000 })
        {
            var maps = await manager.GetAsync(new Uri($"/api/v1/source-event-maps?sourceEntityId={entity}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, maps.StatusCode);
            Assert.Equal(0, (await JsonAsync(maps)).GetArrayLength());
        }

        // Таблиця подій без мапінгів: 200 і нуль — і для адміністратора, і для читача документа.
        foreach (var client in new[] { manager, reader })
        {
            var page = await JsonAsync(await client.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events", UriKind.Relative)));
            Assert.Equal((0, 0), (page.GetProperty("items").GetArrayLength(), page.GetProperty("totalCount").GetInt32()));
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        }

        // Мапінг є, подій ще немає: порожньо; зіпсований курсор — початок списку, не 500.
        await AddMapAsync(stand);
        foreach (var query in new[] { string.Empty, "?cursor=%25%25not-base64", $"?cursor={Uri.EscapeDataString(Convert.ToBase64String("1:x"u8))}" })
        {
            var response = await reader.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events{query}", UriKind.Relative));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{query}: {response.StatusCode}");
            Assert.Equal(0, (await JsonAsync(response)).GetProperty("totalCount").GetInt32());
        }

        // Неіснуюча сутність: адміністратору інтеграції — 404 ключем, читачеві документа — порожньо (без оракула).
        var missingUri = new Uri("/api/v1/sources/2147483000/source-events", UriKind.Relative);
        var missing = await manager.GetAsync(missingUri);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("err.ECR-INT-0404.sourceEntity", (await JsonAsync(missing)).GetProperty("messageKey").GetString());
        var blind = await reader.GetAsync(missingUri);
        Assert.Equal(HttpStatusCode.OK, blind.StatusCode);
        Assert.Equal(0, (await JsonAsync(blind)).GetProperty("totalCount").GetInt32());

        // Невідомий стан у фільтрі — 422 форми запиту, а не «усі стани».
        var badStatus = await reader.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events?status=Nope", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.malformedRequest", (await JsonAsync(badStatus)).GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А3")]
    public async Task Збій_джерела_подій_503_ключем_а_часткова_відповідь_200_з_кодом()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        var mode = "broken";
        using var faked = app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IExternalDataSource>();
            services.RemoveAll<SourceCatalogPolicy>();
            services.AddSingleton(new SourceCatalogPolicy(TimeSpan.FromMilliseconds(300)));
            services.AddSingleton<IExternalDataSource>(new ScriptedEventSource(
                templates: async ct =>
                {
                    if (mode == "hang")
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }

                    throw new HttpRequestException("connection refused");
                },
                events: async (query, ct) =>
                {
                    switch (mode)
                    {
                        case "hang":
                            await Task.Delay(Timeout.Infinite, ct);
                            break;
                        case "partial":
                            return new SourceEventResult(
                                [new SourceEvent("E-1", query.Template, "Flaring", query.FromUtc.AddHours(1), null, null, null, null, [])],
                                true,
                                "ECR-INT-0206");
                        case "unavailable":
                            throw new BusinessRuleException(ErrorCodes.SourceUnavailable, "down", null);
                    }

                    throw new HttpRequestException("connection refused");
                }));
        }));
        using var manager = await SignedInAsync(faked, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        var templatesUri = new Uri($"/api/v1/data-sources/{stand.SourceId}/event-templates", UriKind.Relative);
        var probeUri = new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative);
        var before = await CountsAsync();

        async Task Expect(HttpResponseMessage response, HttpStatusCode status, string messageKey)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"{mode}/{messageKey}: {(int)response.StatusCode} {text}");
            Assert.Equal(messageKey, JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
        }

        // Обрив транспорту: 503 ключем «недоступне», без подробиць винятку назовні.
        await Expect(await manager.GetAsync(templatesUri), HttpStatusCode.ServiceUnavailable, "err.ECR-INT-0503.catalogUnavailable");
        var broken = await manager.PostAsJsonAsync(probeUri, new { template = "FlareEvent" });
        await Expect(broken, HttpStatusCode.ServiceUnavailable, "err.ECR-INT-0503.probeUnavailable");
        Assert.DoesNotContain("connection refused", await broken.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        mode = "unavailable";
        await Expect(await manager.PostAsJsonAsync(probeUri, new { template = "FlareEvent" }), HttpStatusCode.ServiceUnavailable, "err.ECR-INT-0503.probeUnavailable");

        // Зависання: межа часу каталогу — 503 «не відповіло вчасно», а не вічне очікування чи 500.
        mode = "hang";
        await Expect(await manager.GetAsync(templatesUri), HttpStatusCode.ServiceUnavailable, "err.ECR-INT-0503.catalogTimeout");
        await Expect(await manager.PostAsJsonAsync(probeUri, new { template = "FlareEvent" }), HttpStatusCode.ServiceUnavailable, "err.ECR-INT-0503.probeTimeout");

        // Часткова відповідь — 200: події, позначка «обрізано» і код відмови адаптера для банера.
        mode = "partial";
        var partial = await manager.PostAsJsonAsync(probeUri, new { template = "FlareEvent", maxEvents = 1 });
        Assert.True(partial.StatusCode == HttpStatusCode.OK, $"{partial.StatusCode}: {app.ErrorsText}");
        var body = await JsonAsync(partial);
        Assert.Equal("ECR-INT-0206", body.GetProperty("errorCode").GetString());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        var open = Assert.Single(body.GetProperty("events").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("endUtc").ValueKind);

        // Жодна відмова чи часткова проба нічого не записала.
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А5")]
    public async Task Синхронізація_за_паузою_422_лише_перегляд_403_і_жодної_задачі()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        using var viewer = await SignedInAsync(app, ["Integration.View"], stand.ProjectId, GrantLevel.Manage);
        var sync = new Uri($"/api/v1/sources/{stand.EntityId}/source-events/sync", UriKind.Relative);
        var jobs = $"SELECT COUNT(*) FROM itg.JobProgress WHERE TargetKey LIKE '%~source-events-e{stand.EntityId}'";

        var mapId = await AddMapAsync(stand);

        // Перегляд інтеграції — бачить мапінг, але не ставить синхронізацію.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(new Uri($"/api/v1/source-event-maps/{mapId}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(sync, new { })).StatusCode);

        // Пауза через PUT: мапінг є, але неактивний — 422 eventSyncNoMap, як і без мапінгу.
        var paused = await manager.PutAsJsonAsync(
            new Uri($"/api/v1/source-event-maps/{mapId}", UriKind.Relative),
            new { volumeMode = "None", isActive = false, fields = new[] { Field(stand.StartColumn, "$start"), Field(stand.EndColumn, "$end") } });
        Assert.True(paused.StatusCode == HttpStatusCode.OK, $"{paused.StatusCode}: {app.ErrorsText}");
        Assert.False((await JsonAsync(paused)).GetProperty("isActive").GetBoolean());

        var refused = await manager.PostAsJsonAsync(sync, new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("err.ECR-INT-0422.eventSyncNoMap", (await JsonAsync(refused)).GetProperty("messageKey").GetString());

        // Вимкнена сутність — 404 ключем, навіть з активним мапінгом.
        await ExecuteAsync($"UPDATE ext.SourceEventMap SET IsActive = 1 WHERE Id = {mapId}");
        await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {stand.EntityId}");
        var disabled = await manager.PostAsJsonAsync(sync, new { });
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal("err.ECR-INT-0404.sourceEntity", (await JsonAsync(disabled)).GetProperty("messageKey").GetString());

        Assert.Equal(0, await ScalarAsync(jobs));
    }

    /// <summary>Межі довжин домену мапінгу подій — щоб тест не тримав власних «магічних» чисел.</summary>
    private static class SourceEventMapLimits
    {
        public const int Attribute = Ecr.Domain.Entities.External.SourceEventMap.MaxAttributeLength;

        public const int Value = Ecr.Domain.Entities.External.SourceEventMap.MaxValueLength;
    }

    /// <summary>Джерело подій зі сценарієм на кожен виклик; у мережу не ходить.</summary>
    private sealed class ScriptedEventSource(
        Func<CancellationToken, Task<IReadOnlyList<SourceEventTemplate>>> templates,
        Func<SourceEventQuery, CancellationToken, Task<SourceEventResult>> events) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SourceEventTemplate>> DiscoverEventTemplatesAsync(int dataSourceId, CancellationToken ct)
            => templates(ct);

        public Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct)
            => events(query, ct);
    }
}
