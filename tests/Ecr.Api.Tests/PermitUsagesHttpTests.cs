// tests/Ecr.Api.Tests/PermitUsagesHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// PERMUSE: <c>GET /api/v1/permits/{id}/usages</c> («де використано» дозвіл), лічильник <c>usedInDocumentCount</c> у переліку
/// дозволів і вибір дозволу в колонці <c>Lookup</c> таблиці документа (приховані реєстри типів дозволів як ціль).
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>PermitStore.FindUsagesAsync</c> прибрати <c>where !r.IsDeleted</c> в запиті комірок - червоніє
/// «Використання_шапки_і_рядків…» (логічно видалений рядок попадає у відповідь); (2) прибрати фільтр
/// <c>projects.Contains(d.ProjectId)</c> - червоніє «Читач_без_доступу_до_документів…» (чужий документ видно); (3) прибрати
/// шапкову гілку - зникає перший елемент і лічильник.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitUsagesHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Permit-Usage-2026!";
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Використання_шапки_і_рядків_рахується_по_документах_і_періодах_без_видалених_рядків()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var usages = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);

        // Один документ, три місця: поле шапки + дві комірки колонки (поточний і попередній період). Видалений рядок не рахується.
        Assert.Equal((s.Active, 1, 3, false), (
            usages.GetProperty("permitId").GetInt64(),
            usages.GetProperty("documentCount").GetInt32(),
            usages.GetProperty("linkCount").GetInt32(),
            usages.GetProperty("truncated").GetBoolean()));
        var items = usages.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var header = items[0];
        Assert.Equal("header", header.GetProperty("kind").GetString());
        Assert.Equal("PERMIT", header.GetProperty("fieldCode").GetString());
        Assert.Equal(s.Document.DocumentId, header.GetProperty("documentId").GetInt64());
        Assert.Equal(JsonValueKind.Null, header.GetProperty("periodKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, header.GetProperty("tableCode").ValueKind);

        var cells = items.Skip(1).ToList();
        Assert.All(cells, c => Assert.Equal("cell", c.GetProperty("kind").GetString()));
        Assert.Equal([s.PreviousPeriod, s.Period], cells.Select(c => c.GetProperty("periodKey").GetInt32()).ToList());
        Assert.All(cells, c => Assert.Equal(s.PermitColumnCode, c.GetProperty("fieldCode").GetString()));
        Assert.All(cells, c => Assert.Equal(s.TableCode, c.GetProperty("tableCode").GetString()));
        Assert.All(cells, c => Assert.Equal(s.SheetCode, c.GetProperty("sheetCode").GetString()));
        Assert.All(cells, c => Assert.Equal(s.FirstRowKey, c.GetProperty("rowKey").GetString()));

        // Протермінований дозвіл (історичне посилання) теж показується: одна комірка, шапки немає.
        var expired = await GetAsync(client, app, $"/api/v1/permits/{s.Expired}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1), (expired.GetProperty("documentCount").GetInt32(), expired.GetProperty("linkCount").GetInt32()));

        // Ніде не використаний.
        var unused = await GetAsync(client, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (unused.GetProperty("documentCount").GetInt32(), unused.GetProperty("linkCount").GetInt32()));
        Assert.Empty(unused.GetProperty("items").EnumerateArray());

        // limit обрізає перелік, лічильники лишаються повними.
        var limited = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?limit=1").ConfigureAwait(true);
        Assert.Equal((3, true, 1), (
            limited.GetProperty("linkCount").GetInt32(),
            limited.GetProperty("truncated").GetBoolean(),
            limited.GetProperty("items").GetArrayLength()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Фільтр_періоду_лишає_комірки_періоду_і_шапку_документів_з_таблицями_в_ньому()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var current = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?periodKey={s.Period}").ConfigureAwait(true);
        Assert.Equal(["header", "cell"], current.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToList());
        Assert.Equal(
            [s.Period],
            current.GetProperty("items").EnumerateArray().Skip(1).Select(i => i.GetProperty("periodKey").GetInt32()).ToList());

        var previous = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?periodKey={s.PreviousPeriod}").ConfigureAwait(true);
        Assert.Equal(2, previous.GetProperty("linkCount").GetInt32());

        // Період, у якому документ не має таблиць: нічого.
        var none = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?periodKey=209912").ConfigureAwait(true);
        Assert.Equal(0, none.GetProperty("linkCount").GetInt32());

        var bad = await client.GetAsync(new Uri($"/api/v1/permits/{s.Active}/usages?periodKey=7", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Читач_без_доступу_до_документів_не_бачить_їх_у_використанні_а_без_права_Registry_View_отримує_403()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var hidden = await GetAsync(outsider, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (hidden.GetProperty("documentCount").GetInt32(), hidden.GetProperty("linkCount").GetInt32()));
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());

        using var nobody = await SignedInAsync(app, s.NoRegistry).ConfigureAwait(true);
        var forbidden = await nobody.GetAsync(new Uri($"/api/v1/permits/{s.Active}/usages", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var missing = await writer.GetAsync(new Uri("/api/v1/permits/2000000000/usages", UriKind.Relative)).ConfigureAwait(true);
        var body = await missing.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(missing.StatusCode == HttpStatusCode.NotFound, $"{missing.StatusCode}: {body}");
        Assert.Equal("ECR-PRM-0404", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Перелік_дозволів_віддає_у_скількох_документах_використано_кожен()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var list = await GetAsync(writer, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.Equal(
            new Dictionary<long, int> { [s.Active] = 1, [s.Expired] = 1, [s.Unused] = 0 },
            list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt64(), i => i.GetProperty("usedInDocumentCount").GetInt32()));

        // Читач без доступу до документів бачить нулі: лічильник не розкриває чужі документи.
        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var blind = await GetAsync(outsider, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.All(blind.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));
    }

    /// <remarks>
    /// Мутації: у <c>PermitStore.CountTypeUsedInDocumentsAsync</c> прибрати фінальний <c>.Distinct()</c> пар (тип, документ) —
    /// writer отримує 2 замість 1 (шапка + комірки одного документа); у обробниках прибрати умову <c>readable.Count &gt; 0</c>
    /// разом із фільтром проєктів — outsider бачить 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Тип_дозволу_віддає_у_скількох_видимих_документах_використано_його_дозволи()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Один документ: шапка (active), комірки active/expired у двох періодах — рахується ОДИН документ.
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var usage = await GetAsync(writer, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(1, usage.GetProperty("usedInDocumentCount").GetInt32());
        var list = await GetAsync(writer, app, $"/api/v1/permit-types?q={s.TypeCode}").ConfigureAwait(true);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString() == s.TypeCode);
        Assert.Equal(1, item.GetProperty("usedInDocumentCount").GetInt32());

        // Читач без Document.View у проєкті бачить 0: лічильник не розкриває чужі документи.
        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var blind = await GetAsync(outsider, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(0, blind.GetProperty("usedInDocumentCount").GetInt32());
        var blindList = await GetAsync(outsider, app, $"/api/v1/permit-types?q={s.TypeCode}").ConfigureAwait(true);
        Assert.All(blindList.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Колонка_Lookup_на_прихований_реєстр_дозволів_приймає_дозвіл_як_поле_шапки_і_відхиляє_протермінований_та_чужий()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var patchUri = new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative);

        async Task<HttpResponseMessage> PatchAsync(string rowKey, long entryId)
            => await client.PatchAsJsonAsync(patchUri, new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.Period,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = s.PermitColumnCode, value = (object)entryId } },
                    },
                },
            }).ConfigureAwait(true);

        // Чинний дозвіл - вибір приймається (колонка вказує на прихований реєстр шапки типу).
        var accepted = await PatchAsync($"NEW{Guid.NewGuid():N}"[..12], s.Unused).ConfigureAwait(true);
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"{accepted.StatusCode}: {await accepted.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        // ...і він одразу видно в «де використано».
        var usages = await GetAsync(client, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1), (usages.GetProperty("documentCount").GetInt32(), usages.GetProperty("linkCount").GetInt32()));
        Assert.Equal("cell", usages.GetProperty("items")[0].GetProperty("kind").GetString());

        // Протермінований на кінець періоду - відмова тим самим кодом, що й для вибору в списку.
        var expired = await PatchAsync($"NEW{Guid.NewGuid():N}"[..12], s.Expired).ConfigureAwait(true);
        var expiredBody = await expired.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(expired.StatusCode == HttpStatusCode.UnprocessableEntity, $"{expired.StatusCode}: {expiredBody}");
        Assert.Equal("ECR-CELL-4223", JsonDocument.Parse(expiredBody).RootElement.GetProperty("errorCode").GetString());

        // Запис чужого довідника - так само відмова.
        var foreign = await PatchAsync($"NEW{Guid.NewGuid():N}"[..12], s.Foreign).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
    }

    /// <summary>
    /// ⛔ S-4 (аудит 2026-11-10): комірка поза межами читання документа (заборона на таблицю чи колонку, S6; роль, звужена
    /// періодами, D-214) у «де використано» не називається; поле шапки лишається. Диференційно: той самий запит, контроль —
    /// writer без заборон бачить обидві комірки; відрізняється лише грант (або область ролі).
    /// </summary>
    [Theory]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("period")]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Комірки_схованої_таблиці_не_називаються_у_використанні(string restriction)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var reader = await RestrictedReaderAsync(s, restriction).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var control = await GetAsync(writer, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);
        Assert.Equal(["header", "cell", "cell"], Kinds(control));

        using var restricted = await SignedInAsync(app, reader).ConfigureAwait(true);
        var usages = await GetAsync(restricted, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);
        var cells = usages.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "cell").ToList();

        if (restriction == "period")
        {
            // Роль бачить лише поточний період: комірка попереднього не називається.
            Assert.Equal([s.Period], cells.Select(c => c.GetProperty("periodKey").GetInt32()).ToList());
            Assert.Equal(2, usages.GetProperty("linkCount").GetInt32());
        }
        else
        {
            Assert.Empty(cells);
            Assert.Equal(1, usages.GetProperty("linkCount").GetInt32());
            Assert.DoesNotContain(s.TableCode, usages.GetRawText(), StringComparison.Ordinal);
        }

        Assert.Equal("header", Kinds(usages)[0]);
    }

    /// <summary>
    /// ⛔ B56: картка дозволу рахує ДОКУМЕНТИ, а не сегменти історії. Дозвіл <c>unused</c> стоїть лише в історії дозволів (j)
    /// одного документа — сегмент шапки «з дати», два сегменти рядків і частина поділеного місяця — і це ОДИН документ:
    /// <c>documentCount = historyDocumentCount = 1</c>, місць (полів і комірок) 0. Той самий документ, де дозвіл ще й у шапці й
    /// комірках (<c>active</c> + сегмент), — теж 1. Перелік дозволів (<c>usedInDocumentCount</c>) — те саме число; читач без
    /// доступу до проєкту бачить 0.
    /// </summary>
    /// <remarks>
    /// Мутаційні докази (виконано вручну, відновлено): (1) у <c>PermitStore.FindUsagesAsync</c> прибрати
    /// <c>.Union(historyDocuments)</c> з лічильника документів — <c>documentCount</c> 0 замість 1; (2) порахувати історію
    /// сегментами (<c>Concat</c> замість <c>Union</c> в <c>historyDocuments</c>) — <c>historyDocumentCount</c> 4 замість 1;
    /// (3) у <c>CountUsedInDocumentsAsync</c> прибрати гілку <c>fromHistory</c> — <c>usedInDocumentCount</c> 0 замість 1;
    /// (4) прибрати фільтр <c>projects.Contains(d.ProjectId)</c> у сегментах — читач іншого проєкту бачить 1 (outsider без
    /// <c>Document.View</c> цього не ловить: обробник повертає нулі ще до сховища).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Історія_дозволів_рахується_документом_а_не_сегментами()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var now = DateTime.UtcNow;
            var rowKeys = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && (r.Id == s.Document.RowIds[0] || r.Id == s.Document.RowIds[1]))
                .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
            var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);

            db.PermitSegments.AddRange(
                PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, firstDay.AddDays(9), s.Unused, s.WriterId, now),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKeys[0], s.PermitColumnId, firstDay.AddDays(9), s.Unused, s.WriterId, now),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKeys[1], s.PermitColumnId, firstDay.AddDays(19), s.Unused, s.WriterId, now),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKeys[1], s.PermitColumnId, firstDay.AddDays(4), s.Active, s.WriterId, now));
            db.CellPermitParts.Add(new CellPermitPart(
                s.Period, s.Document.DocumentId, s.Document.TableDefId, null, rowKeys[0], s.PermitColumnId, 2, s.Unused,
                firstDay.AddDays(9), firstDay.AddDays(27), null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var history = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1, 0), (
            history.GetProperty("documentCount").GetInt32(),
            history.GetProperty("historyDocumentCount").GetInt32(),
            history.GetProperty("linkCount").GetInt32()));
        // B128c: записи історії — рядки переліку виду history (3 сегменти + частина), лічильник місць історії — 4.
        Assert.Equal(["history", "history", "history", "history"], Kinds(history));
        Assert.Equal(4, history.GetProperty("historyLinkCount").GetInt32());

        // Шапка + комірки + сегмент у тому ж документі — так само один документ.
        var mixed = await GetAsync(writer, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1, 3), (
            mixed.GetProperty("documentCount").GetInt32(),
            mixed.GetProperty("historyDocumentCount").GetInt32(),
            mixed.GetProperty("linkCount").GetInt32()));

        // Дозвіл без історії: historyDocumentCount 0.
        var plain = await GetAsync(writer, app, $"/api/v1/permits/{s.Expired}/usages").ConfigureAwait(true);
        Assert.Equal((1, 0), (plain.GetProperty("documentCount").GetInt32(), plain.GetProperty("historyDocumentCount").GetInt32()));

        // Фільтр періоду: частина належить поточному місяцю, сегменти — документу з таблицями в періоді; періоду без таблиць — 0.
        var inPeriod = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?periodKey={s.Period}").ConfigureAwait(true);
        Assert.Equal(1, inPeriod.GetProperty("documentCount").GetInt32());
        var none = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?periodKey=209912").ConfigureAwait(true);
        Assert.Equal((0, 0), (none.GetProperty("documentCount").GetInt32(), none.GetProperty("historyDocumentCount").GetInt32()));

        var list = await GetAsync(writer, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.Equal(
            new Dictionary<long, int> { [s.Active] = 1, [s.Expired] = 1, [s.Unused] = 1 },
            list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt64(), i => i.GetProperty("usedInDocumentCount").GetInt32()));
        var type = await GetAsync(writer, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(1, type.GetProperty("usedInDocumentCount").GetInt32());

        // Права не розширено: читач без Document.View у проєкті не бачить документа й через історію.
        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var blind = await GetAsync(outsider, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (blind.GetProperty("documentCount").GetInt32(), blind.GetProperty("historyDocumentCount").GetInt32()));
        var blindList = await GetAsync(outsider, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.All(blindList.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));

        // Читач ІНШОГО проєкту (Document.View є, перелік проєктів непорожній — фільтр сховища справді працює): документа з
        // історією не бачить.
        var stranger = await OtherProjectReaderAsync().ConfigureAwait(true);
        using var other = await SignedInAsync(app, stranger).ConfigureAwait(true);
        var foreign = await GetAsync(other, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (foreign.GetProperty("documentCount").GetInt32(), foreign.GetProperty("historyDocumentCount").GetInt32()));
        var foreignList = await GetAsync(other, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.All(foreignList.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));
    }

    /// <summary>
    /// ⛔ B56 (F13): дозвіл лише в частині поділеного місяця рахується документом; тип дозволу, чиї дозволи лише в історії, —
    /// теж (<c>/permit-types/{code}/usage</c> і перелік типів); читач іншого проєкту бачить нулі.
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): вимкнути частини (гілку <c>partDocuments</c>) — <c>documentCount</c> 0;
    /// у <c>CountTypeUsedInDocumentsAsync</c> прибрати <c>.Concat(fromHistory)</c> — тип 0 замість 1; прибрати там фільтр
    /// проєктів в історії — читач іншого проєкту бачить 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Дозвіл_лише_в_частині_місяця_і_тип_лише_в_історії_рахуються_документом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // Лише історія: шапку й комірки з дозволами типу прибрано.
            await db.DocumentHeaderValues.Where(v => v.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            await db.CellValues.Where(c => c.ValueRegistryEntryId == s.Active || c.ValueRegistryEntryId == s.Expired)
                .ExecuteDeleteAsync().ConfigureAwait(true);

            var rowKey = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && r.Id == s.Document.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(true);
            var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
            db.CellPermitParts.Add(new CellPermitPart(
                s.Period, s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, 2, s.Unused,
                firstDay.AddDays(9), firstDay.AddDays(27), null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var part = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1, 0), (
            part.GetProperty("documentCount").GetInt32(),
            part.GetProperty("historyDocumentCount").GetInt32(),
            part.GetProperty("linkCount").GetInt32()));

        var type = await GetAsync(writer, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(1, type.GetProperty("usedInDocumentCount").GetInt32());
        var types = await GetAsync(writer, app, $"/api/v1/permit-types?q={s.TypeCode}").ConfigureAwait(true);
        Assert.Equal(1, Assert.Single(types.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString() == s.TypeCode)
            .GetProperty("usedInDocumentCount").GetInt32());

        var stranger = await OtherProjectReaderAsync().ConfigureAwait(true);
        using var other = await SignedInAsync(app, stranger).ConfigureAwait(true);
        var blindType = await GetAsync(other, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(0, blindType.GetProperty("usedInDocumentCount").GetInt32());
        var blindTypes = await GetAsync(other, app, $"/api/v1/permit-types?q={s.TypeCode}").ConfigureAwait(true);
        Assert.All(blindTypes.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));
        var blindPart = await GetAsync(other, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal(0, blindPart.GetProperty("documentCount").GetInt32());
    }

    /// <summary>
    /// ⛔ B56 (F13): історія логічно видаленого рядка — не використання (SoftDelete сегменти й частини не прибирає); сегмент,
    /// чинний з 10-го числа поточного місяця, не рахується у фільтрі попереднього місяця (<c>ValidFrom</c> ≤ кінець періоду).
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): у <c>PermitHistoryUsage.LiveSegments</c> і <c>LiveParts</c> прибрати вимогу
    /// живого рядка — <c>documentCount</c> 1 замість 0; прибрати умову <c>s.ValidFrom &lt;= p.PeriodEnd</c> — попередній місяць 1
    /// замість 0.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Історія_видаленого_рядка_і_сегмент_з_пізнішої_дати_не_рахуються()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // Рядок 3 видалено логічно в усіх періодах документа (у попередньому той самий ключ інакше лишився б живим).
            var deletedKey = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && r.Id == s.Document.RowIds[2]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(true);
            var instances = await db.TableInstances.AsNoTracking()
                .Where(t => t.DocumentId == s.Document.DocumentId).Select(t => t.Id).ToListAsync().ConfigureAwait(true);
            var alive = await db.TableRows
                .Where(r => instances.Contains(r.TableInstanceId) && r.RowKeyValue == deletedKey && !r.IsDeleted).ToListAsync().ConfigureAwait(true);
            foreach (var row in alive)
            {
                row.SoftDelete(DateTime.UtcNow);
            }

            db.PermitSegments.Add(PermitSegment.ForRow(
                s.Document.DocumentId, s.Document.TableDefId, null, deletedKey, s.PermitColumnId, firstDay.AddDays(9), s.Unused, s.WriterId, DateTime.UtcNow));
            db.CellPermitParts.Add(new CellPermitPart(
                s.Period, s.Document.DocumentId, s.Document.TableDefId, null, deletedKey, s.PermitColumnId, 2, s.Unused,
                firstDay.AddDays(9), firstDay.AddDays(27), null));
            await db.SaveChangesAsync().ConfigureAwait(true);

            // Передумова перевірки ValidFrom: попередній період є в календарі проєкту й закінчується раніше за сегмент.
            var previousEnd = await db.Periods.AsNoTracking()
                .Where(p => p.ProjectId == s.Document.ProjectId && p.PeriodKeyValue == s.PreviousPeriod).Select(p => p.PeriodEnd).SingleAsync().ConfigureAwait(true);
            Assert.True(previousEnd < firstDay.AddDays(9));
        }

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var deleted = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (deleted.GetProperty("documentCount").GetInt32(), deleted.GetProperty("historyDocumentCount").GetInt32()));
        var list = await GetAsync(writer, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.Equal(0, list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == s.Unused)
            .GetProperty("usedInDocumentCount").GetInt32());

        // Сегмент поля шапки з 10-го поточного місяця.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            db.PermitSegments.Add(PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, firstDay.AddDays(9), s.Unused, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var current = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?periodKey={s.Period}").ConfigureAwait(true);
        Assert.Equal((1, 1), (current.GetProperty("documentCount").GetInt32(), current.GetProperty("historyDocumentCount").GetInt32()));
        var previous = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?periodKey={s.PreviousPeriod}").ConfigureAwait(true);
        Assert.Equal((0, 0), (previous.GetProperty("documentCount").GetInt32(), previous.GetProperty("historyDocumentCount").GetInt32()));
    }

    /// <summary>
    /// ⛔ B56 (F13-2): «живий рядок» історії — у правильному місці. (1) Під фільтром періоду сегмент рядка рахується лише за
    /// рядком, живим САМЕ в цьому періоді (як комірки); без фільтра — бодай в одному. (2) Частина місяця — лише за рядком її
    /// місяця: рядок, видалений у місяці частини й живий в іншому, частину не рятує. (3) Рядок копії аркуша з тим самим ключем
    /// не оживляє сегмент оригіналу.
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): у <c>FindUsagesAsync</c> брати <c>LiveSegments</c> замість <c>LiveSegmentsIn</c> —
    /// поточний період 1 замість 0; у <c>LiveParts</c> прибрати <c>ti.PeriodKeyValue == p.PeriodKeyValue</c> — 1 замість 0 (N4);
    /// у <c>LiveSegments</c> прибрати <c>ti.SheetCopyId == s.SheetCopyId</c> — 1 замість 0 (N5).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Живий_рядок_історії_береться_за_періодом_місяцем_частини_і_копією_аркуша()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
        var previousFirstDay = new DateOnly(s.PreviousPeriod / 100, s.PreviousPeriod % 100, 1);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        string rowKey;
        await using (var db = builder.CreateContext())
        {
            // Рядок 1 видалено лише в поточному періоді; у попередньому той самий ключ живий.
            var row = await db.TableRows.SingleAsync(r => r.PeriodKeyValue == s.Period && r.Id == s.Document.RowIds[0]).ConfigureAwait(true);
            rowKey = row.RowKeyValue;
            row.SoftDelete(DateTime.UtcNow);
            db.PermitSegments.Add(PermitSegment.ForRow(
                s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, previousFirstDay.AddDays(4), s.Unused, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        async Task<int> DocumentsAsync(string query = "")
            => (await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages{query}").ConfigureAwait(true)).GetProperty("documentCount").GetInt32();

        // (1) Без фільтра — живий у попередньому, отже 1; у попередньому періоді — 1; у поточному рядок видалено — 0.
        Assert.Equal(1, await DocumentsAsync().ConfigureAwait(true));
        Assert.Equal(1, await DocumentsAsync($"?periodKey={s.PreviousPeriod}").ConfigureAwait(true));
        Assert.Equal(0, await DocumentsAsync($"?periodKey={s.Period}").ConfigureAwait(true));

        // (2) Замість сегмента — частина поточного місяця на тому ж рядку: у її місяці рядок видалено — 0 навіть без фільтра.
        await using (var db = builder.CreateContext())
        {
            await db.PermitSegments.Where(x => x.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            db.CellPermitParts.Add(new CellPermitPart(
                s.Period, s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, 2, s.Unused,
                firstDay.AddDays(9), firstDay.AddDays(27), null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        Assert.Equal(0, await DocumentsAsync().ConfigureAwait(true));

        // (3) Рядок видалено й у попередньому періоді; копія аркуша має ЖИВИЙ рядок з тим самим ключем. Сегмент оригіналу — 0.
        await using (var db = builder.CreateContext())
        {
            await db.CellPermitParts.Where(x => x.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            var instances = await db.TableInstances.AsNoTracking()
                .Where(t => t.DocumentId == s.Document.DocumentId).Select(t => t.Id).ToListAsync().ConfigureAwait(true);
            foreach (var alive in await db.TableRows.Where(r => instances.Contains(r.TableInstanceId) && r.RowKeyValue == rowKey && !r.IsDeleted).ToListAsync().ConfigureAwait(true))
            {
                alive.SoftDelete(DateTime.UtcNow);
            }

            var original = new DocumentSheet(s.Document.DocumentId, s.Document.SheetDefId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync().ConfigureAwait(true);
            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync().ConfigureAwait(true);

            var loader = new Ecr.Infrastructure.Persistence.BulkCellLoader(sql.ConnectionString, 1000);
            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(true);
            var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(true);
            var period = new PeriodKey(s.Period);
            db.TableInstances.Add(new TableInstance(period, instanceId, s.Document.DocumentId, s.Document.TableDefId, DateTime.UtcNow, copy.Id));
            db.TableRows.Add(new TableRow(period, rowId, instanceId, RowKey.Create(rowKey), 1, DateTime.UtcNow));
            db.PermitSegments.Add(PermitSegment.ForRow(
                s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, firstDay.AddDays(4), s.Unused, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        Assert.Equal(0, await DocumentsAsync().ConfigureAwait(true));
    }

    /// <summary>
    /// ⛔ B128 (борг B56): під фільтром періоду (1) рядок копії аркуша з тим самим ключем не оживляє сегмент оригіналу, рядок
    /// якого в цьому періоді видалено; (2) сегмент, перекритий наступним сегментом ТІЄЇ Ж цілі ще до початку періоду, у
    /// періоді не рахується, а наступний сегмент ІНШОЇ цілі (інший рядок) його не перекриває.
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): у <c>PermitHistoryUsage.LiveSegmentsIn</c> прибрати
    /// <c>ti.SheetCopyId == s.SheetCopyId</c> — копія дає 1 замість 0 (N5b); у <c>FindUsagesAsync</c> прибрати умову
    /// «немає наступного сегмента цілі з ValidFrom ≤ PeriodStart» — перекритий сегмент шапки дає 1 замість 0; прибрати з неї
    /// рівність <c>RowKey</c> — сегмент іншого рядка «перекриває» чужий, 0 замість 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Під_фільтром_періоду_копія_аркуша_і_перекритий_сегмент_не_рахуються()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
        var previousFirstDay = new DateOnly(s.PreviousPeriod / 100, s.PreviousPeriod % 100, 1);
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        async Task<int> DocumentsAsync(string query = "")
            => (await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages{query}").ConfigureAwait(true)).GetProperty("documentCount").GetInt32();

        // (1) Рядок 1 оригіналу видалено в поточному періоді (у попередньому живий); копія аркуша має в поточному періоді
        // ЖИВИЙ рядок з тим самим ключем. Сегмент оригіналу під фільтром поточного періоду — 0.
        string rowKey;
        await using (var db = builder.CreateContext())
        {
            var row = await db.TableRows.SingleAsync(r => r.PeriodKeyValue == s.Period && r.Id == s.Document.RowIds[0]).ConfigureAwait(true);
            rowKey = row.RowKeyValue;
            row.SoftDelete(DateTime.UtcNow);

            var original = new DocumentSheet(s.Document.DocumentId, s.Document.SheetDefId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync().ConfigureAwait(true);
            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync().ConfigureAwait(true);

            var loader = new Ecr.Infrastructure.Persistence.BulkCellLoader(sql.ConnectionString, 1000);
            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(true);
            var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(true);
            var period = new PeriodKey(s.Period);
            db.TableInstances.Add(new TableInstance(period, instanceId, s.Document.DocumentId, s.Document.TableDefId, DateTime.UtcNow, copy.Id));
            db.TableRows.Add(new TableRow(period, rowId, instanceId, RowKey.Create(rowKey), 1, DateTime.UtcNow));
            db.PermitSegments.Add(PermitSegment.ForRow(
                s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, previousFirstDay.AddDays(4), s.Unused, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        Assert.Equal(0, await DocumentsAsync($"?periodKey={s.Period}").ConfigureAwait(true));
        Assert.Equal(1, await DocumentsAsync($"?periodKey={s.PreviousPeriod}").ConfigureAwait(true));

        // (2a) Шапка: unused з 5-го попереднього місяця, active — з ПЕРШОГО дня поточного (ValidFrom = PeriodStart). У поточному
        // періоді unused уже не чинний — 0; у попередньому — 1; без фільтра — 1 (історія є).
        await using (var db = builder.CreateContext())
        {
            await db.PermitSegments.Where(x => x.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            db.PermitSegments.AddRange(
                PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, previousFirstDay.AddDays(4), s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, firstDay, s.Active, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        Assert.Equal(0, await DocumentsAsync($"?periodKey={s.Period}").ConfigureAwait(true));
        Assert.Equal(1, await DocumentsAsync($"?periodKey={s.PreviousPeriod}").ConfigureAwait(true));
        Assert.Equal(1, await DocumentsAsync().ConfigureAwait(true));

        // (2b) Рядок 2 (живий): unused з 5-го попереднього місяця; наступний сегмент з першого дня поточного — на ІНШОМУ рядку
        // (рядок 3, інша ціль). Він unused не перекриває: у поточному періоді — 1.
        await using (var db = builder.CreateContext())
        {
            await db.PermitSegments.Where(x => x.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            var keys = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && (r.Id == s.Document.RowIds[1] || r.Id == s.Document.RowIds[2]))
                .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
            db.PermitSegments.AddRange(
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, keys[0], s.PermitColumnId, previousFirstDay.AddDays(4), s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, keys[1], s.PermitColumnId, firstDay, s.Active, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        Assert.Equal(1, await DocumentsAsync($"?periodKey={s.Period}").ConfigureAwait(true));
    }

    /// <summary>
    /// ⛔ B128c: записи історії дозволів — рядки переліку виду <c>history</c> з датою <c>validFrom</c>. Живий сегмент рядка й
    /// сегмент шапки називаються; сегмент логічно видаленого рядка — ні; перекритий до початку періоду — не під фільтром цього
    /// періоду; читач іншого проєкту не бачить нічого; заборона на таблицю ховає запис історії рядка, але не шапки (S-4, як
    /// комірки); <c>limit</c> рахує й записи історії (<c>truncated</c>).
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): прибрати рядки історії з переліку (<c>.Concat(history…)</c>) — переліку немає;
    /// у обробнику не фільтрувати <c>history</c> з таблицею — запис схованої таблиці названо; <c>truncated</c> без
    /// <c>historyLinkCount</c> — <c>false</c> при <c>limit=1</c>; не передати <c>ValidFrom</c> у рядок — дата <c>null</c>; прибрати
    /// фільтр проєктів у <c>segmentRows</c> — читач іншого проєкту бачить рядки.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Рядки_історії_в_переліку_з_датою_без_видалених_перекритих_чужих_і_схованих()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
        var previousFirstDay = new DateOnly(s.PreviousPeriod / 100, s.PreviousPeriod % 100, 1);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var keys = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && (r.Id == s.Document.RowIds[0] || r.Id == s.Document.RowIds[2]))
                .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
            var (liveKey, deletedKey) = (keys[0], keys[1]);

            // Рядок 3 видалено в УСІХ періодах (у поточному вже видалений сценарієм).
            var instances = await db.TableInstances.AsNoTracking()
                .Where(t => t.DocumentId == s.Document.DocumentId).Select(t => t.Id).ToListAsync().ConfigureAwait(true);
            foreach (var row in await db.TableRows.Where(r => instances.Contains(r.TableInstanceId) && r.RowKeyValue == deletedKey && !r.IsDeleted).ToListAsync().ConfigureAwait(true))
            {
                row.SoftDelete(DateTime.UtcNow);
            }

            db.PermitSegments.AddRange(
                // Шапка: unused з 5-го попереднього місяця, з першого дня поточного — active (перекриває unused у поточному).
                PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, previousFirstDay.AddDays(4), s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, firstDay, s.Active, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, liveKey, s.PermitColumnId, firstDay.AddDays(9), s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, deletedKey, s.PermitColumnId, firstDay.AddDays(9), s.Unused, s.WriterId, DateTime.UtcNow));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        // Без фільтра: шапка (з 5-го попереднього) і живий рядок (з 10-го поточного); видалений рядок — ні.
        var all = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        var items = all.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["history", "history"], Kinds(all));
        Assert.Equal((0, 2, false), (all.GetProperty("linkCount").GetInt32(), all.GetProperty("historyLinkCount").GetInt32(), all.GetProperty("truncated").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("tableCode").ValueKind);
        Assert.Equal("PERMIT", items[0].GetProperty("fieldCode").GetString());
        Assert.Equal(previousFirstDay.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), items[0].GetProperty("validFrom").GetString());
        Assert.Equal(s.TableCode, items[1].GetProperty("tableCode").GetString());
        Assert.Equal(s.PermitColumnCode, items[1].GetProperty("fieldCode").GetString());
        Assert.Equal(firstDay.AddDays(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), items[1].GetProperty("validFrom").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("periodKey").ValueKind);

        // Поточний період: шапку перекрито з першого дня — лишається тільки рядок.
        var current = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?periodKey={s.Period}").ConfigureAwait(true);
        Assert.Equal(s.TableCode, Assert.Single(current.GetProperty("items").EnumerateArray()).GetProperty("tableCode").GetString());

        // limit рахує й історію.
        var limited = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?limit=1").ConfigureAwait(true);
        Assert.Equal((true, 1), (limited.GetProperty("truncated").GetBoolean(), limited.GetProperty("items").GetArrayLength()));

        // Читач іншого проєкту — нічого.
        var stranger = await OtherProjectReaderAsync().ConfigureAwait(true);
        using var other = await SignedInAsync(app, stranger).ConfigureAwait(true);
        var foreign = await GetAsync(other, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (foreign.GetProperty("items").GetArrayLength(), foreign.GetProperty("historyLinkCount").GetInt32()));

        // Заборона на таблицю: запис історії рядка не названо, запис шапки — так.
        var reader = await RestrictedReaderAsync(s, "table").ConfigureAwait(true);
        using var restricted = await SignedInAsync(app, reader).ConfigureAwait(true);
        var hidden = await GetAsync(restricted, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        var visible = Assert.Single(hidden.GetProperty("items").EnumerateArray());
        Assert.Equal(("history", JsonValueKind.Null), (visible.GetProperty("kind").GetString(), visible.GetProperty("tableCode").ValueKind));
        Assert.Equal(1, hidden.GetProperty("historyLinkCount").GetInt32());
        Assert.DoesNotContain(s.TableCode, hidden.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⛔ B128 (F13, вцілілі мутації умови перекриття): наступний сегмент ІНШОЇ цілі не перекриває сегмент під фільтром періоду,
    /// навіть якщо збігається все, крім одного складника цілі: (B3c) той самий рядок, інша колонка; (B5) інше поле шапки того
    /// самого документа; (B7) той самий ключ рядка, але в копії аркуша. Скрізь сегмент unused з 5-го попереднього місяця
    /// лишається чинним у поточному періоді — 1, хоч «наступний» сегмент іншої цілі починається з першого дня періоду.
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): прибрати з умови перекриття в <c>FindUsagesAsync</c> рівність
    /// <c>ColumnDefId</c> (B3c), <c>HeaderFieldDefId</c> (B5) або <c>SheetCopyId</c> (B7) — відповідна фаза дає 0 замість 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Під_фільтром_періоду_сегмент_іншої_колонки_поля_чи_копії_не_перекриває()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
        var from = new DateOnly(s.PreviousPeriod / 100, s.PreviousPeriod % 100, 1).AddDays(4);
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        async Task<int> CurrentAsync()
            => (await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages?periodKey={s.Period}").ConfigureAwait(true))
                .GetProperty("documentCount").GetInt32();

        async Task ReplaceAsync(Func<Ecr.Infrastructure.Persistence.EcrDbContext, Task> add)
        {
            await using var db = builder.CreateContext();
            await db.PermitSegments.Where(x => x.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            await add(db).ConfigureAwait(true);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        string rowKey;
        int otherColumnId;
        int otherFieldId;
        long copyId;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && r.Id == s.Document.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(true);
            otherColumnId = s.Document.ColumnDefIds[0];
            Assert.NotEqual(s.PermitColumnId, otherColumnId);

            var lookup = await db.HeaderFieldDefs.AsNoTracking()
                .Where(f => f.Id == s.HeaderFieldId).Select(f => f.LookupRegistryDefId!.Value).SingleAsync().ConfigureAwait(true);
            var otherField = new HeaderFieldDef(s.Document.TemplateVersionId, EcrCode.Create("PERMIT2"), Text("Permit 2"), 2, CellDataType.Lookup);
            otherField.SetLookup(lookup);
            db.HeaderFieldDefs.Add(otherField);
            var original = new DocumentSheet(s.Document.DocumentId, s.Document.SheetDefId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync().ConfigureAwait(true);
            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync().ConfigureAwait(true);
            (otherFieldId, copyId) = (otherField.Id, copy.Id);
        }

        // Контроль: наступний сегмент ТІЄЇ Ж цілі (той самий рядок і колонка) — перекриває, 0.
        await ReplaceAsync(db =>
        {
            db.PermitSegments.AddRange(
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, from, s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, firstDay, s.Active, s.WriterId, DateTime.UtcNow));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        Assert.Equal(0, await CurrentAsync().ConfigureAwait(true));

        // (B3c) Той самий рядок, інша колонка.
        await ReplaceAsync(db =>
        {
            db.PermitSegments.AddRange(
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, from, s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKey, otherColumnId, firstDay, s.Active, s.WriterId, DateTime.UtcNow));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        Assert.Equal(1, await CurrentAsync().ConfigureAwait(true));

        // (B5) Інше поле шапки того самого документа.
        await ReplaceAsync(db =>
        {
            db.PermitSegments.AddRange(
                PermitSegment.ForHeader(s.Document.DocumentId, s.HeaderFieldId, from, s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForHeader(s.Document.DocumentId, otherFieldId, firstDay, s.Active, s.WriterId, DateTime.UtcNow));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        Assert.Equal(1, await CurrentAsync().ConfigureAwait(true));

        // (B7) Той самий ключ рядка в копії аркуша.
        await ReplaceAsync(db =>
        {
            db.PermitSegments.AddRange(
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, null, rowKey, s.PermitColumnId, from, s.Unused, s.WriterId, DateTime.UtcNow),
                PermitSegment.ForRow(s.Document.DocumentId, s.Document.TableDefId, copyId, rowKey, s.PermitColumnId, firstDay, s.Active, s.WriterId, DateTime.UtcNow));
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        Assert.Equal(1, await CurrentAsync().ConfigureAwait(true));
    }

    /// <summary>
    /// ⛔ B128c (F13, S-4/D-214): межі читання запису історії рядка перевіряються в періодах, де рядок живий І сегмент чинний,
    /// а не в місяці <c>validFrom</c>. Читач з областю «від поточного періоду»: (A) сегмент рядка, живого лише в попередньому
    /// (недоступному) періоді й чинного з поточного — не називається; (B) сегмент живого рядка з попереднього місяця, чинний і в
    /// поточному, — називається, і з фільтром поточного періоду теж; (C) заборона на таблицю зменшує <c>linkCount</c> лише на
    /// сховані комірки, а <c>historyLinkCount</c> — на сховані записи історії.
    /// </summary>
    /// <remarks>
    /// Мутації (виконано вручну, відновлено): повернути перевірку за місяцем <c>validFrom</c> (D1) — (A) і (B) червоніють;
    /// з фільтром брати місяць <c>validFrom</c> замість періоду фільтра (M2b) — (B) з фільтром червоніє; зменшувати
    /// <c>linkCount</c> на всі сховані рядки (M6) — (C) <c>linkCount</c> 0 замість 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Межі_читання_запису_історії_рядка_за_періодами_де_рядок_живий_і_сегмент_чинний()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var firstDay = new DateOnly(s.Period / 100, s.Period % 100, 1);
        var previousFrom = new DateOnly(s.PreviousPeriod / 100, s.PreviousPeriod % 100, 1).AddDays(4);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var reader = await RestrictedReaderAsync(s, "period").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var restricted = await SignedInAsync(app, reader).ConfigureAwait(true);

        string liveKey;
        string deletedKey;
        await using (var db = builder.CreateContext())
        {
            var keys = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == s.Period && (r.Id == s.Document.RowIds[0] || r.Id == s.Document.RowIds[2]))
                .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
            (liveKey, deletedKey) = (keys[0], keys[1]);
        }

        async Task ReplaceAsync(params PermitSegment[] segments)
        {
            await using var db = builder.CreateContext();
            await db.PermitSegments.Where(x => x.DocumentId == s.Document.DocumentId).ExecuteDeleteAsync().ConfigureAwait(true);
            db.PermitSegments.AddRange(segments);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        // (A) Рядок 3 видалено в поточному періоді (сценарій), живий лише в попередньому — недоступному читачеві. Сегмент — з
        // 10-го поточного: у попередньому ще не чинний, у поточному рядка немає.
        await ReplaceAsync(PermitSegment.ForRow(
            s.Document.DocumentId, s.Document.TableDefId, null, deletedKey, s.PermitColumnId, firstDay.AddDays(9), s.Unused, s.WriterId, DateTime.UtcNow));
        var leak = await GetAsync(restricted, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (leak.GetProperty("items").GetArrayLength(), leak.GetProperty("historyLinkCount").GetInt32()));
        Assert.DoesNotContain(deletedKey, leak.GetRawText(), StringComparison.Ordinal);

        // ⛔ B128c: лічильники рахують рівно ті записи, що й перелік — навіть писачеві сегмент рядка, живого лише до початку дії
        // сегмента, не є використанням: перелік порожній, документів 0, у переліку дозволів 0. Мутація: повернути старе
        // LiveSegments («рядок живий бодай в одному періоді») — 1 замість 0.
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var counted = await GetAsync(writer, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0, 0, 0), (
            counted.GetProperty("items").GetArrayLength(), counted.GetProperty("documentCount").GetInt32(),
            counted.GetProperty("historyDocumentCount").GetInt32(), counted.GetProperty("historyLinkCount").GetInt32()));
        var list = await GetAsync(writer, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.Equal(0, list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == s.Unused)
            .GetProperty("usedInDocumentCount").GetInt32());

        // (B) Живий рядок 1, сегмент з 5-го попереднього місяця — чинний і в поточному (доступному).
        await ReplaceAsync(PermitSegment.ForRow(
            s.Document.DocumentId, s.Document.TableDefId, null, liveKey, s.PermitColumnId, previousFrom, s.Unused, s.WriterId, DateTime.UtcNow));
        var shown = await GetAsync(restricted, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal(liveKey, Assert.Single(shown.GetProperty("items").EnumerateArray()).GetProperty("rowKey").GetString());
        var filtered = await GetAsync(restricted, app, $"/api/v1/permits/{s.Unused}/usages?periodKey={s.Period}").ConfigureAwait(true);
        Assert.Equal(liveKey, Assert.Single(filtered.GetProperty("items").EnumerateArray()).GetProperty("rowKey").GetString());

        // (C) Заборона на таблицю: active — шапка + дві комірки + сегмент рядка. Сховано дві комірки й запис історії рядка.
        await ReplaceAsync(PermitSegment.ForRow(
            s.Document.DocumentId, s.Document.TableDefId, null, liveKey, s.PermitColumnId, previousFrom, s.Active, s.WriterId, DateTime.UtcNow));
        var tableReader = await RestrictedReaderAsync(s, "table").ConfigureAwait(true);
        using var noTable = await SignedInAsync(app, tableReader).ConfigureAwait(true);
        var hidden = await GetAsync(noTable, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);
        Assert.Equal(["header"], Kinds(hidden));
        Assert.Equal((1, 0), (hidden.GetProperty("linkCount").GetInt32(), hidden.GetProperty("historyLinkCount").GetInt32()));
    }

    /// <summary>Читач з <c>Registry.View</c> і <c>Document.View</c>, грант <c>Read</c> — лише на проєкт окремого документа.</summary>
    private async Task<string> OtherProjectReaderAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var foreignDocument = await builder.BuildAsync(202601, columnCount: 1, rowCount: 1).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var user = NewUser(db, "pu_x", out var name);
        var role = new Role(EcrCode.Create($"PUX_{Guid.NewGuid():N}"), Text("Other project reader"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.AddRange(new RolePermission(role.Id, "Registry.View"), new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, foreignDocument.ProjectId, GrantLevel.Read));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return name;
    }

    private static List<string?> Kinds(JsonElement usages)
        => [.. usages.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString())];

    /// <summary>
    /// Читач з <c>Registry.View</c> (окрема роль), <c>Document.View</c> і грантом <c>Read</c> на проєкт документа; плюс заборона на
    /// таблицю (<c>table</c>), на колонку дозволу (<c>column</c>) або область ролі з поточного періоду (<c>period</c>).
    /// </summary>
    private async Task<string> RestrictedReaderAsync(Scenario s, string restriction)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = NewUser(db, "pu_r", out var name);
        // Registry.View — окремою роллю без області: право звуженої ролі (D-214) глобальним не стає.
        var catalog = new Role(EcrCode.Create($"PUC_{Guid.NewGuid():N}"), Text("Permit usage catalog"));
        var role = new Role(EcrCode.Create($"PUR_{Guid.NewGuid():N}"), Text("Permit usage restricted"));
        db.Roles.AddRange(catalog, role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.AddRange(new RolePermission(catalog.Id, "Registry.View"), new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(catalog.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, GrantLevel.Read));
        if (restriction == "table")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Table, s.Document.TableDefId, GrantLevel.Read, isDeny: true));
        }
        else if (restriction == "column")
        {
            var columnId = await db.ColumnDefs.AsNoTracking()
                .Where(c => c.TableDefId == s.Document.TableDefId && c.Code == s.PermitColumnCode).Select(c => c.Id).SingleAsync().ConfigureAwait(false);
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, columnId, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, null);
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (restriction == "period")
        {
            var scope = RoleAssignmentScope.Create([s.Document.ProjectId], null, new PeriodKey(s.Period), null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}").ConfigureAwait(false);
        }

        return name;
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> GetAsync(HttpClient client, EcrApiFactory app, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static User NewUser(Ecr.Infrastructure.Persistence.EcrDbContext db, string prefix, out string name)
    {
        name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        return user;
    }

    /// <summary>
    /// Документ з шапкою <c>PERMIT</c> і колонкою <c>Lookup</c> на прихований реєстр шапки опублікованого типу дозволів; чинний,
    /// протермінований і невикористаний дозвіл; чужий запис; три користувачі.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var period = (siteToday.Year * 100) + siteToday.Month;
        var previousMonth = siteToday.AddMonths(-1);
        var previous = (previousMonth.Year * 100) + previousMonth.Month;

        var document = await builder.BuildAsync(period, columnCount: 1, rowCount: 3, rowMode: TableRowMode.Mixed).ConfigureAwait(false);
        var previousInstance = await builder.AddPeriodInstanceAsync(document, new PeriodKey(previous)).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = DateTime.UtcNow;

        // Тип дозволів: прихований реєстр шапки (PRM_…) - ціль Lookup; чужий довідник - для відмови.
        var header = new RegistryDef(EcrCode.Create($"PRM_PU{tag}"), Text("Permit header"), isTemporal: true);
        var other = new RegistryDef(EcrCode.Create($"OTHER_{tag}"), Text("Other"), isTemporal: false);
        db.RegistryDefs.AddRange(header, other);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var type = new PermitTypeDef(EcrCode.Create($"PU{tag}"), Text("Usage type"), PermitCategory.Air, header.Id);
        db.PermitTypeDefs.Add(type);
        await db.SaveChangesAsync().ConfigureAwait(false);
        type.Publish(1, now);

        RegistryEntry Entry(RegistryDef def, string code, DateOnly from, DateOnly to)
        {
            var entry = new RegistryEntry(def.Id, EcrCode.Create(code), Text(code), 1, now);
            entry.SetValidity(from, to);
            db.RegistryEntries.Add(entry);
            return entry;
        }

        var active = Entry(header, $"ACT{tag}", new DateOnly(2020, 1, 1), new DateOnly(2099, 1, 1));
        var expired = Entry(header, $"EXP{tag}", new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1));
        var unused = Entry(header, $"UNU{tag}", new DateOnly(2020, 1, 1), new DateOnly(2099, 1, 1));
        var foreign = Entry(other, $"FOR{tag}", new DateOnly(2020, 1, 1), new DateOnly(2099, 1, 1));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Структура шаблону: поле шапки та колонка Lookup на реєстр шапки типу.
        var headerField = new HeaderFieldDef(document.TemplateVersionId, EcrCode.Create("PERMIT"), Text("Permit"), 1, CellDataType.Lookup);
        headerField.SetLookup(header.Id);
        db.HeaderFieldDefs.Add(headerField);
        var column = new ColumnDef(document.TableDefId, EcrCode.Create($"PCOL{tag}"), Text("Permit"), 2, CellDataType.Lookup);
        column.SetLookup(header.Id);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Дані: шапка -> active; поточний період: рядок 1 -> active, рядок 2 -> expired, рядок 3 (ВИДАЛЕНИЙ) -> active;
        // попередній період: рядок 1 -> active.
        db.DocumentHeaderValues.Add(new DocumentHeaderValue(
            document.DocumentId, headerField.Id, new DocumentHeaderValueData { ValueRegistryEntryId = active.Id }));

        void Cell(int periodKey, long rowId, long entryId)
            => db.CellValues.Add(new CellValue(
                new CellAddress(new PeriodKey(periodKey), rowId, column.Id), document.TableDefId,
                new CellValueData { ValueRegistryEntryId = entryId }));

        Cell(period, document.RowIds[0], active.Id);
        Cell(period, document.RowIds[1], expired.Id);
        Cell(period, document.RowIds[2], active.Id);
        Cell(previous, previousInstance.RowIds[0], active.Id);
        var deletedRow = await db.TableRows
            .SingleAsync(r => r.PeriodKeyValue == period && r.Id == document.RowIds[2]).ConfigureAwait(false);
        deletedRow.SoftDelete(now);

        var firstRowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period && r.Id == document.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        var tableCode = await db.TableDefs.AsNoTracking().Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        // Користувачі: writer (читає й пише документ, бачить дозволи), outsider (дозволи без доступу до проєкту),
        // noRegistry (документ без права читати довідники).
        var writer = NewUser(db, "pu_w", out var writerName);
        var outsider = NewUser(db, "pu_o", out var outsiderName);
        var noRegistry = NewUser(db, "pu_n", out var noRegistryName);
        var role = new Role(EcrCode.Create($"PU_{Guid.NewGuid():N}"), Text("Permit usage"));
        var viewOnly = new Role(EcrCode.Create($"PUV_{Guid.NewGuid():N}"), Text("Permit view only"));
        var docOnly = new Role(EcrCode.Create($"PUD_{Guid.NewGuid():N}"), Text("Document only"));
        db.Roles.AddRange(role, viewOnly, docOnly);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.AddRange(
            new RolePermission(role.Id, "Registry.View"), new RolePermission(role.Id, "Document.View"),
            new RolePermission(viewOnly.Id, "Registry.View"), new RolePermission(docOnly.Id, "Document.View"));
        db.RoleAssignments.AddRange(
            new RoleAssignment(role.Id, writer.Id, null), new RoleAssignment(viewOnly.Id, outsider.Id, null),
            new RoleAssignment(docOnly.Id, noRegistry.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        // Проєкт активний, поточний період відкритий (як у CellWriteRoundTripTests): інакше запис комірки законно відмовляє.
        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var currentPeriod = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == period).ConfigureAwait(false);
        currentPeriod.RecomputeBoundaries(policy, SiteZone);
        currentPeriod.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document, document.SheetCode, tableCode, column.Code, firstRowKey, period, previous, type.Code,
            active.Id, expired.Id, unused.Id, foreign.Id, writerName, outsiderName, noRegistryName,
            headerField.Id, column.Id, writer.Id);
    }

    private sealed record Scenario(
        TestDocument Document, string SheetCode, string TableCode, string PermitColumnCode, string FirstRowKey,
        int Period, int PreviousPeriod, string TypeCode, long Active, long Expired, long Unused, long Foreign,
        string Writer, string Outsider, string NoRegistry, int HeaderFieldId, int PermitColumnId, int WriterId);
}
