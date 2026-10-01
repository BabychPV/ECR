// tests/Ecr.Api.Tests/RowWindowMapsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// CRUD прив'язок PI за вікном рядка (<c>/api/v1/row-window-maps</c>, HSE301 A1, FEATURE-HSE301-VIEW §4.4) на
/// РЕАЛЬНОМУ HTTP і SQL Server: права, грант на проєкт колонки-цілі, відмови 404/409/422, повна заміна (PUT) із
/// джерелами, оптимістичне блокування, видалення із залежністю від провенансу, слід у журналі й ізоляція чужих
/// прив'язок.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — точковою правкою):
/// <list type="bullet">
/// <item>у <c>RowWindowMapSupport.RequireProjectGrantsAsync</c> замінити <c>&lt; Manage</c> на <c>&lt; Read</c> —
/// червоніє <see cref="Створення_права_грант_відмови_і_умовчання_на_реальній_базі"/>;</item>
/// <item>у <c>RowWindowMapSupport.RequireShape</c> прибрати перевірку шляху атрибута — у тому ж тесті замість 422
/// відповідь 500 (домен кидає <c>ArgumentException</c>);</item>
/// <item>у <c>UpdateRowWindowMapHandler</c> прибрати <c>store.ReleaseSources</c> — заміна джерел падає в EF
/// («розірваний обов'язковий зв'язок»), червоніє
/// <see cref="Повна_заміна_джерел_пауза_застарілий_rowVersion_і_ізоляція_сусідньої_прив_язки"/>;</item>
/// <item>у тому ж обробнику не порівнювати <c>RowVersion</c> — той самий тест не отримує 409;</item>
/// <item>у <c>DeleteRowWindowMapHandler</c> прибрати перевірку <c>CountValuesAsync</c> — видалення падає на
/// <c>FK_RWV_Map</c> голим 500, червоніє <see cref="Видалення_відмовляє_за_наявності_провенансу_і_лишає_чуже"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class RowWindowMapsApiTests(SqlServerFixture sql)
{
    private const string Password = "Row-Window-Api-2026!";
    private static readonly Uri Maps = new("/api/v1/row-window-maps", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public async Task Без_права_403_на_запис_а_View_лише_читає()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var none = await SignedInAsync(app, ["System.ViewHealth"], null, null);
        using var viewer = await SignedInAsync(app, ["Integration.View"], null, null);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        var id = await CreateAsync(manager, stand, stand.TargetA);
        var one = new Uri($"/api/v1/row-window-maps/{id}", UriKind.Relative);

        HttpResponseMessage[] denied =
        [
            await none.GetAsync(Maps),
            await none.GetAsync(one),
            await none.PostAsJsonAsync(Maps, Body(stand, stand.TargetB)),
            await none.PutAsJsonAsync(one, Replace(stand)),
            await none.DeleteAsync(one),
            // View читає, але не пише.
            await viewer.PostAsJsonAsync(Maps, Body(stand, stand.TargetB)),
            await viewer.PutAsJsonAsync(one, Replace(stand)),
            await viewer.DeleteAsync(one),
        ];

        Assert.All(denied, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Maps)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(one)).StatusCode);

        // Жодна відмова нічого не змінила.
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowMap WHERE TableDefId = {stand.TableDefId}"));
        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowSource WHERE RowWindowMapId = {id}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public async Task Створення_права_грант_відмови_і_умовчання_на_реальній_базі()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Write);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        // Без гранта Manage на проєкт, який використовує колонку-ціль, — 403.
        var noGrant = await writer.PostAsJsonAsync(Maps, Body(stand, stand.TargetA));
        Assert.Equal(HttpStatusCode.Forbidden, noGrant.StatusCode);
        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", (await JsonAsync(noGrant)).GetProperty("messageKey").GetString());

        // Кожна відмова — свій статус, код і ключ; жодна нічого не пише.
        await ExpectAsync(manager, Body(stand, stand.TargetA, start: stand.TargetB), 422, "err.ECR-INT-0422.windowColumnsNotDate");
        await ExpectAsync(manager, Body(stand, stand.TextColumn), 422, "err.ECR-INT-0422.targetNotDecimal");
        await ExpectAsync(manager, Body(stand, stand.TargetA, end: stand.Start), 422, "err.ECR-INT-0422.windowColumnsSame");
        await ExpectAsync(manager, Body(stand, stand.TargetA, selector: stand.ForeignColumn), 422, "err.ECR-INT-0422.selectorNotInTable");
        await ExpectAsync(manager, Body(stand, stand.TargetA, tableDefId: stand.ForeignTableDefId), 422, "err.ECR-INT-0422.rowWindowTargetNotInTable");
        await ExpectAsync(manager, Body(stand, stand.TargetA, minPercentGood: "101"), 422, "err.ECR-INT-0422.rowWindowPolicyOutOfRange");
        await ExpectAsync(manager, Body(stand, stand.TargetA, sources: [Source("A", stand.EntityId, "  ", stand.UnitSource)]), 422, "err.ECR-REQ-0422.rowWindowSourceInvalid");
        await ExpectAsync(manager, Body(stand, stand.TargetA, selector: null, sources: [Source("A", stand.EntityId, "Flare.Total", stand.UnitSource)]), 422, "err.ECR-INT-0422.rowWindowSelectorWithoutColumn");
        await ExpectAsync(manager, Body(stand, stand.TargetA, sources: [Source("A", stand.EntityId, "F1", stand.UnitSource), Source("a", stand.EntityId, "F2", stand.UnitSource)]), 409, "err.ECR-INT-0409.rowWindowSelectorTaken");
        await ExpectAsync(manager, Body(stand, 999_999_999), 404, "err.ECR-INT-0405.column");
        await ExpectAsync(manager, Body(stand, stand.TargetA, targetUnit: 999_999_999), 404, "err.ECR-UOM-0404.unitId");
        await ExpectAsync(manager, Body(stand, stand.TargetA, sources: [Source("A", 999_999_999, "F1", stand.UnitSource)]), 404, "err.ECR-INT-0404.sourceEntity");
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowMap WHERE TableDefId = {stand.TableDefId}"));

        // Успіх: 201, Location, умовчання порогів (95 і 7), два джерела.
        var created = await manager.PostAsJsonAsync(Maps, Body(stand, stand.TargetA));
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var body = await JsonAsync(created);
        var id = body.GetProperty("id").GetInt32();
        Assert.EndsWith($"/api/v1/row-window-maps/{id}", created.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal((95m, 7, "Total", true), (
            Dec(body.GetProperty("minPercentGood")),
            body.GetProperty("refetchWithinDays").GetInt32(),
            body.GetProperty("summary").GetString(),
            body.GetProperty("isActive").GetBoolean()));
        Assert.Equal(stand.TargetACode, body.GetProperty("targetColumnCode").GetString());
        Assert.Equal(2, body.GetProperty("sources").GetArrayLength());

        // Друга прив'язка на ту саму колонку-ціль — 409.
        await ExpectAsync(manager, Body(stand, stand.TargetA), 409, "err.ECR-INT-0409.rowWindowTargetTaken");

        // Читання: перелік за таблицею й за сутністю джерела, один, а чужа таблиця й чужа сутність порожні.
        var other = await CreateAsync(manager, stand, stand.TargetB);
        Assert.Equal(
            [id, other],
            Ids(await QueryAsync(manager, $"/api/v1/row-window-maps?tableDefId={stand.TableDefId}&sourceEntityId={stand.EntityId}")));
        Assert.Empty(Ids(await QueryAsync(manager, $"/api/v1/row-window-maps?tableDefId={stand.ForeignTableDefId}")));
        Assert.Empty(Ids(await QueryAsync(manager, "/api/v1/row-window-maps?sourceEntityId=999999999")));
        Assert.Equal(id, (await QueryAsync(manager, $"/api/v1/row-window-maps/{id}")).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(new Uri("/api/v1/row-window-maps/999999999", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    [Trait("Requirement", "ФВ-12.10")]
    public async Task Повна_заміна_джерел_пауза_застарілий_rowVersion_і_ізоляція_сусідньої_прив_язки()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        using var writer = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Write);

        var id = await CreateAsync(manager, stand, stand.TargetA);
        var neighbour = await CreateAsync(manager, stand, stand.TargetB);
        var one = new Uri($"/api/v1/row-window-maps/{id}", UriKind.Relative);
        var before = await JsonAsync(await manager.GetAsync(one));
        var version = before.GetProperty("rowVersion").GetString();

        // Без гранта Manage — 403, 404 на неіснуючу, а сирі зміни не проходять.
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PutAsJsonAsync(one, Replace(stand))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync(new Uri("/api/v1/row-window-maps/999999999", UriKind.Relative), Replace(stand))).StatusCode);

        // Відмови не чіпають нічого: невідома одиниця, вікно не з Date, дубль селектора.
        Assert.Equal(404, (int)(await manager.PutAsJsonAsync(one, Replace(stand, targetUnit: 999_999_999))).StatusCode);
        Assert.Equal(422, (int)(await manager.PutAsJsonAsync(one, Replace(stand, start: stand.TargetB))).StatusCode);
        Assert.Equal(409, (int)(await manager.PutAsJsonAsync(
            one,
            Replace(stand, sources: [Source("A", stand.EntityId, "F1", stand.UnitSource), Source("a", stand.EntityId, "F2", stand.UnitSource)]))).StatusCode);
        Assert.Equal("Total", (await JsonAsync(await manager.GetAsync(one))).GetProperty("summary").GetString());
        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowSource WHERE RowWindowMapId = {id}"));

        // Заміна: інша згортка, ступінчастий ряд, пороги, ТЕ САМЕ значення селектора з новим атрибутом (унікальний
        // індекс «прив'язка+селектор»: старе видаляється в тій самій транзакції), пауза.
        var replaced = await manager.PutAsJsonAsync(
            one,
            Replace(
                stand,
                summary: "Average",
                isStep: true,
                isActive: false,
                minPercentGood: "80",
                refetchWithinDays: 3,
                maxGapSeconds: 600,
                rowVersion: version,
                sources: [Source("A", stand.EntityId, "Flare.NewTag", stand.UnitTarget), Source("C", stand.EntityId, "Flare.Other", stand.UnitSource)]));
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, $"{replaced.StatusCode}: {app.ErrorsText}");
        var after = await JsonAsync(replaced);
        Assert.Equal(("Average", true, false, 80m, 3, 600), (
            after.GetProperty("summary").GetString(),
            after.GetProperty("isStep").GetBoolean(),
            after.GetProperty("isActive").GetBoolean(),
            Dec(after.GetProperty("minPercentGood")),
            after.GetProperty("refetchWithinDays").GetInt32(),
            after.GetProperty("maxGapSeconds").GetInt32()));
        Assert.Equal(
            ["A:Flare.NewTag", "C:Flare.Other"],
            after.GetProperty("sources").EnumerateArray().Select(s => $"{s.GetProperty("selectorValue").GetString()}:{s.GetProperty("sourceField").GetString()}").Order().ToArray());
        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowSource WHERE RowWindowMapId = {id}"));
        Assert.NotEqual(version, after.GetProperty("rowVersion").GetString());
        // Колонка-ціль й таблиця — ключ: лишились ті самі.
        Assert.Equal(stand.TargetA, after.GetProperty("targetColumnDefId").GetInt32());

        // Застарілий rowVersion — 409, і стан не змінюється.
        var stale = await manager.PutAsJsonAsync(one, Replace(stand, summary: "Maximum", rowVersion: version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("err.ECR-INT-0409.rowWindowConcurrency", (await JsonAsync(stale)).GetProperty("messageKey").GetString());
        Assert.Equal("Average", (await JsonAsync(await manager.GetAsync(one))).GetProperty("summary").GetString());

        // Друга заміна: селектора більше немає, одне джерело «для всіх», відновлення.
        var second = await manager.PutAsJsonAsync(
            one,
            Replace(stand, selector: null, sources: [Source(null, stand.EntityId, "Flare.All", stand.UnitSource)]));
        Assert.True(second.StatusCode == HttpStatusCode.OK, $"{second.StatusCode}: {app.ErrorsText}");
        var source = Assert.Single((await JsonAsync(second)).GetProperty("sources").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("selectorValue").ValueKind);
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowMap WHERE Id = {id} AND SelectorColumnDefId IS NOT NULL"));

        // Сусідня прив'язка тієї ж таблиці не змінилась.
        var neighbourBody = await JsonAsync(await manager.GetAsync(new Uri($"/api/v1/row-window-maps/{neighbour}", UriKind.Relative)));
        Assert.Equal(("Total", true, 2), (
            neighbourBody.GetProperty("summary").GetString(),
            neighbourBody.GetProperty("isActive").GetBoolean(),
            neighbourBody.GetProperty("sources").GetArrayLength()));

        // Слід у журналі структурних змін: створення й дві зміни зі станом до й після.
        var audit = await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.RowWindowMap", id);
        Assert.Equal(["CreateRowWindowMap", "UpdateRowWindowMap", "UpdateRowWindowMap"], audit.Select(a => a.Operation));
        Assert.Null(audit[0].OldJson);
        Assert.Contains("\"isActive\":true", audit[1].OldJson, StringComparison.Ordinal);
        Assert.Contains("\"isActive\":false", audit[1].NewJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    [Trait("Requirement", "ФВ-12.10")]
    public async Task Видалення_відмовляє_за_наявності_провенансу_і_лишає_чуже()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        using var writer = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Write);

        var id = await CreateAsync(manager, stand, stand.TargetA);
        var neighbour = await CreateAsync(manager, stand, stand.TargetB);
        var one = new Uri($"/api/v1/row-window-maps/{id}", UriKind.Relative);
        await AddValueAsync(stand, id);

        Assert.Equal(HttpStatusCode.Forbidden, (await writer.DeleteAsync(one)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.DeleteAsync(new Uri("/api/v1/row-window-maps/999999999", UriKind.Relative))).StatusCode);

        // Провенанс пояснює числа колонки: прив'язка з ним не видаляється (409), вихід — пауза.
        var refused = await manager.DeleteAsync(one);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("err.ECR-INT-0409.rowWindowMapHasValues", (await JsonAsync(refused)).GetProperty("messageKey").GetString());
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowMap WHERE Id = {id}"));
        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowSource WHERE RowWindowMapId = {id}"));

        await ExecuteAsync($"DELETE FROM ext.RowWindowValue WHERE RowWindowMapId = {id}");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(one)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(one)).StatusCode);
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowSource WHERE RowWindowMapId = {id}"));

        // Сусідня прив'язка з її джерелами вціліла.
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowMap WHERE Id = {neighbour}"));
        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowSource WHERE RowWindowMapId = {neighbour}"));

        // Колонку-ціль можна прив'язати знову (унікальний індекс звільнено).
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync(Maps, Body(stand, stand.TargetA))).StatusCode);

        var audit = await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.RowWindowMap", id);
        Assert.Equal(["CreateRowWindowMap", "DeleteRowWindowMap"], audit.Select(a => a.Operation));
        Assert.Null(audit[1].NewJson);
        Assert.Contains("\"summary\":\"Total\"", audit[1].OldJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    public async Task Кожна_зміна_прив_язки_скидає_знімок_колонок_вікна_хука_запису_а_старий_провенанс_лишається()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);
        var index = app.Services.GetRequiredService<IRowWindowColumnIndex>();

        // Знімок прогрітий ДО створення: без скидання він ще 60 с казав би «колонок вікна немає».
        Assert.Empty(await index.WindowColumnsAsync(stand.TableDefId, CancellationToken.None));

        var id = await CreateAsync(manager, stand, stand.TargetA);
        Assert.Equal(
            new[] { stand.Start, stand.End, stand.Selector }.Order(),
            (await index.WindowColumnsAsync(stand.TableDefId, CancellationToken.None)).Order());

        // PUT без селектора й зі старим провенансом: знімок оновився, провенанс не зачеплено.
        await AddValueAsync(stand, id);
        var one = new Uri($"/api/v1/row-window-maps/{id}", UriKind.Relative);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(one, Replace(stand, selector: null, sources: [Source(null, stand.EntityId, "Flare.All", stand.UnitSource)]))).StatusCode);
        Assert.Equal(
            new[] { stand.Start, stand.End }.Order(),
            (await index.WindowColumnsAsync(stand.TableDefId, CancellationToken.None)).Order());
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowValue WHERE RowWindowMapId = {id}"));

        // Пауза виводить прив'язку зі знімка (у ньому лише активні).
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(one, Replace(stand, selector: null, isActive: false, sources: [Source(null, stand.EntityId, "Flare.All", stand.UnitSource)]))).StatusCode);
        Assert.Empty(await index.WindowColumnsAsync(stand.TableDefId, CancellationToken.None));

        // Відновлення повертає її; видалення після очищення провенансу знову спорожнює знімок.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(one, Replace(stand, selector: null, sources: [Source(null, stand.EntityId, "Flare.All", stand.UnitSource)]))).StatusCode);
        Assert.NotEmpty(await index.WindowColumnsAsync(stand.TableDefId, CancellationToken.None));
        await ExecuteAsync($"DELETE FROM ext.RowWindowValue WHERE RowWindowMapId = {id}");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(one)).StatusCode);
        Assert.Empty(await index.WindowColumnsAsync(stand.TableDefId, CancellationToken.None));
    }

    // ── тіла запитів ──────────────────────────────────────────────────────────

    private static object Source(string? selector, int entityId, string field, int unitId)
        => new { selectorValue = selector, sourceEntityId = entityId, sourceField = field, sourceUnitId = unitId };

    private static object Body(
        Stand stand,
        int target,
        int? start = null,
        int? end = null,
        int? selector = -1,
        int? tableDefId = null,
        string? minPercentGood = null,
        int? targetUnit = null,
        object[]? sources = null)
        => new
        {
            tableDefId = tableDefId ?? stand.TableDefId,
            targetColumnDefId = target,
            startColumnDefId = start ?? stand.Start,
            endColumnDefId = end ?? stand.End,
            selectorColumnDefId = selector == -1 ? stand.Selector : selector,
            summary = "Total",
            isStep = false,
            targetUnitId = targetUnit ?? stand.UnitTarget,
            minPercentGood,
            sources = sources ?? [Source("A", stand.EntityId, "Flare.Total", stand.UnitSource), Source(null, stand.EntityId, "Flare.Default", stand.UnitSource)],
        };

    private static object Replace(
        Stand stand,
        int? start = null,
        int? selector = -1,
        string summary = "Total",
        bool isStep = false,
        bool isActive = true,
        string? minPercentGood = null,
        int? refetchWithinDays = null,
        int? maxGapSeconds = null,
        int? targetUnit = null,
        string? rowVersion = null,
        object[]? sources = null)
        => new
        {
            startColumnDefId = start ?? stand.Start,
            endColumnDefId = stand.End,
            selectorColumnDefId = selector == -1 ? stand.Selector : selector,
            summary,
            isStep,
            targetUnitId = targetUnit ?? stand.UnitTarget,
            minPercentGood,
            refetchWithinDays,
            maxGapSeconds,
            isActive,
            rowVersion,
            sources = sources ?? [Source("A", stand.EntityId, "Flare.Total", stand.UnitSource)],
        };

    private static async Task<int> CreateAsync(HttpClient client, Stand stand, int target)
    {
        var response = await client.PostAsJsonAsync(Maps, Body(stand, target));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await JsonAsync(response)).GetProperty("id").GetInt32();
    }

    private static async Task ExpectAsync(HttpClient client, object body, int status, string messageKey)
    {
        var response = await client.PostAsJsonAsync(Maps, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"{messageKey}: очікувався {status}, прийшло {(int)response.StatusCode}: {text}");
        Assert.Equal(messageKey, JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
    }

    // Decimal у контракті — рядок (глобальний конвертер): і відповідь, і запит.
    private static decimal Dec(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetDecimal();

    private static int[] Ids(JsonElement list) => [.. list.EnumerateArray().Select(m => m.GetProperty("id").GetInt32())];

    private static async Task<JsonElement> QueryAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    // ── стенд ────────────────────────────────────────────────────────────────

    private sealed record Stand(
        int ProjectId,
        int TableDefId,
        long InstanceId,
        int EntityId,
        int Start,
        int End,
        int TargetA,
        string TargetACode,
        int TargetB,
        int TextColumn,
        int Selector,
        int ForeignColumn,
        int ForeignTableDefId,
        int UnitSource,
        int UnitTarget,
        Func<ValueTask> Cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cleanup();
    }

    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            periodKey: 202601, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var foreign = await builder.BuildAsync(
            periodKey: 202601, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"RWS_{tag}"), Name("Start"), 20, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"RWE_{tag}"), Name("End"), 21, CellDataType.Date);
        var targetA = new ColumnDef(chain.TableDefId, EcrCode.Create($"RWA_{tag}"), Name("Volume A"), 22, CellDataType.Decimal);
        var targetB = new ColumnDef(chain.TableDefId, EcrCode.Create($"RWB_{tag}"), Name("Volume B"), 23, CellDataType.Decimal);
        var text = new ColumnDef(chain.TableDefId, EcrCode.Create($"RWT_{tag}"), Name("Text"), 24, CellDataType.String);
        var selector = new ColumnDef(chain.TableDefId, EcrCode.Create($"RWK_{tag}"), Name("Selector"), 25, CellDataType.String);
        db.ColumnDefs.AddRange(start, end, targetA, targetB, text, selector);

        var source = new DataSource(
            EcrCode.Create($"RWSRC{tag}"), Name("HSE301-A1"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        var dimensionId = await db.Dimensions.OrderBy(d => d.Id).Select(d => d.Id).FirstAsync();
        var unitSource = new Unit(
            EcrCode.Create($"RWU{tag}"), Name("src"), Name("src"), dimensionId, isBase: false, factorToBase: 1m, offsetToBase: 0m);
        var unitTarget = new Unit(
            EcrCode.Create($"RWV{tag}"), Name("dst"), Name("dst"), dimensionId, isBase: false, factorToBase: 1000m, offsetToBase: 0m);
        db.Units.AddRange(unitSource, unitTarget);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(source.Id, $"FlareTag{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return new Stand(
            chain.ProjectId,
            chain.TableDefId,
            chain.TableInstanceId,
            entity.Id,
            start.Id,
            end.Id,
            targetA.Id,
            $"RWA_{tag}",
            targetB.Id,
            text.Id,
            selector.Id,
            foreign.ColumnDefIds[0],
            foreign.TableDefId,
            unitSource.Id,
            unitTarget.Id,
            async () =>
            {
                // База спільна на весь прогін: прив'язки й провенанс цього стенда прибираються, а сутність джерела
                // вимикається (активна без збору фарбувала б SourcesHealthCheck інших тестів).
                await ExecuteAsync(
                    "DELETE v FROM ext.RowWindowValue v JOIN ext.RowWindowMap m ON m.Id = v.RowWindowMapId "
                    + $"WHERE m.TableDefId = {chain.TableDefId}");
                await ExecuteAsync(
                    "DELETE s FROM ext.RowWindowSource s JOIN ext.RowWindowMap m ON m.Id = s.RowWindowMapId "
                    + $"WHERE m.TableDefId = {chain.TableDefId}");
                await ExecuteAsync($"DELETE FROM ext.RowWindowMap WHERE TableDefId = {chain.TableDefId}");
                await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {entity.Id}");
                await ExecuteAsync($"UPDATE ext.DataSource SET IsActive = 0 WHERE Id = {source.Id}");
            });
    }

    private async Task AddValueAsync(Stand stand, int mapId)
    {
        await using var db = NewDb();
        db.RowWindowValues.Add(new RowWindowValue(
            202601,
            stand.InstanceId,
            "EF-1",
            stand.TargetA,
            mapId,
            stand.EntityId,
            "Flare.Total",
            new DateTime(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc),
            new DateTime(2026, 1, 28, 9, 24, 50, DateTimeKind.Utc),
            RowWindowSummaryKind.Total,
            stand.UnitTarget,
            new DateTime(2026, 2, 5, 9, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
    }

    /// <summary>Користувач із заданими функціональними правами і, якщо задано, грантом на проєкт.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions, int? projectId, GrantLevel? level)
    {
        var client = app.CreateClient();
        var name = $"rwm_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("A1 row window test"));
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
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

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
}
