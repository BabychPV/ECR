// tests/Ecr.Api.Tests/SourceEventsApiTests.Validation.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмови мапінгу подій і проби на РЕАЛЬНОМУ HTTP (TESTER-SCENARIOS Н-А3, Н-А4): кожен код, який гайд тестувальника
/// називає, тут перевірено статусом і <c>messageKey</c> відповіді, а не лише доменним тестом.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — точковою правкою):
/// <list type="bullet">
/// <item>у <c>SourceEventMap.AddField</c> звузити умову <c>lookupKind != lookupColumn || target.IsComputed</c> до
/// <c>target.IsComputed</c> — мапінг створюється (200), червоніє
/// <see cref="Кожна_відмова_мапінгу_подій_має_свій_статус_і_ключ_і_нічого_не_пише"/> на <c>eventMapValueKindMismatch</c>;</item>
/// <item>у <c>CreateSourceEventMapHandler</c> прибрати перевірку <c>target.TemplateVersionId != document.TemplateVersionId</c> —
/// той самий тест отримує <c>eventMapColumnNotInTable</c> чи 200 замість <c>eventMapTableNotInDocument</c>;</item>
/// <item>у <c>ProbeSourceEventsHandler</c> підняти стелю вікна з 92 до 93 днів — червоніє
/// <see cref="Проба_поза_межами_вікна_й_ліміту_422_до_звернення_в_джерело"/>.</item>
/// </list>
/// </remarks>
public sealed partial class SourceEventsApiTests
{
    private static readonly Uri EventMaps = new("/api/v1/source-event-maps", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А4")]
    public async Task Кожна_відмова_мапінгу_подій_має_свій_статус_і_ключ_і_нічого_не_пише()
    {
        await using var stand = await ArrangeAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var fixedChain = await builder.BuildAsync(periodKey: 202601, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Fixed);
        var foreignChain = await builder.BuildAsync(periodKey: 202601, columnCount: 2, rowCount: 0, rowMode: TableRowMode.Dynamic);

        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        using var fixedManager = await SignedInAsync(app, ["Integration.Manage"], fixedChain.ProjectId, GrantLevel.Manage);

        // Decimal-колонка мапінгу (будівник): другий стовпець динамічної таблиці стенда.
        var decimalColumn = await ScalarAsync(
            $"SELECT TOP 1 Id FROM cfg.ColumnDef WHERE TableDefId = {stand.TableDefId} AND DataType = {(int)CellDataType.Decimal} ORDER BY Id");

        object[] StartEnd() => [Field(stand.StartColumn, "$start"), Field(stand.EndColumn, "$end")];

        object Map(object[] fields, long? documentId = null, int? tableDefId = null, string? filterAttribute = null, string? filterScope = null, string? filterValue = null)
            => new
            {
                sourceEntityId = stand.EntityId,
                documentId = documentId ?? stand.DocumentId,
                tableDefId = tableDefId ?? stand.TableDefId,
                volumeMode = "None",
                filterAttribute,
                filterScope,
                filterValue,
                fields,
            };

        // 422: подія лягає лише в динамічну таблицю.
        await ExpectMapAsync(
            fixedManager,
            new
            {
                sourceEntityId = stand.EntityId,
                documentId = fixedChain.DocumentId,
                tableDefId = fixedChain.TableDefId,
                volumeMode = "None",
                fields = new[] { Field(fixedChain.ColumnDefIds[0], "$start"), Field(fixedChain.ColumnDefIds[1], "$end") },
            },
            422,
            "err.ECR-INT-0422.eventMapTargetNotDynamic");

        // 422: таблиця іншої версії шаблону, ніж документ.
        await ExpectMapAsync(manager, Map(StartEnd(), tableDefId: foreignChain.TableDefId), 422, "err.ECR-INT-0422.eventMapTableNotInDocument");

        // 422: колонка з чужої таблиці.
        await ExpectMapAsync(
            manager, Map([.. StartEnd(), Field(foreignChain.ColumnDefIds[0], "Volume")]), 422, "err.ECR-INT-0422.eventMapColumnNotInTable");

        // 422: невідомий $-атрибут.
        await ExpectMapAsync(
            manager, Map([.. StartEnd(), Field(decimalColumn, "$foo")]), 422, "err.ECR-INT-0422.eventMapReservedAttributeInvalid");

        // 422: $end у не-дату.
        await ExpectMapAsync(
            manager,
            Map([Field(stand.StartColumn, "$start"), Field(decimalColumn, "$end")]),
            422,
            "err.ECR-INT-0422.eventMapStartEndNotDate");

        // 422: пошук запису довідника на не-Lookup колонці.
        await ExpectMapAsync(
            manager, Map([.. StartEnd(), Field(decimalColumn, "Volume", "LookupByCode")]), 422, "err.ECR-INT-0422.eventMapValueKindMismatch");

        // 422: «Only events where» без значення.
        await ExpectMapAsync(
            manager, Map(StartEnd(), filterAttribute: "Flare", filterScope: "PrimaryElement"), 422, "err.ECR-INT-0422.eventMapFilterIncomplete");

        // 422: явна відповідність значень на полі, що шукає за кодом.
        await ExpectMapAsync(
            manager,
            Map([.. StartEnd(), Field(stand.CategoryColumn, "Category", "LookupByCode", [new { sourceValue = "зима", registryEntryId = stand.V8EntryId }])]),
            422,
            "err.ECR-INT-0422.eventMapValueMapNotAllowed");

        // 409: два атрибути в одну колонку.
        await ExpectMapAsync(
            manager, Map([.. StartEnd(), Field(decimalColumn, "Volume"), Field(decimalColumn, "Mass")]), 409, "err.ECR-INT-0409.eventMapColumnTaken");

        // 409: те саме значення джерела двічі (порівняння без регістру).
        await ExpectMapAsync(
            manager,
            Map(
            [
                .. StartEnd(),
                Field(
                    stand.CategoryColumn,
                    "Category",
                    "ValueMap",
                    [new { sourceValue = "зима", registryEntryId = stand.V8EntryId }, new { sourceValue = " ЗИМА ", registryEntryId = stand.V8EntryId }]),
            ]),
            409,
            "err.ECR-INT-0409.eventMapSourceValueTaken");

        // Жодна відмова нічого не записала.
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE SourceEntityId = {stand.EntityId}"));

        // 404 з ключем на кожному маршруті одного мапінгу.
        var missing = new Uri("/api/v1/source-event-maps/2147483000", UriKind.Relative);
        HttpResponseMessage[] notFound =
        [
            await manager.GetAsync(missing),
            await manager.PutAsJsonAsync(missing, new { volumeMode = "None", isActive = true, fields = StartEnd() }),
            await manager.DeleteAsync(missing),
        ];
        foreach (var response in notFound)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("err.ECR-INT-0404.eventMap", (await JsonAsync(response)).GetProperty("messageKey").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А3")]
    public async Task Проба_поза_межами_вікна_й_ліміту_422_до_звернення_в_джерело()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], null, null);
        var probe = new Uri($"/api/v1/data-sources/{stand.SourceId}/probe-events", UriKind.Relative);
        var to = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        // Справжній адаптер без налаштованого запиту подій відмовив би eventQueryNotConfigured; тут відмова — раніше,
        // на формі запиту, тому ключ саме probeEventsInvalid.
        object[] invalid =
        [
            new { template = "FlareEvent", fromUtc = to.AddDays(-93), toUtc = to },
            new { template = "FlareEvent", maxEvents = 0 },
            new { template = "FlareEvent", maxEvents = 101 },
            new { template = "FlareEvent", fromUtc = to, toUtc = to },
        ];
        foreach (var body in invalid)
        {
            var response = await manager.PostAsJsonAsync(probe, body);
            Assert.True(
                response.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"{JsonSerializer.Serialize(body)} → {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("err.ECR-REQ-0422.probeEventsInvalid", (await JsonAsync(response)).GetProperty("messageKey").GetString());
        }

        // Рівно 92 дні й ліміт 100 — межі включні: відмова вже від адаптера (запит подій не налаштовано), не від форми.
        var edge = await manager.PostAsJsonAsync(probe, new { template = "FlareEvent", fromUtc = to.AddDays(-92), toUtc = to, maxEvents = 100 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edge.StatusCode);
        Assert.NotEqual("err.ECR-REQ-0422.probeEventsInvalid", (await JsonAsync(edge)).GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A6")]
    [Trait("Scenario", "Н-А5")]
    public async Task Розмір_сторінки_таблиці_подій_поза_1_500_дає_422_з_ключем()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        foreach (var limit in new[] { 0, 501 })
        {
            var response = await manager.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events?limit={limit}", UriKind.Relative));
            Assert.True(
                response.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"limit={limit} → {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("err.ECR-REQ-0422.pageSizeOutOfRange", (await JsonAsync(response)).GetProperty("messageKey").GetString());
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await manager.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events?limit=500", UriKind.Relative))).StatusCode);
    }

    private static async Task ExpectMapAsync(HttpClient client, object body, int status, string messageKey)
    {
        var response = await client.PostAsJsonAsync(EventMaps, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{messageKey}: очікувався {status}, прийшло {(int)response.StatusCode}: {text}");
        Assert.Equal(messageKey, JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
    }
}
