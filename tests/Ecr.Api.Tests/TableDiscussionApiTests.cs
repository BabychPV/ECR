// tests/Ecr.Api.Tests/TableDiscussionApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Чат таблиці документа (TC) наскрізно через HTTP: матриця доступу, згадки, вікно правки, м'яке видалення, пагінація
/// за ключем, опитування з ETag, відсутність записів у комірки/розрахунки, межі тексту, звуження періодами.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — окремий тест, що червоніє):
/// <list type="bullet">
/// <item>прибрати перевірку <c>scope.CanReadTableCode</c> у <c>TableDiscussionAccess.ResolveAsync</c> (повертати доступ
/// будь-кому) → <c>Схована_таблиця_для_читача_не_існує_…</c>;</item>
/// <item>прибрати фільтр <c>CanSeeAsync</c> у <c>TableDiscussionAccess.FilterMentionsAsync</c> →
/// <c>Згадка_користувача_без_доступу_…</c>;</item>
/// <item>замінити <c>allowedPeriods</c> на <c>null</c> у <c>TableDiscussionQueries.ListMessagesAsync</c> →
/// <c>Роль_звужена_періодами_…</c>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class TableDiscussionApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-TableChat-2026!";
    private const int Nov = 202611;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повідомлення_лічильники_непрочитане_прочитано_і_ETag()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);

        var posted = await PostAsync(alice, s, s.Table1, "Перше повідомлення", s.Period).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var message = JsonDocument.Parse(await posted.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("Перше повідомлення", message.GetProperty("body").GetString());
        Assert.True(message.GetProperty("canEdit").GetBoolean());
        Assert.True(message.GetProperty("canDelete").GetBoolean());
        Assert.Equal(s.Period, message.GetProperty("periodKey").GetInt32());
        var firstId = message.GetProperty("id").GetInt64();

        // Автор не бачить власне як непрочитане; Боб бачить 1.
        var aliceSummary = await SummaryAsync(alice, s).ConfigureAwait(true);
        Assert.Equal(1, aliceSummary.GetProperty(s.Table1).GetProperty("total").GetInt32());
        Assert.Equal(0, aliceSummary.GetProperty(s.Table1).GetProperty("unread").GetInt32());
        var bobSummary = await SummaryAsync(bob, s).ConfigureAwait(true);
        Assert.Equal(1, bobSummary.GetProperty(s.Table1).GetProperty("unread").GetInt32());
        Assert.False(bobSummary.GetProperty(s.Table1).GetProperty("mentionedMe").GetBoolean());

        // Чужа сторінка: не можна ні правити, ні видаляти.
        var page = await GetPageAsync(bob, s, s.Table1, string.Empty).ConfigureAwait(true);
        var shown = Assert.Single(page.GetProperty("messages").EnumerateArray());
        Assert.False(shown.GetProperty("canEdit").GetBoolean());
        Assert.False(shown.GetProperty("canDelete").GetBoolean());
        Assert.True(page.GetProperty("canWrite").GetBoolean());

        // ETag/304: повтор без змін — 304, після нового повідомлення — 200 з новою ревізією.
        var uri = MessagesUri(s, s.Table1, "?after=" + firstId.ToString(CultureInfo.InvariantCulture));
        var first = await bob.GetAsync(uri).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag));
        using (var conditional = new HttpRequestMessage(HttpMethod.Get, uri))
        {
            conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
            Assert.Equal(HttpStatusCode.NotModified, (await bob.SendAsync(conditional).ConfigureAwait(true)).StatusCode);
        }

        await PostAsync(alice, s, s.Table1, "Друге", s.Period).ConfigureAwait(true);
        using (var changed = new HttpRequestMessage(HttpMethod.Get, uri))
        {
            changed.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var response = await bob.SendAsync(changed).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
            Assert.Equal("Друге", Assert.Single(body.GetProperty("messages").EnumerateArray()).GetProperty("body").GetString());
        }

        // «Прочитано» знімає непрочитане, не зменшується назад.
        var last = (await GetPageAsync(bob, s, s.Table1, string.Empty).ConfigureAwait(true)).GetProperty("lastMessageId").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync(ReadUri(s, s.Table1), new { lastReadMessageId = last + 1000 }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(0, (await SummaryAsync(bob, s).ConfigureAwait(true)).GetProperty(s.Table1).GetProperty("unread").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync(ReadUri(s, s.Table1), new { lastReadMessageId = 1 }).ConfigureAwait(true)).StatusCode);
        var after = await GetPageAsync(bob, s, s.Table1, string.Empty).ConfigureAwait(true);
        Assert.Equal(last, after.GetProperty("lastReadMessageId").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Схована_таблиця_для_читача_не_існує_а_без_Discuss_лише_читання()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var denied = await SignedInAsync(app, s.Denied.UserName).ConfigureAwait(true);
        using var viewer = await SignedInAsync(app, s.ViewerOnly.UserName).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, s.Stranger.UserName).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(alice, s, s.Table2, "Секретна розмова", s.Period).ConfigureAwait(true)).StatusCode);
        await PostAsync(alice, s, s.Table1, "Відкрита розмова", s.Period).ConfigureAwait(true);

        // Схована таблиця: GET/POST/PUT read/candidates — ТА САМА 404, що й на неіснуючий документ.
        var hiddenGet = await denied.GetAsync(MessagesUri(s, s.Table2, string.Empty)).ConfigureAwait(true);
        var missing = await denied.GetAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId + 100000)}/tables/{s.Table2}/messages", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, hiddenGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            JsonDocument.Parse(await missing.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("messageKey").GetString(),
            JsonDocument.Parse(await hiddenGet.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(denied, s, s.Table2, "спроба", s.Period).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await denied.PutAsJsonAsync(ReadUri(s, s.Table2), new { lastReadMessageId = 1 }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await denied.GetAsync(new Uri($"{MessagesUri(s, s.Table2, string.Empty).OriginalString.Replace("/messages", "/mention-candidates", StringComparison.Ordinal)}?q=ab", UriKind.Relative)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await denied.GetAsync(MessagesUri(s, "NO_SUCH_TABLE", string.Empty)).ConfigureAwait(true)).StatusCode);

        // Підсумок не називає схованої таблиці.
        var summary = await SummaryAsync(denied, s).ConfigureAwait(true);
        Assert.True(summary.TryGetProperty(s.Table1, out _));
        Assert.False(summary.TryGetProperty(s.Table2, out _));

        // Без Discuss: читає (200), пише — 403 ECR-AUTH-0403; чат видно, canWrite = false.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(MessagesUri(s, s.Table1, string.Empty)).ConfigureAwait(true)).StatusCode);
        var forbidden = await PostAsync(viewer, s, s.Table1, "я лише дивлюсь", s.Period).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("ECR-AUTH-0403", JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("errorCode").GetString());
        Assert.False((await GetPageAsync(viewer, s, s.Table1, string.Empty).ConfigureAwait(true)).GetProperty("canWrite").GetBoolean());

        // Без Document.View — відмова, документа не видно.
        Assert.False((await stranger.GetAsync(MessagesUri(s, s.Table1, string.Empty)).ConfigureAwait(true)).IsSuccessStatusCode);
        Assert.False((await PostAsync(stranger, s, s.Table1, "x", s.Period).ConfigureAwait(true)).IsSuccessStatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Згадка_користувача_без_доступу_відкидається_і_імена_не_витікають()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);
        using var denied = await SignedInAsync(app, s.Denied.UserName).ConfigureAwait(true);

        // Таблиця 2 схована від Denied: згадка Боба проходить, Denied (і неіснуючий id) — мовчки відкидаються.
        var response = await alice.PostAsJsonAsync(
            MessagesUri(s, s.Table2, string.Empty),
            new { body = "Дивись @Боб", periodKey = s.Period, mentionUserIds = new[] { s.Bob.Id, s.Denied.Id, 987654321 } }).ConfigureAwait(true);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var mentions = JsonDocument.Parse(text).RootElement.GetProperty("mentions").EnumerateArray().ToList();
        Assert.Equal([s.Bob.Id], mentions.Select(m => m.GetProperty("userId").GetInt32()).ToList());
        Assert.DoesNotContain(s.Denied.UserName, text, StringComparison.Ordinal);

        // «Мої задачі»: у Боба згадка є; у Denied — немає.
        var bobTasks = JsonDocument.Parse(await bob.GetStringAsync(new Uri("/api/v1/me/table-discussion-mentions", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        Assert.Single(bobTasks.GetProperty("items").EnumerateArray());
        var deniedTasks = JsonDocument.Parse(await denied.GetStringAsync(new Uri("/api/v1/me/table-discussion-mentions", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        Assert.Empty(deniedTasks.GetProperty("items").EnumerateArray());
        Assert.True((await SummaryAsync(bob, s).ConfigureAwait(true)).GetProperty(s.Table2).GetProperty("mentionedMe").GetBoolean());

        // Кандидати для схованої таблиці: Denied не повертається ніколи, навіть за точним іменем.
        var candidates = await alice.GetStringAsync(new Uri(
            $"/api/v1/documents/{Num(s.DocumentId)}/tables/{s.Table2}/mention-candidates?q={s.Denied.UserName[..6]}", UriKind.Relative)).ConfigureAwait(true);
        Assert.DoesNotContain(s.Denied.UserName, candidates, StringComparison.Ordinal);
        var visibleCandidates = await alice.GetStringAsync(new Uri(
            $"/api/v1/documents/{Num(s.DocumentId)}/tables/{s.Table2}/mention-candidates?q={s.Bob.UserName[..6]}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Contains(s.Bob.UserName, visibleCandidates, StringComparison.Ordinal);

        // Прочитав — задача зникла.
        var last = (await GetPageAsync(bob, s, s.Table2, string.Empty).ConfigureAwait(true)).GetProperty("lastMessageId").GetInt64();
        await bob.PutAsJsonAsync(ReadUri(s, s.Table2), new { lastReadMessageId = last }).ConfigureAwait(true);
        var afterRead = JsonDocument.Parse(await bob.GetStringAsync(new Uri("/api/v1/me/table-discussion-mentions", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        Assert.Empty(afterRead.GetProperty("items").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_лише_автором_і_лише_у_вікні_15_хвилин()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);

        var id = await PostIdAsync(alice, s, s.Table1, "Початковий текст").ConfigureAwait(true);

        // Автор у вікні — 200, позначка editedAt.
        var edit = await alice.PatchAsJsonAsync(MessageUri(s, s.Table1, id), new { body = "Виправлений" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = JsonDocument.Parse(await edit.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("Виправлений", edited.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);

        // Чужий — 403 з ключем.
        var foreign = await bob.PatchAsJsonAsync(MessageUri(s, s.Table1, id), new { body = "хакнув" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("err.ECR-ACCS-0403.discussionEditForbidden", await KeyAsync(foreign).ConfigureAwait(true));

        // Вікно: 14 хв 50 с тому — ще можна, 15 хв 10 с — вже ні (точна межа — у доменному тесті з годинником).
        await AgeAsync(id, TimeSpan.FromSeconds(14 * 60 + 50)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await alice.PatchAsJsonAsync(MessageUri(s, s.Table1, id), new { body = "ще встигла" }).ConfigureAwait(true)).StatusCode);
        await AgeAsync(id, TimeSpan.FromSeconds(15 * 60 + 10)).ConfigureAwait(true);
        var late = await alice.PatchAsJsonAsync(MessageUri(s, s.Table1, id), new { body = "запізно" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.discussionEditWindowClosed", await KeyAsync(late).ConfigureAwait(true));
        var shown = Assert.Single((await GetPageAsync(alice, s, s.Table1, string.Empty).ConfigureAwait(true)).GetProperty("messages").EnumerateArray());
        Assert.Equal("ще встигла", shown.GetProperty("body").GetString());
        Assert.False(shown.GetProperty("canEdit").GetBoolean());

        // Повідомлення з іншої таблиці/чужого чату — 404, як і неіснуюче.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PatchAsJsonAsync(MessageUri(s, s.Table2, id), new { body = "x" }).ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task М_яке_видалення_автор_чи_модератор_текст_лишається_в_журналі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);
        using var moderator = await SignedInAsync(app, s.Moderator.UserName).ConfigureAwait(true);

        var mine = await PostIdAsync(alice, s, s.Table1, "Видалю сама").ConfigureAwait(true);
        var other = await PostIdAsync(alice, s, s.Table1, "Видалить модератор").ConfigureAwait(true);
        await PostIdAsync(alice, s, s.Table1, "Лишається").ConfigureAwait(true);

        // Чужий без модерації — 403.
        var forbidden = await bob.DeleteAsync(MessageUri(s, s.Table1, mine)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("err.ECR-ACCS-0403.discussionDeleteForbidden", await KeyAsync(forbidden).ConfigureAwait(true));

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync(MessageUri(s, s.Table1, mine)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await moderator.DeleteAsync(MessageUri(s, s.Table1, other)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync(MessageUri(s, s.Table1, mine)).ConfigureAwait(true)).StatusCode); // повтор

        var page = await GetPageAsync(bob, s, s.Table1, string.Empty).ConfigureAwait(true);
        var messages = page.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        foreach (var deleted in messages.Where(m => m.GetProperty("isDeleted").GetBoolean()))
        {
            Assert.Equal(JsonValueKind.Null, deleted.GetProperty("body").ValueKind);
            Assert.False(deleted.GetProperty("canEdit").GetBoolean());
        }

        Assert.Equal(2, messages.Count(m => m.GetProperty("isDeleted").GetBoolean()));
        Assert.Equal(1, (await SummaryAsync(bob, s).ConfigureAwait(true)).GetProperty(s.Table1).GetProperty("total").GetInt32());
        Assert.Equal(1, (await SummaryAsync(bob, s).ConfigureAwait(true)).GetProperty(s.Table1).GetProperty("unread").GetInt32());

        // Правка видаленого — 422 з ключем.
        var edit = await alice.PatchAsJsonAsync(MessageUri(s, s.Table1, mine), new { body = "воскреслий" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.discussionMessageDeleted", await KeyAsync(edit).ConfigureAwait(true));

        // Текст видаленого — у журналі аудиту; усі три типи подій є.
        var details = await EventDetailsAsync(s, "TableDiscussionMessageDeleted").ConfigureAwait(true);
        // JSON журналу екранує кирилицю (\uXXXX), тому текст читаємо розбором, а не підрядком.
        var parsed = details.Select(d => JsonDocument.Parse(d).RootElement).ToList();
        Assert.Contains(parsed, d => d.GetProperty("body").GetString() == "Видалю сама" && !d.GetProperty("byModerator").GetBoolean());
        Assert.Contains(parsed, d => d.GetProperty("body").GetString() == "Видалить модератор" && d.GetProperty("byModerator").GetBoolean());
        Assert.Equal(3, (await EventDetailsAsync(s, "TableDiscussionMessagePosted").ConfigureAwait(true)).Count);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пагінація_за_ключем_стабільна_при_нових_повідомленнях_і_after_віддає_лише_нове()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);

        var ids = new List<long>();
        for (var i = 1; i <= 7; i++)
        {
            ids.Add(await PostIdAsync(alice, s, s.Table1, $"m{i.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(true));
        }

        var newest = await GetPageAsync(alice, s, s.Table1, "?limit=3").ConfigureAwait(true);
        Assert.Equal(ids.TakeLast(3), Ids(newest));
        Assert.True(newest.GetProperty("hasMoreBefore").GetBoolean());

        // Між сторінками з'являються нові повідомлення — старші сторінки не зсуваються.
        await PostIdAsync(alice, s, s.Table1, "m8").ConfigureAwait(true);
        await PostIdAsync(alice, s, s.Table1, "m9").ConfigureAwait(true);

        var before = ids[4];
        var second = await GetPageAsync(alice, s, s.Table1, $"?limit=3&before={Num(before)}").ConfigureAwait(true);
        Assert.Equal(ids.Skip(1).Take(3), Ids(second));
        var third = await GetPageAsync(alice, s, s.Table1, $"?limit=3&before={Num(ids[1])}").ConfigureAwait(true);
        Assert.Equal([ids[0]], Ids(third));
        Assert.False(third.GetProperty("hasMoreBefore").GetBoolean());

        // after: рівно ті, що новіші за останнє побачене, за зростанням.
        var poll = await GetPageAsync(alice, s, s.Table1, $"?after={Num(ids[^1])}").ConfigureAwait(true);
        Assert.Equal(2, Ids(poll).Count);
        Assert.Equal(Ids(poll).Order().ToList(), Ids(poll));

        // before і after разом — 422.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await alice.GetAsync(MessagesUri(s, s.Table1, "?before=5&after=1")).ConfigureAwait(true)).StatusCode);

        // Фільтр періоду.
        await alice.PostAsJsonAsync(MessagesUri(s, s.Table1, string.Empty), new { body = "листопад", periodKey = Nov }).ConfigureAwait(true);
        var november = await GetPageAsync(alice, s, s.Table1, $"?periodKey={Num(Nov)}").ConfigureAwait(true);
        Assert.Equal("листопад", Assert.Single(november.GetProperty("messages").EnumerateArray()).GetProperty("body").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чат_не_пише_в_комірки_рядки_і_розрахунки()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var moderator = await SignedInAsync(app, s.Moderator.UserName).ConfigureAwait(true);

        var before = await FingerprintAsync().ConfigureAwait(true);

        var id = await PostIdAsync(alice, s, s.Table1, "Нічого не чіпаю").ConfigureAwait(true);
        await alice.PatchAsJsonAsync(MessageUri(s, s.Table1, id), new { body = "ще раз" }).ConfigureAwait(true);
        await alice.PutAsJsonAsync(ReadUri(s, s.Table1), new { lastReadMessageId = id }).ConfigureAwait(true);
        await moderator.DeleteAsync(MessageUri(s, s.Table1, id)).ConfigureAwait(true);

        Assert.Equal(before, await FingerprintAsync().ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Межі_тексту_керівні_символи_прибираються_і_недійсне_відхиляється()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);

        // Рівно 4000 — можна; 4001 — 422 з ключем.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(alice, s, s.Table1, new string('я', 4000), s.Period).ConfigureAwait(true)).StatusCode);
        var tooLong = await PostAsync(alice, s, s.Table1, new string('я', 4001), s.Period).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.discussionBodyInvalid", await KeyAsync(tooLong).ConfigureAwait(true));

        // Порожнє, лише пробіли/керівні, недійсний період, забагато згадок — 422.
        foreach (var bad in new[] { string.Empty, "   \n\t ", new string([(char)1, (char)2, (char)0x200B]) })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(alice, s, s.Table1, bad, s.Period).ConfigureAwait(true)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(alice, s, s.Table1, "x", 13).ConfigureAwait(true)).StatusCode);
        var tooMany = await alice.PostAsJsonAsync(
            MessagesUri(s, s.Table1, string.Empty),
            new { body = "x", periodKey = s.Period, mentionUserIds = Enumerable.Range(1, 11).ToArray() }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);

        // Керівні символи вирізаються, розмітка лишається ТЕКСТОМ (не HTML), переноси нормалізуються.
        var response = await PostAsync(alice, s, s.Table1, "a" + (char)0 + "b" + (char)0x202E + "c\r\nd<script>alert(1)</script>", s.Period).ConfigureAwait(true);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("body").GetString();
        Assert.Equal("abc\nd<script>alert(1)</script>", body);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_звужена_періодами_бачить_і_пише_лише_видимі_періоди()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var narrowed = await SignedInAsync(app, s.NovemberOnly.UserName).ConfigureAwait(true);

        await PostAsync(alice, s, s.Table1, "січень-1", s.Period).ConfigureAwait(true);
        await PostAsync(alice, s, s.Table1, "січень-2", s.Period).ConfigureAwait(true);
        await PostAsync(alice, s, s.Table1, "листопад-1", Nov).ConfigureAwait(true);

        // Звужений бачить лише листопад: сторінка, підсумок, непрочитане — один предикат.
        var page = await GetPageAsync(narrowed, s, s.Table1, string.Empty).ConfigureAwait(true);
        Assert.Equal(["листопад-1"], page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToList());
        var summary = (await SummaryAsync(narrowed, s).ConfigureAwait(true)).GetProperty(s.Table1);
        Assert.Equal(1, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("unread").GetInt32());

        // Фільтр періоду з невидимим періодом — порожньо, нічого не розкрито.
        Assert.Empty((await GetPageAsync(narrowed, s, s.Table1, $"?periodKey={Num(s.Period)}").ConfigureAwait(true)).GetProperty("messages").EnumerateArray());

        // Писати можна у видимий період; у невидимий — 403 з ключем.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(narrowed, s, s.Table1, "відповідь", Nov).ConfigureAwait(true)).StatusCode);
        var forbidden = await PostAsync(narrowed, s, s.Table1, "не туди", s.Period).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("err.ECR-ACCS-0403.discussionPeriodForbidden", await KeyAsync(forbidden).ConfigureAwait(true));

        // Згадка: Writer бачить обидва періоди, звужений — лише листопад; кандидат у січні — без звуженого.
        var january = await alice.GetStringAsync(new Uri(
            $"/api/v1/documents/{Num(s.DocumentId)}/tables/{s.Table1}/mention-candidates?q={s.NovemberOnly.UserName[..6]}&periodKey={Num(s.Period)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.DoesNotContain(s.NovemberOnly.UserName, january, StringComparison.Ordinal);
        var november = await alice.GetStringAsync(new Uri(
            $"/api/v1/documents/{Num(s.DocumentId)}/tables/{s.Table1}/mention-candidates?q={s.NovemberOnly.UserName[..6]}&periodKey={Num(Nov)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Contains(s.NovemberOnly.UserName, november, StringComparison.Ordinal);

        var inJanuary = await alice.PostAsJsonAsync(
            MessagesUri(s, s.Table1, string.Empty),
            new { body = "січневе", periodKey = s.Period, mentionUserIds = new[] { s.NovemberOnly.Id } }).ConfigureAwait(true);
        Assert.Empty(JsonDocument.Parse(await inJanuary.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("mentions").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_чернетки_документа_прибирає_його_чат_а_текст_лишається_в_журналі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var posted = await manager.PostAsJsonAsync(
            MessagesUri(s, s.Table1, string.Empty),
            new { body = "до видалення", periodKey = s.Period, mentionUserIds = new[] { s.Bob.Id } }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        await manager.PutAsJsonAsync(ReadUri(s, s.Table1), new { lastReadMessageId = 1 }).ConfigureAwait(true);

        var deleted = await manager.DeleteAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(deleted.IsSuccessStatusCode, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync().ConfigureAwait(true)}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(0, await db.TableDiscussionMessages.CountAsync(m => m.DocumentId == s.DocumentId).ConfigureAwait(true));
        Assert.Equal(0, await db.TableDiscussionMentions.CountAsync(m => m.DocumentId == s.DocumentId).ConfigureAwait(true));
        Assert.Equal(0, await db.TableDiscussions.CountAsync(m => m.DocumentId == s.DocumentId).ConfigureAwait(true));
        Assert.Single(await EventDetailsAsync(s, "TableDiscussionMessagePosted").ConfigureAwait(true));
    }
    // ── помічники ───────────────────────────────────────────────────────────────────

    private static List<long> Ids(JsonElement page)
        => [.. page.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt64())];

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static Uri MessagesUri(Scenario s, string table, string query)
        => new($"/api/v1/documents/{Num(s.DocumentId)}/tables/{table}/messages{query}", UriKind.Relative);

    private static Uri MessageUri(Scenario s, string table, long id)
        => new($"/api/v1/documents/{Num(s.DocumentId)}/tables/{table}/messages/{Num(id)}", UriKind.Relative);

    private static Uri ReadUri(Scenario s, string table)
        => new($"/api/v1/documents/{Num(s.DocumentId)}/tables/{table}/read", UriKind.Relative);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Scenario s, string table, string body, int period)
        => client.PostAsJsonAsync(MessagesUri(s, table, string.Empty), new { body, periodKey = period });

    private static async Task<long> PostIdAsync(HttpClient client, Scenario s, string table, string body)
    {
        var response = await PostAsync(client, s, table, body, s.Period).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<JsonElement> GetPageAsync(HttpClient client, Scenario s, string table, string query)
    {
        var response = await client.GetAsync(MessagesUri(s, table, query)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Підсумок як словник «код таблиці → рядок».</summary>
    private static async Task<JsonElement> SummaryAsync(HttpClient client, Scenario s)
    {
        var text = await client.GetStringAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}/discussions/summary", UriKind.Relative)).ConfigureAwait(false);
        var items = JsonDocument.Parse(text).RootElement.GetProperty("items");
        var map = new Dictionary<string, JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            map[item.GetProperty("tableCode").GetString()!] = item.Clone();
        }

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(map));
        return doc.RootElement.Clone();
    }

    private static async Task<string?> KeyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("messageKey").GetString();

    private async Task AgeAsync(long messageId, TimeSpan age)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var seconds = (int)age.TotalSeconds;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.TableDiscussionMessage SET CreatedAt = DATEADD(second, {-seconds}, SYSUTCDATETIME()) WHERE Id = {messageId}").ConfigureAwait(false);
    }

    private async Task<List<string>> EventDetailsAsync(Scenario s, string eventType)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var marker = $"\"documentId\":{Num(s.DocumentId)}";
        return await db.Database
            .SqlQuery<string>($"SELECT DetailsJson AS Value FROM aud.SecurityEvent WHERE EventType = {eventType} AND DetailsJson LIKE {"%" + marker + "%"}")
            .ToListAsync().ConfigureAwait(false);
    }

    /// <summary>Відбиток таблиць даних і розрахунків: кількість і контрольна сума по всій базі (колекція тестів послідовна).</summary>
    private async Task<string> FingerprintAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var parts = new List<string>();
        foreach (var table in new[] { "doc.CellValue", "doc.TableRow", "doc.TableInstance", "calc.CalculationRun", "calc.CalculationResult", "wf.ApprovalState" })
        {
            var sqlText = $"SELECT CONCAT(COUNT_BIG(*), '/', ISNULL(CHECKSUM_AGG(CHECKSUM(*)), 0)) AS Value FROM {table}";
#pragma warning disable EF1002 // ім'я таблиці — константа цього тесту
            parts.Add(table + "=" + await db.Database.SqlQueryRaw<string>(sqlText).SingleAsync().ConfigureAwait(false));
#pragma warning restore EF1002
        }

        return string.Join(';', parts);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await builder.AddPeriodInstanceAsync(document, new PeriodKey(Nov)).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Другий аркуш із таблицею (її ховає заборона на аркуш) — екземпляр за основний період.
        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create($"SHB{tag}"), Text("Sheet B"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table2 = new TableDef(sheet2.Id, EcrCode.Create($"TBB{tag}"), Text("Table B"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ColumnDefs.Add(new ColumnDef(table2.Id, EcrCode.Create($"CB{tag}"), Text("Col B"), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table2.Id, RowKey.Create($"RB{tag}"), 1, Text("Row B"), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2 = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var row2 = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(document.PeriodKey, instance2, document.DocumentId, table2.Id, now));
        db.TableRows.Add(new TableRow(document.PeriodKey, row2, instance2, RowKey.Create($"RB{tag}"), 1, now));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table1 = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        async Task<Person> NewUserAsync(
            string prefix, string[] permissions, ResourceKind? deny = null, int denyId = 0, PeriodKey? only = null, bool grant = true, GrantLevel level = GrantLevel.Write)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"TC_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Table chat test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            var assignment = new RoleAssignment(role.Id, user.Id, null);
            if (only is { } period)
            {
                assignment.SetScope(RoleAssignmentScope.Create([document.ProjectId], periodFrom: period, periodTo: period));
            }

            db.RoleAssignments.Add(assignment);
            if (grant)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
            }

            if (deny is { } kind)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, denyId, GrantLevel.None, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return new Person(user.Id, userName);
        }

        string[] writer = ["Document.View", "Document.Discuss"];
        return new Scenario(
            document.DocumentId, document.PeriodKey.Value, table1, table2.Code,
            Writer: await NewUserAsync("tcw", writer).ConfigureAwait(false),
            Bob: await NewUserAsync("tcb", writer).ConfigureAwait(false),
            Moderator: await NewUserAsync("tcm", [.. writer, "Document.DiscussModerate"]).ConfigureAwait(false),
            ViewerOnly: await NewUserAsync("tcv", ["Document.View"]).ConfigureAwait(false),
            Denied: await NewUserAsync("tcd", writer, ResourceKind.Sheet, sheet2.Id).ConfigureAwait(false),
            Stranger: await NewUserAsync("tcs", [], grant: false).ConfigureAwait(false),
            NovemberOnly: await NewUserAsync("tcn", writer, only: new PeriodKey(Nov)).ConfigureAwait(false),
            Manager: await NewUserAsync("tcg", [.. writer, "Document.Delete"], level: GrantLevel.Manage).ConfigureAwait(false));
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, int Period, string Table1, string Table2,
        Person Writer, Person Bob, Person Moderator, Person ViewerOnly, Person Denied, Person Stranger, Person NovemberOnly,
        Person Manager);
}
