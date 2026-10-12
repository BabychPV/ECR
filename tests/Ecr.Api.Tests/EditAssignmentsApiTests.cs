// tests/Ecr.Api.Tests/EditAssignmentsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Передача місяця виконавцям (EA-C) справжнім HTTP: потік «передати → заповнити → повернути →
/// валідувати», права, оракули, ідемпотентність, аудит.
/// </summary>
/// <remarks>
/// Автор документа в фікстурі — користувач <c>1</c>; керує менеджер проєкту (грант <c>Manage</c>), решта
/// ролей — або лише <c>Write</c> (не керує), або лише <c>Read</c> (виконавець).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-Assign-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Потік_передати_заповнити_повернути_заповнити_валідувати()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var exec = await SignedInAsync(app, s.Executor.UserName).ConfigureAwait(true);

        var created = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var dto = await JsonAsync(created, HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = dto.GetProperty("id").GetInt64();
        Assert.Equal("Assigned", dto.GetProperty("status").GetString());

        // «Мої задачі» виконавця.
        var mine = await JsonAsync(await exec.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var task = Assert.Single(mine.GetProperty("assignedToMe").EnumerateArray());
        Assert.Equal(id, task.GetProperty("assignmentId").GetInt64());

        // Заповнено.
        var filled = await JsonAsync(await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Filled", filled.GetProperty("status").GetString());
        var version = filled.GetProperty("version").GetString();

        // Власник бачить «чекає валідації».
        var awaiting = await JsonAsync(await owner.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Single(awaiting.GetProperty("awaitingMyValidation").EnumerateArray());

        // Повернути з причиною.
        var returned = await JsonAsync(
            await owner.PostAsJsonAsync(ActionUri(s, id, "return"), new { reason = "Fix totals", baseVersion = version }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Returned", returned.GetProperty("status").GetString());
        var recipient = Assert.Single(returned.GetProperty("recipients").EnumerateArray());
        Assert.Equal("Fix totals", recipient.GetProperty("returnReason").GetString());

        // Виконавець бачить причину повернення і заповнює знову.
        mine = await JsonAsync(await exec.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Fix totals", Assert.Single(mine.GetProperty("assignedToMe").EnumerateArray()).GetProperty("returnReason").GetString());

        filled = await JsonAsync(await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        version = filled.GetProperty("version").GetString();

        // Валідація.
        var validated = await JsonAsync(
            await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = version }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Validated", validated.GetProperty("status").GetString());

        // Повтор валідації — 200 без змін.
        var again = await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = "stale" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Аудит: передано, заповнено, повернуто, валідовано.
        var events = await EventTypesAsync(s).ConfigureAwait(true);
        Assert.Contains("EditAssignmentGranted", events);
        Assert.Contains("EditAssignmentFilled", events);
        Assert.Contains("EditAssignmentReturned", events);
        Assert.Contains("EditAssignmentValidated", events);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторне_заповнено_і_повторний_upsert_ідемпотентні()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var exec = await SignedInAsync(app, s.Executor.UserName).ConfigureAwait(true);

        var first = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = first.GetProperty("id").GetInt64();

        // Той самий склад і ознаки без baseVersion — не помилка й не нова передача.
        var second = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(id, second.GetProperty("id").GetInt64());
        Assert.Equal(first.GetProperty("version").GetString(), second.GetProperty("version").GetString());

        var f1 = await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true);
        var f2 = await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, f1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, f2.StatusCode);

        var filledEvents = (await EventTypesAsync(s).ConfigureAwait(true)).Count(e => e == "EditAssignmentFilled");
        Assert.Equal(1, filledEvents);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_наявної_без_baseVersion_422_а_зі_старою_409()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var first = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);

        var noVersion = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id, isExclusive: false)).ConfigureAwait(true);
        var p1 = await JsonAsync(noVersion, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentBaseVersion", p1.GetProperty("messageKey").GetString());

        var stale = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id, isExclusive: false, baseVersion: "00")).ConfigureAwait(true);
        var p2 = await JsonAsync(stale, HttpStatusCode.Conflict, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-DOC-0409.editAssignmentStale", p2.GetProperty("messageKey").GetString());

        var ok = await owner.PostAsJsonAsync(
            ListUri(s),
            CreateBody(s, s.Executor.Id, isExclusive: false, baseVersion: first.GetProperty("version").GetString())).ConfigureAwait(true);
        var updated = await JsonAsync(ok, HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.False(updated.GetProperty("isExclusive").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Керувати_може_лише_автор_чи_менеджер_а_невидимому_404()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, s.Stranger.UserName).ConfigureAwait(true);

        var forbidden = await writer.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var p = await JsonAsync(forbidden, HttpStatusCode.Forbidden, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-ACCS-0403.notEditAssignmentManager", p.GetProperty("messageKey").GetString());

        var listForbidden = await writer.GetAsync(ListUri(s)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, listForbidden.StatusCode);

        var hidden = await stranger.GetAsync(ListUri(s)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Одержувач_без_Read_чи_неіснуючий_дає_однакове_422()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var noRead = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Stranger.Id)).ConfigureAwait(true);
        var missing = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, 987_654_321)).ConfigureAwait(true);

        var a = await noRead.Content.ReadAsStringAsync().ConfigureAwait(true);
        var b = await missing.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRead.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(
            "err.ECR-REQ-0422.editAssignmentRecipientInvalid",
            JsonDocument.Parse(a).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(
            JsonDocument.Parse(a).RootElement.GetProperty("messageKey").GetString(),
            JsonDocument.Parse(b).RootElement.GetProperty("messageKey").GetString());

        // Нічого не створено.
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(list.GetProperty("assignments").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Fill_чужою_людиною_і_неіснуючої_передачі_дає_404()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var other = await SignedInAsync(app, s.OtherExecutor.UserName).ConfigureAwait(true);

        var created = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();

        var foreign = await other.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true);
        var missing = await other.PostAsync(ActionUri(s, id + 1000, "fill"), null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var foreignKey = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("messageKey").GetString();
        var missingKey = JsonDocument.Parse(await missing.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("messageKey").GetString();
        Assert.Equal(foreignKey, missingKey);

        // Чужа людина не бачить передачі і в «своїх» задачах.
        var mine = await JsonAsync(await other.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(mine.GetProperty("assignedToMe").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Передача_понад_власні_права_дає_422()
    {
        var s = await ArrangeAsync(denyColumnForManager: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var response = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var p = await JsonAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-REQ-0422.editAssignmentDelegationExceedsOwnRights",
            p.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Валідація_до_заповнення_422_повернення_без_причини_422()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("version").GetString();

        var early = await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = version }).ConfigureAwait(true);
        var p = await JsonAsync(early, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentNotAllFilled", p.GetProperty("messageKey").GetString());

        var noReason = await owner.PostAsJsonAsync(ActionUri(s, id, "return"), new { reason = "  ", baseVersion = version }).ConfigureAwait(true);
        var p2 = await JsonAsync(noReason, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentReasonRequired", p2.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зняття_передачі_і_пошук_кандидатів()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();

        var del = await owner.DeleteAsync(new Uri($"{ListUri(s)}/{id.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(list.GetProperty("assignments").EnumerateArray());
        Assert.Contains("EditAssignmentRevoked", await EventTypesAsync(s).ConfigureAwait(true));

        var tooShort = await owner.GetAsync(new Uri($"{ListUri(s)}/candidates?q=a", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);

        var found = await owner.GetAsync(new Uri($"{ListUri(s)}/candidates?q={Uri.EscapeDataString(s.Executor.UserName[..6])}", UriKind.Relative)).ConfigureAwait(true);
        var body = await found.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(s.Executor.Id.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Набір_застосовується_до_зрізів_копіюється_і_зміна_зі_старою_версією_409()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        // Чужий користувач — теж менеджер (S2: /me/editor-sets лише для Write у проєкті), але не власник набору.
        using var other = await SignedInAsync(app, s.Manager2.UserName).ConfigureAwait(true);
        var setsUri = new Uri("/api/v1/me/editor-sets", UriKind.Relative);

        var set = await JsonAsync(
            await owner.PostAsJsonAsync(setsUri, new { name = "Dept 5", memberUserIds = new[] { s.Executor.Id } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var setId = set.GetProperty("id").GetInt32();
        var setVersion = set.GetProperty("version").GetString();

        // Чужий користувач не бачить набір і не змінює його.
        var foreignList = await JsonAsync(await other.GetAsync(setsUri).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(foreignList.EnumerateArray());
        var foreignPut = await other.PutAsJsonAsync(
            new Uri($"/api/v1/me/editor-sets/{setId.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative),
            new { name = "X", memberUserIds = Array.Empty<int>(), baseVersion = setVersion }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);

        // Застосування до двох зрізів.
        var applied = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/apply-set", UriKind.Relative),
                new
                {
                    setId,
                    slices = new[]
                    {
                        new { sheetCode = s.SheetCode, periodKey = (int?)202601 },
                        new { sheetCode = s.SheetCode, periodKey = (int?)202602 },
                    },
                }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var items = applied.GetProperty("assignments").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("Dept 5", i.GetProperty("appliedSetName").GetString()));

        // Копіювання на третій зріз.
        var copied = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/copy", UriKind.Relative),
                new
                {
                    fromSlice = new { sheetCode = s.SheetCode, periodKey = (int?)202601 },
                    toSlices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202603 } },
                }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var copy = Assert.Single(copied.GetProperty("assignments").EnumerateArray());
        Assert.Equal(s.Executor.Id, Assert.Single(copy.GetProperty("recipients").EnumerateArray()).GetProperty("userId").GetInt32());

        // Зміна набору: зі свіжою версією — 200, зі старою — 409.
        var putUri = new Uri($"/api/v1/me/editor-sets/{setId.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative);
        var updated = await JsonAsync(
            await owner.PutAsJsonAsync(putUri, new { name = "Dept 5b", memberUserIds = new[] { s.Executor.Id }, baseVersion = setVersion }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Dept 5b", updated.GetProperty("name").GetString());

        var stale = await owner.PutAsJsonAsync(
            putUri, new { name = "Dept 5c", memberUserIds = new[] { s.Executor.Id }, baseVersion = setVersion }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Видалення набору лишає передачі.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(putUri).ConfigureAwait(true)).StatusCode);
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(3, list.GetProperty("assignments").GetArrayLength());
    }
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Картка_документа_віддає_canManageEditAccess_за_рішенням_сервера()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var card = new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative);

        async Task<bool?> CanManageAsync(string userName)
        {
            using var client = await SignedInAsync(app, userName).ConfigureAwait(true);
            var json = await JsonAsync(await client.GetAsync(card).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
            return json.TryGetProperty("canManageEditAccess", out var v) && v.ValueKind != JsonValueKind.Null
                ? v.GetBoolean()
                : null;
        }

        // Автор (лише Write) і Manage проєкту — так; Write не автор і Read — ні (клієнт не показує пункт, а не ловить 403).
        Assert.True(await CanManageAsync(s.Author.UserName).ConfigureAwait(true));
        Assert.True(await CanManageAsync(s.Manager.UserName).ConfigureAwait(true));
        Assert.False(await CanManageAsync(s.Writer.UserName).ConfigureAwait(true));
        Assert.False(await CanManageAsync(s.Executor.UserName).ConfigureAwait(true));

        // Автор документа (ownerUserId) віддається читачеві картки.
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        var body = await JsonAsync(await owner.GetAsync(card).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(s.Author.Id, body.GetProperty("ownerUserId").GetInt32());
    }
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Автор_документа_з_одним_Write_передає_і_знімає_передачу()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignedInAsync(app, s.Author.UserName).ConfigureAwait(true);

        var created = await JsonAsync(
            await author.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Assigned", created.GetProperty("status").GetString());

        var id = created.GetProperty("id").GetInt64();
        var del = await author.DeleteAsync(new Uri($"{ListUri(s)}/{id.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    // ───────────────────────── Аудит EA-C (P1/P2): приховані аркуші, IDOR, RequireManager, атомарність ─────────────────────────

    /// <summary>Те, що клієнт бачить у відмові: статус, код і ключ каталогу (traceId не порівнюється).</summary>
    private static async Task<string> ShapeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var json = JsonDocument.Parse(body).RootElement;
        string Prop(string name) => json.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;
        // title/detail входять у порівняння: відмінність тексту так само розкрила б різницю випадків.
        return $"{(int)response.StatusCode}|{Prop("errorCode")}|{Prop("messageKey")}|{Prop("documentId")}|{Prop("title")}|{Prop("detail")}";
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аркуш_схований_від_менеджера_дає_ту_саму_відповідь_що_й_відсутня_передача()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var hidden = await SignedInAsync(app, s.HiddenManager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(
            await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("version").GetString();
        var missing = id + 987_654;

        var del = await ShapeAsync(await hidden.DeleteAsync(new Uri($"{ListUri(s)}/{id}", UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
        var delMissing = await ShapeAsync(await hidden.DeleteAsync(new Uri($"{ListUri(s)}/{missing}", UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(delMissing, del);
        Assert.StartsWith("404|", del, StringComparison.Ordinal);

        var val = await ShapeAsync(await hidden.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = version }).ConfigureAwait(true)).ConfigureAwait(true);
        var valMissing = await ShapeAsync(await hidden.PostAsJsonAsync(ActionUri(s, missing, "validate"), new { baseVersion = version }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(valMissing, val);

        var ret = await ShapeAsync(await hidden.PostAsJsonAsync(ActionUri(s, id, "return"), new { reason = "x", baseVersion = version }).ConfigureAwait(true)).ConfigureAwait(true);
        var retMissing = await ShapeAsync(await hidden.PostAsJsonAsync(ActionUri(s, missing, "return"), new { reason = "x", baseVersion = version }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(retMissing, ret);

        // Copy: зріз-джерело схований ≡ зрізу-джерела немає.
        var copyHidden = await ShapeAsync(await hidden.PostAsJsonAsync(
            new Uri($"{ListUri(s)}/copy", UriKind.Relative),
            new
            {
                fromSlice = new { sheetCode = s.SheetCode },
                toSlices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202602 } },
            }).ConfigureAwait(true)).ConfigureAwait(true);
        var copyMissing = await ShapeAsync(await hidden.PostAsJsonAsync(
            new Uri($"{ListUri(s)}/copy", UriKind.Relative),
            new
            {
                fromSlice = new { sheetCode = s.SheetCode, periodKey = (int?)209912 },
                toSlices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202602 } },
            }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(copyMissing, copyHidden);

        // Перелік — без передачі схованого аркуша, і нічого не видалено.
        var list = await JsonAsync(await hidden.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(list.GetProperty("assignments").EnumerateArray());
        var still = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Single(still.GetProperty("assignments").EnumerateArray());

        // Передача зрізу зі схованим аркушем — 422 без назви аркуша.
        var denied = await hidden.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, denied.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Передача_іншого_документа_недосяжна_через_чужий_aid_а_відповідь_та_сама_що_й_для_відсутньої()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var exec = await SignedInAsync(app, s.Executor.UserName).ConfigureAwait(true);

        var created = await JsonAsync(
            await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("version").GetString();
        var other = new Uri($"/api/v1/documents/{s.OtherDocumentId.ToString(CultureInfo.InvariantCulture)}/edit-assignments", UriKind.Relative);

        async Task<(string Foreign, string Missing)> PairAsync(HttpClient client, string action, object? body)
        {
            var foreign = new Uri($"{other}/{id}/{action}", UriKind.Relative);
            var missing = new Uri($"{other}/{id + 987_654}/{action}", UriKind.Relative);
            var a = await client.PostAsJsonAsync(foreign, body).ConfigureAwait(false);
            var b = await client.PostAsJsonAsync(missing, body).ConfigureAwait(false);
            return (await ShapeAsync(a).ConfigureAwait(false), await ShapeAsync(b).ConfigureAwait(false));
        }

        var fill = await PairAsync(exec, "fill", new { }).ConfigureAwait(true);
        Assert.Equal(fill.Missing, fill.Foreign);
        var validate = await PairAsync(owner, "validate", new { baseVersion = version }).ConfigureAwait(true);
        Assert.Equal(validate.Missing, validate.Foreign);
        var ret = await PairAsync(owner, "return", new { reason = "x", baseVersion = version }).ConfigureAwait(true);
        Assert.Equal(ret.Missing, ret.Foreign);

        var delForeign = await ShapeAsync(await owner.DeleteAsync(new Uri($"{other}/{id}", UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
        var delMissing = await ShapeAsync(await owner.DeleteAsync(new Uri($"{other}/{id + 987_654}", UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(delMissing, delForeign);
        Assert.StartsWith("404|", delForeign, StringComparison.Ordinal);

        // Передача у своєму документі не зачеплена.
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Single(list.GetProperty("assignments").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кожна_дія_керування_вимагає_права_менеджера()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var writer = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);

        var created = await JsonAsync(
            await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();

        var responses = new List<(string Name, HttpResponseMessage Response)>
        {
            ("validate", await writer.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = "x" }).ConfigureAwait(true)),
            ("return", await writer.PostAsJsonAsync(ActionUri(s, id, "return"), new { reason = "x", baseVersion = "x" }).ConfigureAwait(true)),
            ("delete", await writer.DeleteAsync(new Uri($"{ListUri(s)}/{id}", UriKind.Relative)).ConfigureAwait(true)),
            ("candidates", await writer.GetAsync(new Uri($"{ListUri(s)}/candidates?q=ea", UriKind.Relative)).ConfigureAwait(true)),
            ("apply-set", await writer.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/apply-set", UriKind.Relative),
                new { setId = 1, slices = new[] { new { sheetCode = s.SheetCode } } }).ConfigureAwait(true)),
            ("copy", await writer.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/copy", UriKind.Relative),
                new { fromSlice = new { sheetCode = s.SheetCode }, toSlices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202602 } } }).ConfigureAwait(true)),
        };

        foreach (var (name, response) in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {response.StatusCode}");
            var key = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("messageKey").GetString();
            Assert.Equal("err.ECR-ACCS-0403.notEditAssignmentManager", key);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Причина_довша_за_500_дає_422_а_apply_set_з_хибним_зрізом_не_пише_нічого()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(
            await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();

        var tooLong = await owner.PostAsJsonAsync(
            ActionUri(s, id, "return"),
            new { reason = new string('x', 501), baseVersion = created.GetProperty("version").GetString() }).ConfigureAwait(true);
        var problem = await JsonAsync(tooLong, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentReasonTooLong", problem.GetProperty("messageKey").GetString());

        // apply-set: перший зріз коректний, другий — невідомий аркуш; після 422 нічого нового не створено.
        var set = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri("/api/v1/me/editor-sets", UriKind.Relative),
                new { name = "Atomic", memberUserIds = new[] { s.OtherExecutor.Id } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);

        var apply = await owner.PostAsJsonAsync(
            new Uri($"{ListUri(s)}/apply-set", UriKind.Relative),
            new
            {
                setId = set.GetProperty("id").GetInt32(),
                slices = new[]
                {
                    new { sheetCode = s.SheetCode, periodKey = (int?)202604 },
                    new { sheetCode = "NO_SUCH_SHEET", periodKey = (int?)202605 },
                },
            }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, apply.StatusCode);

        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Single(list.GetProperty("assignments").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Copy_передачі_застосованої_з_набору_іншого_менеджера_проходить()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var first = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var second = await SignedInAsync(app, s.Manager2.UserName).ConfigureAwait(true);

        var set = await JsonAsync(
            await first.PostAsJsonAsync(
                new Uri("/api/v1/me/editor-sets", UriKind.Relative),
                new { name = "Mine", memberUserIds = new[] { s.Executor.Id } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        await JsonAsync(
            await first.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/apply-set", UriKind.Relative),
                new { setId = set.GetProperty("id").GetInt32(), slices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202601 } } })
                .ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);

        // Другий менеджер НЕ власник набору — копія все одно проходить і зберігає знімок назви.
        var copied = await JsonAsync(
            await second.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/copy", UriKind.Relative),
                new
                {
                    fromSlice = new { sheetCode = s.SheetCode, periodKey = (int?)202601 },
                    toSlices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202602 } },
                }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var item = Assert.Single(copied.GetProperty("assignments").EnumerateArray());
        Assert.Equal("Mine", item.GetProperty("appliedSetName").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Набори_виконавців_лише_для_тих_хто_має_Write_а_кандидати_лише_з_Read()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Executor.UserName).ConfigureAwait(true);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var sets = new Uri("/api/v1/me/editor-sets", UriKind.Relative);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(sets).ConfigureAwait(true)).StatusCode);
        var create = await reader.PostAsJsonAsync(sets, new { name = "x", memberUserIds = new[] { s.Stranger.Id } }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        // Кандидати: користувач без доступу до проєкту (Stranger) у відповіді не з'являється.
        var found = await owner.GetAsync(
            new Uri($"{ListUri(s)}/candidates?q={Uri.EscapeDataString(s.Stranger.UserName[..6])}", UriKind.Relative)).ConfigureAwait(true);
        var body = await found.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.DoesNotContain(s.Stranger.Id.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_наявної_передачі_не_бере_блокування_документа_і_не_інвертується_із_записом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(
            await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("version").GetString();

        // «Запис комірки»: S на рядку передачі (REPEATABLEREAD) і лише потім U/X на документі (дотик).
        await using var writer = new SqlConnection(sql.ConnectionString);
        await writer.OpenAsync().ConfigureAwait(true);
        await using var tx = (SqlTransaction)await writer.BeginTransactionAsync().ConfigureAwait(true);
        await using (var cmd = writer.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT Id FROM doc.EditAssignment WITH (REPEATABLEREAD, ROWLOCK) WHERE Id = {id}";
            await cmd.ExecuteScalarAsync().ConfigureAwait(true);
        }

        // Зміна (isExclusive) іде в тлі: вона тримає U на передачі й чекає X, але НЕ тримає документ.
        var change = owner.PostAsJsonAsync(
            ListUri(s), CreateBody(s, s.Executor.Id, isExclusive: false, baseVersion: version));
        // Чекаємо не годинник, а факт очікування: зміна вже тримає U на передачі й чекає її X-конверсії
        // (у dm_tran_locks це CONVERT, а не WAIT) - тобто вона ПРОЙШЛА місце, де раніше брала документ.
        // Не вимагаємо черги: якщо запит устиг завершитися чи не став у чергу, інверсії все одно немає - тест
        // лише чекає ДО 10 с, щоб синхронізуватися з моментом, коли зміна тримає U на передачі.
        await WaitForLockWaitAsync(seconds: 10, required: false, stopWhen: change).ConfigureAwait(true);

        // «Дотик» документа записом не чекає: якби зміна тримала U на doc.Document — тут 1222/1205.
        await using (var touch = writer.CreateCommand())
        {
            touch.Transaction = tx;
            touch.CommandText = "SET LOCK_TIMEOUT 3000; UPDATE doc.Document SET ModifiedAt = ModifiedAt "
                + $"WHERE Id = {s.DocumentId}";
            await touch.ExecuteNonQueryAsync().ConfigureAwait(true);
        }

        await tx.CommitAsync().ConfigureAwait(true);

        var response = await change.ConfigureAwait(true);
        var updated = await JsonAsync(response, HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.False(updated.GetProperty("isExclusive").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_передачі_спершу_чекає_документ_і_не_тримає_діапазон_передачі_порядок_P2_1()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // «Видалення документа» тримає UPDLOCK на рядку документа ПЕРШИМ (так само бере DocumentDeletionStore).
        await using var holder = new SqlConnection(sql.ConnectionString);
        await holder.OpenAsync().ConfigureAwait(true);
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync().ConfigureAwait(true);
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT Id FROM doc.Document WITH (UPDLOCK, ROWLOCK) WHERE Id = {s.DocumentId}";
            await cmd.ExecuteScalarAsync().ConfigureAwait(true);
        }

        var create = owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id));
        await WaitForLockWaitAsync().ConfigureAwait(true);

        // ⛔ Створення стоїть у черзі за документом і НЕ тримає блокувань на передачах: якби воно спершу взяло
        // HOLDLOCK-діапазон передачі (UQ_EditAssignment), а потім чекало документ, видалення (документ → передача)
        // дало б цикл і 1205. Мутація «знову FindBySliceKeyForUpdate до LockDocument» червоніє тут.
        Assert.Equal(0, await GrantedLocksOnAssignmentsAsync().ConfigureAwait(true));

        await tx.RollbackAsync().ConfigureAwait(true);
        var response = await create.ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_передачі_віддає_нейтральне_409_а_не_текст_про_міграцію_шаблону()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        await using var holder = new SqlConnection(sql.ConnectionString);
        await holder.OpenAsync().ConfigureAwait(true);
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync().ConfigureAwait(true);
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT Id FROM doc.Document WITH (UPDLOCK, ROWLOCK) WHERE Id = {s.DocumentId}";
            await cmd.ExecuteScalarAsync().ConfigureAwait(true);
        }

        // Очікування обмежене (15 с): 409 ECR-DOC-4091 з ключем передачі, а не спільним lockTimeout.
        var response = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var problem = await JsonAsync(response, HttpStatusCode.Conflict, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-DOC-4091.editAssignmentBusy", problem.GetProperty("messageKey").GetString());

        await tx.RollbackAsync().ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Склад_набору_перевіряється_одним_правилом_без_оракула_існування()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        var sets = new Uri("/api/v1/me/editor-sets", UriKind.Relative);

        // Без жодного Read і неіснуючий — та сама відповідь побайтно.
        var ghostId = 987_654_321;
        var noRead = await ShapeAsync(await owner.PostAsJsonAsync(sets, new { name = "S1", memberUserIds = new[] { s.Stranger.Id } }).ConfigureAwait(true)).ConfigureAwait(true);
        var missing = await ShapeAsync(await owner.PostAsJsonAsync(sets, new { name = "S1", memberUserIds = new[] { ghostId } }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(missing, noRead);
        Assert.StartsWith("422|", noRead, StringComparison.Ordinal);

        // З Read — проходить, і ім'я з'являється лише тут.
        var ok = await JsonAsync(
            await owner.PostAsJsonAsync(sets, new { name = "S1", memberUserIds = new[] { s.Executor.Id } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(s.Executor.Id, Assert.Single(ok.GetProperty("members").EnumerateArray()).GetProperty("userId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_upsert_без_набору_ідемпотентний_і_мітку_набору_не_знімає()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var set = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri("/api/v1/me/editor-sets", UriKind.Relative),
                new { name = "Keep", memberUserIds = new[] { s.Executor.Id } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var applied = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/apply-set", UriKind.Relative),
                new { setId = set.GetProperty("id").GetInt32(), slices = new[] { new { sheetCode = s.SheetCode } } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var first = Assert.Single(applied.GetProperty("assignments").EnumerateArray());

        // Той самий склад і ознаки без набору й без baseVersion — 200 без змін, мітка набору лишається.
        var again = await JsonAsync(
            await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(first.GetProperty("version").GetString(), again.GetProperty("version").GetString());
        Assert.Equal("Keep", again.GetProperty("appliedSetName").GetString());
    }

    private async Task WaitForLockWaitAsync(int seconds = 15, bool required = true, Task? stopWhen = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (stopWhen is { IsCompleted: true })
            {
                return;
            }

            if (await ScalarAsync(
                    "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND request_status IN ('WAIT', 'CONVERT')")
                .ConfigureAwait(false) > 0)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        if (required)
        {
            Assert.Fail("Запит не став у чергу на блокування вчасно.");
        }
    }

    private Task<int> GrantedLocksOnAssignmentsAsync()
        => ScalarAsync(
            "SELECT COUNT(*) FROM sys.dm_tran_locks AS l JOIN sys.partitions AS p ON p.hobt_id = l.resource_associated_entity_id "
            + "WHERE l.resource_database_id = DB_ID() AND l.request_status = 'GRANT' AND l.request_session_id <> @@SPID "
            + "AND l.request_mode IN (N'U', N'X', N'RangeS-U', N'RangeX-X', N'RangeS-S', N'RangeI-N') "
            + "AND p.object_id = OBJECT_ID(N'doc.EditAssignment')");

    private async Task<int> ScalarAsync(string commandText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
    private static Uri ListUri(Scenario s)
        => new($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/edit-assignments", UriKind.Relative);

    private static Uri ActionUri(Scenario s, long id, string action)
        => new($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/edit-assignments/{id.ToString(CultureInfo.InvariantCulture)}/{action}", UriKind.Relative);

    private static Uri MeUri() => new("/api/v1/me/edit-assignments", UriKind.Relative);

    private static object CreateBody(Scenario s, int recipient, bool isExclusive = true, string? baseVersion = null)
        => new
        {
            slice = new { sheetCode = s.SheetCode },
            recipientUserIds = new[] { recipient },
            isExclusive,
            baseVersion,
        };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<List<string>> EventTypesAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var docMarker = $"\"documentId\":{s.DocumentId.ToString(CultureInfo.InvariantCulture)}";
        return await db.Database
            .SqlQuery<string>($"SELECT EventType AS Value FROM aud.SecurityEvent WHERE EventType LIKE N'EditAssignment%' AND DetailsJson LIKE {"%" + docMarker + "%"}")
            .ToListAsync().ConfigureAwait(false);
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

    private async Task<Scenario> ArrangeAsync(bool denyColumnForManager = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = builder.CreateContext();

        async Task<Person> NewUserAsync(string prefix, GrantLevel? level, bool withView, bool denyColumn = false, bool denySheet = false)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"EA_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Edit assignment test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (withView)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (level is { } l)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, l));
            }

            if (denyColumn)
            {
                db.ResourceGrants.Add(new ResourceGrant(
                    role.Id, ResourceKind.Column, document.ColumnDefIds[1], GrantLevel.None, isDeny: true));
            }

            if (denySheet)
            {
                db.ResourceGrants.Add(new ResourceGrant(
                    role.Id, ResourceKind.Sheet, document.SheetDefId, GrantLevel.None, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return new Person(user.Id, userName);
        }

        var manager = await NewUserAsync("eam", GrantLevel.Manage, withView: true, denyColumn: denyColumnForManager).ConfigureAwait(false);
        var writer = await NewUserAsync("eaw", GrantLevel.Write, withView: true).ConfigureAwait(false);
        var executor = await NewUserAsync("eae", GrantLevel.Read, withView: true).ConfigureAwait(false);
        var other = await NewUserAsync("eao", GrantLevel.Read, withView: true).ConfigureAwait(false);
        var stranger = await NewUserAsync("eas", null, withView: false).ConfigureAwait(false);

        // Автор документа — відомий користувач із відомим паролем (Write, не Manage): фікстура будує документ
        // від імені bootstrap-користувача, тож авторство переписується прямо в базі.
        var author = await NewUserAsync("eaa", GrantLevel.Write, withView: true).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {author.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        // Менеджер із забороною на аркуш (бачить документ, не бачить аркуш), другий менеджер і другий документ
        // того самого проєкту (IDOR між документами).
        var hidden = await NewUserAsync("eah", GrantLevel.Manage, withView: true, denySheet: true).ConfigureAwait(false);
        var manager2 = await NewUserAsync("eam2", GrantLevel.Manage, withView: true).ConfigureAwait(false);

        var otherDocument = new Document(document.ProjectId, $"OTH{Guid.NewGuid():N}"[..14], 1, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
        db.Documents.Add(otherDocument);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, document.SheetCode, manager, writer, executor, other, stranger, author,
            hidden, manager2, otherDocument.Id);
    }

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, string SheetCode, Person Manager, Person Writer, Person Executor, Person OtherExecutor,
        Person Stranger, Person Author, Person HiddenManager, Person Manager2, long OtherDocumentId);
}
