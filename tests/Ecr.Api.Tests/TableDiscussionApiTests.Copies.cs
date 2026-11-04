// tests/Ecr.Api.Tests/TableDiscussionApiTests.Copies.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// TC02: чат таблиці на копіях аркушів. Копія аркуша — окремий чат (<c>sheetCopyId</c>): повідомлення, лічильники,
/// «прочитано», ідемпотентність <c>clientMessageId</c> і згадки не мішаються з оригіналом; чужий/недійсний
/// <c>sheetCopyId</c> — та сама 404, що й схована таблиця; видалення копії забирає її чат і пише кількість у журнал.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не комітяться): прибрати <c>m.SheetCopyKey == sheetCopyKey</c> у
/// <c>TableDiscussionStore.ListAsync</c> → червоніє <c>Копія_аркуша_має_окремий_чат_…</c>; прибрати перевірку
/// <c>SheetDefIdOfTableCode</c> у <c>TableDiscussionAccess.ResolveAsync</c> → <c>Чужа_або_недійсна_копія_…</c>; прибрати
/// <c>DeleteChatAsync</c> у <c>DocumentSheetCopyHandler.DeleteCopyAsync</c> → <c>Видалення_копії_…</c>.
/// </remarks>
public sealed partial class TableDiscussionApiTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Копія_аркуша_має_окремий_чат_лічильники_прочитано_і_ключі_підсумку()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var copies = await AddCopiesAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);

        await PostAsync(alice, s, s.Table1, "в оригіналі", s.Period).ConfigureAwait(true);
        var inCopy = await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), new { body = "в копії", periodKey = s.Period }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Created, inCopy.StatusCode);
        await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), new { body = "в копії ще", periodKey = s.Period }).ConfigureAwait(true);

        // Сторінки не перетинаються.
        var original = await GetPageAsync(bob, s, s.Table1, string.Empty).ConfigureAwait(true);
        Assert.Equal(["в оригіналі"], original.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToList());
        var copy = await GetPageAsync(bob, s, s.Table1, "?sheetCopyId=" + Num(copies.Copy1)).ConfigureAwait(true);
        Assert.Equal(["в копії", "в копії ще"], copy.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToList());

        // Підсумок: ключі «КОД» і «КОД#id»; непрочитане — окремо.
        var items = await SummaryItemsAsync(bob, s).ConfigureAwait(true);
        var originalRow = items.Single(i => i.GetProperty("key").GetString() == s.Table1);
        var copyRow = items.Single(i => i.GetProperty("key").GetString() == s.Table1 + "#" + Num(copies.Copy1));
        Assert.Equal(1, originalRow.GetProperty("unread").GetInt32());
        Assert.Equal(JsonValueKind.Null, originalRow.GetProperty("sheetCopyId").ValueKind);
        Assert.Equal(2, copyRow.GetProperty("unread").GetInt32());
        Assert.Equal(copies.Copy1, copyRow.GetProperty("sheetCopyId").GetInt64());
        Assert.Equal(s.Table1, copyRow.GetProperty("tableCode").GetString());

        // «Прочитано» у копії не знімає непрочитане оригіналу, і навпаки; позначка рахується на чат.
        var lastInCopy = copy.GetProperty("lastMessageId").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync(CopyReadUri(s, s.Table1, copies.Copy1), new { lastReadMessageId = lastInCopy }).ConfigureAwait(true)).StatusCode);
        items = await SummaryItemsAsync(bob, s).ConfigureAwait(true);
        Assert.Equal(1, items.Single(i => i.GetProperty("key").GetString() == s.Table1).GetProperty("unread").GetInt32());
        Assert.Equal(0, items.Single(i => i.GetProperty("key").GetString() == s.Table1 + "#" + Num(copies.Copy1)).GetProperty("unread").GetInt32());
        Assert.Equal(0, (await GetPageAsync(bob, s, s.Table1, string.Empty).ConfigureAwait(true)).GetProperty("lastReadMessageId").GetInt64());

        // ETag сторінки копії не збігається з оригінальним.
        var etagOriginal = (await bob.GetAsync(MessagesUri(s, s.Table1, string.Empty)).ConfigureAwait(true)).Headers.ETag?.Tag;
        var etagCopy = (await bob.GetAsync(MessagesUri(s, s.Table1, "?sheetCopyId=" + Num(copies.Copy1))).ConfigureAwait(true)).Headers.ETag?.Tag;
        Assert.NotEqual(etagOriginal, etagCopy);

        // Правка й видалення — лише в своєму чаті: id повідомлення копії через оригінал — 404.
        var copyMessageId = copy.GetProperty("messages")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PatchAsJsonAsync(MessageUri(s, s.Table1, copyMessageId), new { body = "x" }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PatchAsJsonAsync(CopyMessageUri(s, s.Table1, copyMessageId, copies.Copy1), new { body = "в копії (виправлено)" }).ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чужа_або_недійсна_копія_дає_ту_саму_404_що_й_схована_таблиця()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var copies = await AddCopiesAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var denied = await SignedInAsync(app, s.Denied.UserName).ConfigureAwait(true);

        var reference = await alice.GetAsync(MessagesUri(s, "NO_SUCH_TABLE", string.Empty)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, reference.StatusCode);
        var expected = await ShapeAsync(reference).ConfigureAwait(true);

        // Копія ІНШОГО аркуша, оригінал (не копія), неіснуюча, нуль і від'ємна — усе 404 тієї ж форми.
        foreach (var bad in new long[] { copies.Copy2, copies.Original1, 987_654_321, 0, -5 })
        {
            var response = await alice.GetAsync(MessagesUri(s, s.Table1, "?sheetCopyId=" + Num(bad))).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(expected, await ShapeAsync(response).ConfigureAwait(true));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy2), new { body = "x", periodKey = s.Period }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PutAsJsonAsync(CopyReadUri(s, s.Table1, copies.Copy2), new { lastReadMessageId = 1 }).ConfigureAwait(true)).StatusCode);

        // Копія видима, коли видима її таблиця: схована таблиця 2 - 404 і для її копії.
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table2, copies.Copy2), new { body = "секрет", periodKey = s.Period }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await denied.GetAsync(MessagesUri(s, s.Table2, "?sheetCopyId=" + Num(copies.Copy2))).ConfigureAwait(true)).StatusCode);
        Assert.DoesNotContain(await SummaryItemsAsync(denied, s).ConfigureAwait(true), i => i.GetProperty("tableCode").GetString() == s.Table2);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повтор_POST_з_clientMessageId_ідемпотентний_у_межах_копії_а_не_між_нею_й_оригіналом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var copies = await AddCopiesAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        var clientId = Guid.NewGuid();
        var payload = new { body = "раз", periodKey = s.Period, clientMessageId = clientId };

        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync(MessagesUri(s, s.Table1, string.Empty), payload).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), payload).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), payload).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1b), payload).ConfigureAwait(true)).StatusCode);

        Assert.Single((await GetPageAsync(alice, s, s.Table1, string.Empty).ConfigureAwait(true)).GetProperty("messages").EnumerateArray());
        Assert.Single((await GetPageAsync(alice, s, s.Table1, "?sheetCopyId=" + Num(copies.Copy1)).ConfigureAwait(true)).GetProperty("messages").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Згадка_в_копії_несе_sheetCopyId_а_прочитано_знімає_лише_свій_чат()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var copies = await AddCopiesAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);

        await alice.PostAsJsonAsync(MessagesUri(s, s.Table1, string.Empty), new { body = "оригінал @Боб", periodKey = s.Period, mentionUserIds = new[] { s.Bob.Id } }).ConfigureAwait(true);
        await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), new { body = "копія @Боб", periodKey = s.Period, mentionUserIds = new[] { s.Bob.Id } }).ConfigureAwait(true);

        var tasks = JsonDocument.Parse(await bob.GetStringAsync(new Uri("/api/v1/me/table-discussion-mentions", UriKind.Relative)).ConfigureAwait(true)).RootElement.GetProperty("items");
        Assert.Equal(2, tasks.GetArrayLength());
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("sheetCopyId").ValueKind == JsonValueKind.Null);
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("sheetCopyId").ValueKind == JsonValueKind.Number && t.GetProperty("sheetCopyId").GetInt64() == copies.Copy1);

        var lastInCopy = (await GetPageAsync(bob, s, s.Table1, "?sheetCopyId=" + Num(copies.Copy1)).ConfigureAwait(true)).GetProperty("lastMessageId").GetInt64();
        await bob.PutAsJsonAsync(CopyReadUri(s, s.Table1, copies.Copy1), new { lastReadMessageId = lastInCopy + 1000 }).ConfigureAwait(true);

        // Позначка в копії зрізується до id ЇЇ чату й не знімає згадку оригіналу (чий id менший, але інший чат).
        var after = JsonDocument.Parse(await bob.GetStringAsync(new Uri("/api/v1/me/table-discussion-mentions", UriKind.Relative)).ConfigureAwait(true)).RootElement.GetProperty("items");
        var left = Assert.Single(after.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, left.GetProperty("sheetCopyId").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_копії_забирає_її_чат_у_транзакції_показує_кількість_до_підтвердження_і_пише_її_в_журнал()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var copies = await AddCopiesAsync(s).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        await PostAsync(alice, s, s.Table1, "оригінал лишається", s.Period).ConfigureAwait(true);
        await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), new { body = "c1", periodKey = s.Period, mentionUserIds = new[] { s.Bob.Id } }).ConfigureAwait(true);
        await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), new { body = "c2", periodKey = s.Period }).ConfigureAwait(true);
        var removed = await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1), new { body = "c3", periodKey = s.Period }).ConfigureAwait(true);
        var removedId = JsonDocument.Parse(await removed.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("id").GetInt64();
        await alice.DeleteAsync(CopyMessageUri(s, s.Table1, removedId, copies.Copy1)).ConfigureAwait(true); // видалене не рахується
        await bob.PutAsJsonAsync(CopyReadUri(s, s.Table1, copies.Copy1), new { lastReadMessageId = 1 }).ConfigureAwait(true);
        await alice.PostAsJsonAsync(CopyMessagesUri(s, s.Table1, copies.Copy1b), new { body = "інша копія", periodKey = s.Period }).ConfigureAwait(true);

        // До підтвердження: у переліку таблиць копія несе кількість живих повідомлень; оригінал - null.
        var tables = JsonDocument.Parse(await manager.GetStringAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}/tables?periodKey={Num(s.Period)}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        var copyTable = tables.EnumerateArray().Single(t => t.TryGetProperty("sheetCopyId", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt64() == copies.Copy1 && t.GetProperty("tableCode").GetString() == s.Table1);
        Assert.Equal(2, copyTable.GetProperty("chatMessageCount").GetInt32());
        var originalTable = tables.EnumerateArray().First(t => t.GetProperty("tableCode").GetString() == s.Table1 && (!t.TryGetProperty("sheetCopyId", out var id) || id.ValueKind == JsonValueKind.Null));
        Assert.True(!originalTable.TryGetProperty("chatMessageCount", out var originalCount) || originalCount.ValueKind == JsonValueKind.Null);

        var deleted = await manager.DeleteAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}/sheet-copies/{Num(copies.Copy1)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync().ConfigureAwait(true));

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(0, await db.TableDiscussionMessages.CountAsync(m => m.DocumentId == s.DocumentId && m.SheetCopyKey == copies.Copy1).ConfigureAwait(true));
        Assert.Equal(0, await db.TableDiscussionMentions.CountAsync(m => m.DocumentId == s.DocumentId && m.SheetCopyKey == copies.Copy1).ConfigureAwait(true));
        Assert.Equal(0, await db.TableDiscussionReads.CountAsync(r => r.DocumentId == s.DocumentId && r.SheetCopyKey == copies.Copy1).ConfigureAwait(true));
        Assert.Equal(0, await db.TableDiscussions.CountAsync(r => r.DocumentId == s.DocumentId && r.SheetCopyKey == copies.Copy1).ConfigureAwait(true));

        // Оригінал і інша копія не зачеплені.
        Assert.Equal(1, await db.TableDiscussionMessages.CountAsync(m => m.DocumentId == s.DocumentId && m.SheetCopyKey == 0).ConfigureAwait(true));
        Assert.Equal(1, await db.TableDiscussionMessages.CountAsync(m => m.DocumentId == s.DocumentId && m.SheetCopyKey == copies.Copy1b).ConfigureAwait(true));

        // Журнал видалення копії несе кількість живих повідомлень чату; тексти видалених лишились у журналі подій чату.
        var audit = (await EventDetailsAsync(s, "DocumentSheetCopyDeleted").ConfigureAwait(true)).Select(d => JsonDocument.Parse(d).RootElement).Single();
        Assert.Equal(2, audit.GetProperty("chatMessages").GetInt32());
        Assert.Equal(copies.Copy1, audit.GetProperty("sheetCopyId").GetInt64());
        Assert.NotEmpty(await EventDetailsAsync(s, "TableDiscussionMessagePosted").ConfigureAwait(true));

        // Видалена копія вже не адресується.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(MessagesUri(s, s.Table1, "?sheetCopyId=" + Num(copies.Copy1))).ConfigureAwait(true)).StatusCode);
    }

    // ── помічники копій ─────────────────────────────────────────────────────────────

    private static Uri CopyMessagesUri(Scenario s, string table, long copy) => MessagesUri(s, table, "?sheetCopyId=" + Num(copy));

    private static Uri CopyReadUri(Scenario s, string table, long copy)
        => new($"/api/v1/documents/{Num(s.DocumentId)}/tables/{table}/read?sheetCopyId={Num(copy)}", UriKind.Relative);

    private static Uri CopyMessageUri(Scenario s, string table, long id, long copy)
        => new($"/api/v1/documents/{Num(s.DocumentId)}/tables/{table}/messages/{Num(id)}?sheetCopyId={Num(copy)}", UriKind.Relative);

    private static async Task<List<JsonElement>> SummaryItemsAsync(HttpClient client, Scenario s)
    {
        var text = await client.GetStringAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}/discussions/summary", UriKind.Relative)).ConfigureAwait(false);
        return [.. JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone())];
    }

    private static async Task<string> ShapeAsync(HttpResponseMessage response)
    {
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        return root.GetProperty("errorCode").GetString() + "|" + root.GetProperty("messageKey").GetString();
    }

    /// <summary>Склад документа: оригінали обох аркушів і по копії (аркуш 1 - дві копії, аркуш 2 - одна).</summary>
    private async Task<CopySet> AddCopiesAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        // Аркуш таблиці визначаємо за екземплярами документа (код таблиці в тесті унікальний лише в межах версії).
        var rows = await (
            from i in db.TableInstances
            where i.DocumentId == s.DocumentId
            join t in db.TableDefs on i.TableDefId equals t.Id
            select new { t.Code, t.SheetDefId }).Distinct().ToListAsync().ConfigureAwait(false);
        var sheet1 = rows.Single(r => (string)r.Code == s.Table1).SheetDefId;
        var sheet2 = rows.Single(r => (string)r.Code == s.Table2).SheetDefId;

        var original1 = new DocumentSheet(s.DocumentId, sheet1);
        var original2 = new DocumentSheet(s.DocumentId, sheet2);
        db.DocumentSheets.AddRange(original1, original2);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var copy1 = original1.CreateCopy(2);
        var copy1b = original1.CreateCopy(3);
        var copy2 = original2.CreateCopy(2);
        db.DocumentSheets.AddRange(copy1, copy1b, copy2);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new CopySet(original1.Id, copy1.Id, copy1b.Id, copy2.Id);
    }

    private sealed record CopySet(long Original1, long Copy1, long Copy1b, long Copy2);
}
