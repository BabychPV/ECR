// tests/Ecr.Api.Tests/SourceEventsApiTests.D465.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-465 на РЕАЛЬНОМУ HTTP і SQL Server: «Дані з PI» для подій (матриця прав, документ, якого читач не бачить, —
/// без рядка й комірок, keyset-сторінки, фільтри, причини, CSV зі стелею, захистом від формул і подією безпеки),
/// «Звідки це значення» для комірки рядка події (доступ рівно як до комірки), і відмова мапінгу на колонку місяця.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — точковою правкою):
/// <list type="bullet">
/// <item>у <c>PiEventDataHandler.PageAsync</c> читати комірки й без видимого документа — червоніє «прихований документ»
/// (з'являються значення комірок / ключ рядка);</item>
/// <item>у <c>PiEventDataHandler.RequireAsync</c> пустити <c>Document.View</c> — червоніє матриця прав;</item>
/// <item>у <c>SourceEventMapSupport.BuildSpecsAsync</c> прибрати перевірку <c>IsMonthColumn</c> — мапінг створюється (200);</item>
/// <item>у <c>CellSourceHandler.SummaryAsync</c> не перевіряти префікс <c>EF-</c> / документ мапінгу — порожні
/// <c>eventSources</c> чи зайві звернення у сталих зверненнях D-463.</item>
/// </list>
/// </remarks>
public sealed partial class SourceEventsApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Мапінг_на_колонку_місяця_422_ключем_а_наявний_позначено_попередженням()
    {
        await using var stand = await ArrangeAsync();
        var decimalColumn = await ScalarAsync(
            $"SELECT TOP 1 Id FROM cfg.ColumnDef WHERE TableDefId = {stand.TableDefId} AND DataType = {(int)CellDataType.Decimal} ORDER BY Id");

        // Наявний мапінг (до рішення D-465) уже пише в колонку, яку потім позначено колонкою місяця.
        int existing;
        await using (var db = NewDb())
        {
            var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == stand.TableDefId);
            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == stand.TableDefId).ToDictionaryAsync(c => c.Id);
            var map = SourceEventMap.Create(
                stand.EntityId,
                stand.DocumentId,
                table,
                [
                    new(columns[stand.StartColumn], SourceEventMap.StartAttribute),
                    new(columns[stand.EndColumn], SourceEventMap.EndAttribute),
                    new(columns[decimalColumn], "Volume"),
                ],
                SourceEventVolumeMode.None);
            db.SourceEventMaps.Add(map);
            await db.SaveChangesAsync();
            existing = map.Id;
        }

        await ExecuteAsync($"UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = 3 WHERE Id = {decimalColumn}");

        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        var maps = await JsonAsync(await manager.GetAsync(new Uri($"/api/v1/source-event-maps?sourceEntityId={stand.EntityId}", UriKind.Relative)));
        var listed = maps.EnumerateArray().Single(m => m.GetProperty("id").GetInt32() == existing);
        Assert.Equal(1, listed.GetProperty("monthColumnFieldIds").GetArrayLength());

        var window = $"fromUtc=2026-01-01T00:00:00Z&toUtc=2026-12-31T00:00:00Z";
        var eventMaps = await JsonAsync(await manager.GetAsync(new Uri($"/api/v1/sources/{stand.EntityId}/raw-data/events/maps?{window}", UriKind.Relative)));
        Assert.True(eventMaps.GetProperty("maps")[0].GetProperty("hasMonthColumnFields").GetBoolean());

        // Новий мапінг на колонку місяця — 422 з ключем і колонкою, нічого не записано.
        await ExecuteAsync($"DELETE FROM ext.SourceEventFieldMap WHERE SourceEventMapId = {existing}; DELETE FROM ext.SourceEventMap WHERE Id = {existing}");
        var before = await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE SourceEntityId = {stand.EntityId}");
        var refused = await manager.PostAsJsonAsync(
            EventMaps,
            new
            {
                sourceEntityId = stand.EntityId,
                documentId = stand.DocumentId,
                tableDefId = stand.TableDefId,
                volumeMode = "None",
                fields = new object[]
                {
                    new { targetColumnDefId = stand.StartColumn, sourceAttribute = "$start", attributeScope = "Event", valueKind = "Direct" },
                    new { targetColumnDefId = stand.EndColumn, sourceAttribute = "$end", attributeScope = "Event", valueKind = "Direct" },
                    new { targetColumnDefId = decimalColumn, sourceAttribute = "Volume", attributeScope = "Event", valueKind = "Direct" },
                },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await JsonAsync(refused);
        Assert.Equal("err.ECR-INT-0422.eventMapMonthColumn", problem.GetProperty("messageKey").GetString());
        Assert.Contains(decimalColumn.ToString(CultureInfo.InvariantCulture), problem.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(before, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventMap WHERE SourceEntityId = {stand.EntityId}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Події_PI_право_прихований_документ_сторінки_фільтри_й_CSV()
    {
        await using var stand = await ArrangeAsync();
        var mapId = await AddMapAsync(stand);

        // 118 записаних подій (рядок є), одна незакрита, одна за стелею рядків.
        var start = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var written = new List<long>();
        for (var i = 0; i < 118; i++)
        {
            written.Add(await AddLinkAsync(mapId, $"W{i:D3}", start.AddHours(i), written: true));
        }

        var open = await AddLinkAsync(mapId, "OPEN1", start.AddHours(200), written: false, open: true);
        var limited = await AddLinkAsync(mapId, "LIMIT1", start.AddHours(201), written: false);
        await ExecuteAsync($"""
            INSERT INTO ext.SourceEventAttributeValue (LinkId, Scope, Attribute, ValueNumeric, ValueText, IsTruncated, SourceUnit, ValueHash, SourceModifiedUtc, ChangedAt, RunId)
            VALUES ({open}, 0, N'Category', NULL, N'=HYPERLINK("http://x")', 0, NULL, HASHBYTES('SHA2_256', N'x'), NULL, '2026-02-05', 'test');
            UPDATE ext.SourceEventLink SET EventName = N'=1+1' WHERE Id = {open};
            """);

        using var app = new EcrApiFactory(sql);
        var window = "fromUtc=2026-01-01T00:00:00Z&toUtc=2026-03-01T00:00:00Z";
        var root = $"/api/v1/sources/{stand.EntityId}/raw-data/events";
        string[] routes = [$"{root}/maps?{window}", $"{root}?{window}", $"{root}/removed?{window}", $"{root}/export.csv?{window}"];

        // Матриця прав: анонім — 401; Document.View (навіть із грантом на проєкт) — 403; View/Manage — 200.
        using (var anonymous = app.CreateClient())
        {
            foreach (var route in routes)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri(route, UriKind.Relative))).StatusCode);
            }
        }

        using (var reader = await SignedInAsync(app, ["Document.View"], stand.ProjectId, GrantLevel.Read))
        {
            foreach (var route in routes)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(new Uri(route, UriKind.Relative))).StatusCode);
            }
        }

        using var hidden = await SignedInAsync(app, ["Integration.View"], null, null);
        using var visible = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Read);
        foreach (var route in routes)
        {
            Assert.Equal(HttpStatusCode.OK, (await hidden.GetAsync(new Uri(route, UriKind.Relative))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await visible.GetAsync(new Uri(route, UriKind.Relative))).StatusCode);
        }

        // Документ, якого читач не бачить: подія й значення PI є, а ні документа, ні ключа рядка, ні комірок немає.
        var hiddenMaps = await JsonAsync(await hidden.GetAsync(new Uri($"{root}/maps?{window}", UriKind.Relative)));
        var hiddenMap = hiddenMaps.GetProperty("maps")[0];
        Assert.False(hiddenMap.GetProperty("documentVisible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hiddenMap.GetProperty("documentKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, hiddenMap.GetProperty("documentId").ValueKind);

        var hiddenPage = await JsonAsync(await hidden.GetAsync(new Uri($"{root}?{window}&limit=200", UriKind.Relative)));
        var hiddenItems = hiddenPage.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(120, hiddenItems.Count);
        var hiddenWritten = hiddenItems.Single(e => e.GetProperty("eventId").GetString() == "W005");
        Assert.Equal(JsonValueKind.Null, hiddenWritten.GetProperty("row").ValueKind);
        Assert.True(hiddenWritten.GetProperty("rowHidden").GetBoolean());
        Assert.All(
            hiddenWritten.GetProperty("attributes").EnumerateArray(),
            a => Assert.Equal(JsonValueKind.Null, a.GetProperty("cellValue").ValueKind));
        Assert.Equal("Hidden", hiddenWritten.GetProperty("attributes")[0].GetProperty("comparison").GetString());
        Assert.DoesNotContain("EF-W005", hiddenPage.GetRawText(), StringComparison.Ordinal);

        // Видимий документ — рядок і документ є; незакрита подія — «не записано, триває», стеля — «ліміт рядків».
        var visiblePage = await JsonAsync(await visible.GetAsync(new Uri($"{root}?{window}&limit=200", UriKind.Relative)));
        var items = visiblePage.GetProperty("items").EnumerateArray().ToList();
        var w5 = items.Single(e => e.GetProperty("eventId").GetString() == "W005");
        Assert.Equal("EF-W005", w5.GetProperty("row").GetProperty("rowKey").GetString());
        Assert.False(w5.GetProperty("rowHidden").GetBoolean());
        var openItem = items.Single(e => e.GetProperty("eventId").GetString() == "OPEN1");
        Assert.Equal("Open", openItem.GetProperty("reason").GetString());
        var category = openItem.GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("attribute").GetString() == "Category");
        Assert.Equal("=HYPERLINK(\"http://x\")", category.GetProperty("piValueText").GetString());
        Assert.Equal(JsonValueKind.Null, category.GetProperty("comparison").ValueKind);
        Assert.Equal("NotWritten", openItem.GetProperty("attributes")[0].GetProperty("comparison").GetString());
        Assert.Equal("RowLimit", items.Single(e => e.GetProperty("eventId").GetString() == "LIMIT1").GetProperty("reason").GetString());

        // Keyset: 120 подій сторінками по 50 — 50/50/20, без дублів, новіші першими, загальна кількість одна.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await JsonAsync(await visible.GetAsync(new Uri(
                $"{root}?{window}&limit=50" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"), UriKind.Relative)));
            Assert.Equal(120, page.GetProperty("totalCount").GetInt32());
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("eventId").GetString()!));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(120, seen.Distinct().Count());
        Assert.Equal("LIMIT1", seen[0]);

        // Фільтри: стан і назва (без регістру — зіставлення бази).
        var openOnly = await JsonAsync(await visible.GetAsync(new Uri($"{root}?{window}&status=Open", UriKind.Relative)));
        Assert.Equal(["OPEN1"], openOnly.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("eventId").GetString()));
        var byName = await JsonAsync(await visible.GetAsync(new Uri($"{root}?{window}&name=flaring%20w01", UriKind.Relative)));
        Assert.Equal(10, byName.GetProperty("totalCount").GetInt32());

        // Чужий мапінг — 404, задовгий фільтр — 422.
        Assert.Equal(HttpStatusCode.NotFound, (await visible.GetAsync(new Uri($"{root}?{window}&mapId=999999", UriKind.Relative))).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await visible.GetAsync(new Uri($"{root}?{window}&name={new string('x', 201)}", UriKind.Relative))).StatusCode);

        // CSV: BOM, заголовок, рядок «подія × поле», текст PI з «=» — нейтралізований, подія безпеки без значень.
        var exportsBefore = await ExportEventsAsync(mapId);
        var csv = await visible.GetAsync(new Uri($"{root}/export.csv?{window}&status=Open", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = System.Text.Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("eventId,eventName,startUtc", lines[0], StringComparison.Ordinal);
        Assert.Equal(1 + 2, lines.Length);
        Assert.Contains(",'=1+1,", lines[1], StringComparison.Ordinal);
        Assert.Equal(exportsBefore + 1, await ExportEventsAsync(mapId));

        // Стеля — 422 до першого байта і без події безпеки.
        using var limitedApp = app.WithWebHostBuilder(b => b.UseSetting("Integration:RawDataExportMaxRows", "2"));
        using var limitedClient = await SignedInAsync(limitedApp, ["Integration.View"], null, null);
        var tooLarge = await limitedClient.GetAsync(new Uri($"{root}/export.csv?{window}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.auditExportTooLarge", (await JsonAsync(tooLarge)).GetProperty("messageKey").GetString());
        Assert.Equal(exportsBefore + 1, await ExportEventsAsync(mapId));

        Assert.NotEqual(0, limited);
        Assert.Equal(118, written.Count);
    }

    private Task<int> ExportEventsAsync(int mapId)
        => ScalarAsync(
            "SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'PiEventDataExported' AND DetailsJson LIKE N'%\"mapId\":"
            + mapId.ToString(CultureInfo.InvariantCulture) + ",%'");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Звідки_це_значення_для_комірки_рядка_події_рівно_доступ_до_комірки()
    {
        await using var stand = await ArrangeAsync();
        var mapId = await AddMapAsync(stand);
        await AddLinkAsync(mapId, "X1", L1Start, written: true);

        // Рядок події й комірка $start (пояс проєкту Asia/Atyrau, +05:00 — як записав би синк).
        await ExecuteAsync($"""
            DECLARE @row bigint = NEXT VALUE FOR doc.TableRowSeq;
            INSERT INTO doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, Ordinal, IsDeleted, IsOrphaned, OrphanedAt, ModifiedAt)
            VALUES (202601, @row, {stand.InstanceId}, N'EF-X1', 1, 0, 0, NULL, '2026-02-05');
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueDate, IsCalculated, IsEmpty)
            VALUES (202601, @row, {stand.StartColumn}, {stand.TableDefId}, '2026-01-28T14:09:20', 0, 0);
            """);

        using var app = new EcrApiFactory(sql);
        var cell = $"/api/v1/documents/{stand.DocumentId}/tables/{stand.InstanceId}/cell-source?rowKey=EF-X1&columnDefId={stand.StartColumn}";

        using var reader = await SignedInAsync(app, ["Document.View"], stand.ProjectId, GrantLevel.Read);
        var view = await JsonAsync(await reader.GetAsync(new Uri(cell, UriKind.Relative)));
        Assert.Empty(view.GetProperty("sources").EnumerateArray());
        var source = Assert.Single(view.GetProperty("eventSources").EnumerateArray());
        Assert.Equal(mapId, source.GetProperty("mapId").GetInt32());
        Assert.Equal("X1", source.GetProperty("event").GetProperty("eventId").GetString());
        Assert.Equal("Synced", source.GetProperty("event").GetProperty("status").GetString());
        var attribute = source.GetProperty("attribute");
        Assert.Equal("$start", attribute.GetProperty("attribute").GetString());
        Assert.Equal("2026-01-28 14:09:20", attribute.GetProperty("expectedValue").GetString());
        Assert.Equal("2026-01-28 14:09:20", attribute.GetProperty("cellValue").GetString());
        Assert.Equal("Match", attribute.GetProperty("comparison").GetString());

        // Колонка, закрита Deny на таблицю, — та сама 404, що й неіснуючий документ.
        using var denied = await SignedInAsync(app, ["Document.View"], stand.ProjectId, GrantLevel.Read, denyTableDefId: stand.TableDefId);
        var refused = await denied.GetAsync(new Uri(cell, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        var missing = await reader.GetAsync(new Uri(
            $"/api/v1/documents/999999999/tables/{stand.InstanceId}/cell-source?rowKey=EF-X1&columnDefId={stand.StartColumn}", UriKind.Relative));
        Assert.Equal(
            (await JsonAsync(missing)).GetProperty("messageKey").GetString(),
            (await JsonAsync(refused)).GetProperty("messageKey").GetString());
    }
}
