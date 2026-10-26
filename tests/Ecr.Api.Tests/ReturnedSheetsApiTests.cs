// tests/Ecr.Api.Tests/ReturnedSheetsApiTests.cs
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
/// ✎ D-449: «Повернуто на доопрацювання» (<c>GET /api/v1/me/returned-sheets</c>) справжнім HTTP на справжній базі —
/// справжні відхилення погоджувачем і повернення в роботу, надання учасника (D-443), останній подавач, заборони й
/// видимість аркуша.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (локально, не в коміті; тести лише в CI за рішенням людини — прогін за CI):
/// <list type="bullet">
/// <item>у <c>ListMyReturnedSheetsHandler</c> прибрати умову <c>row.OwnerUserId == userId</c> (будь-який видимий
/// документ проєкту) → червоніє <c>Власник_бачить_повернений_аркуш_з_причиною…</c> (колега-автор і погоджувач бачать
/// чужу задачу);</item>
/// <item>замість <c>DocumentParticipation.WorksOnSheet</c> пускати будь-яке надання → червоніє
/// <c>Учасник_з_рівнем_від_Write…</c> (учасник Read і учасник на іншому аркуші бачать задачу);</item>
/// <item>прибрати <c>RolesWriteSheet</c> для останнього подавача (пускати будь-якого подавача) → червоніє
/// <c>Останній_подавач_без_права_писати…</c>;</item>
/// <item>прибрати перевірку «повернув сам» → червоніє <c>Повернення_в_роботу…</c>;</item>
/// <item>у сховищі прибрати умову «остання подія — Reopen» (лише <c>ReopenedAt != null</c>) → червоніє
/// <c>Відкликання_автором…</c> (повернений колись, а потім відкликаний аркуш лишається в переліку);</item>
/// <item>прибрати перевірку <c>CanReadSheet</c> → червоніє <c>Схований_від_власника_аркуш…</c>;</item>
/// <item>прибрати <c>DeniesProject</c> → червоніє <c>Заборона_проєкту…</c>;</item>
/// <item>у сховищі брати <c>Status != Submitted</c> замість <c>== Rejected</c> → червоніє повторне подання/відкликання
/// (аркуш у <c>Draft</c> лишається в переліку).</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class ReturnedSheetsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Returned-Sheets-2026!";
    private const string Route = "/api/v1/me/returned-sheets";
    private const string Reason = "Рядок 3: сума не збігається з актами.\nПеревірте одиниці.";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Власник_бачить_повернений_аркуш_з_причиною_і_повторне_подання_його_прибирає()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var owner = await NewUserAsync(s.ProjectId, "rto", GrantLevel.Submit, withView: true).ConfigureAwait(true);
        var colleague = await NewUserAsync(s.ProjectId, "rtc", GrantLevel.Submit, withView: true).ConfigureAwait(true);
        await SetOwnerAsync(s, owner).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var ownerClient = await SignedInAsync(app, owner.UserName).ConfigureAwait(true);
        using var colleagueClient = await SignedInAsync(app, colleague.UserName).ConfigureAwait(true);
        using var managerClient = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var submitterClient = await SignedInAsync(app, s.Submitter.UserName).ConfigureAwait(true);

        // До повернення — порожньо; подання саме по собі задачею власника не є.
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(app, ownerClient).ConfigureAwait(true));

        await RejectAsync(app, managerClient, s, s.Sheet1, Reason).ConfigureAwait(true);

        var item = Assert.Single(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        Assert.Equal(s.DocumentId, item.GetProperty("documentId").GetInt64());
        Assert.Equal(s.DocumentKey, item.GetProperty("documentKey").GetString());
        Assert.Equal(s.Sheet1, item.GetProperty("sheetDefId").GetInt32());
        Assert.Equal(s.SheetCode1, item.GetProperty("sheetCode").GetString());
        Assert.Equal(s.PeriodKey, item.GetProperty("periodKey").GetInt32());
        Assert.Equal(s.Manager.UserName, item.GetProperty("returnedByDisplayName").GetString());
        Assert.Equal(Reason, item.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("projectCode").GetString()));
        Assert.Equal(JsonValueKind.Object, item.GetProperty("sheetNameL10n").ValueKind);
        Assert.True(item.GetProperty("returnedAt").GetDateTime() > DateTime.UtcNow.AddMinutes(-10));

        // Останній подавач (Submit на проєкт — право писати є) бачить ту саму задачу; одна картка, не дві.
        var submitted = Assert.Single(await ItemsAsync(app, submitterClient).ConfigureAwait(true));
        Assert.Equal(s.Sheet1, submitted.GetProperty("sheetDefId").GetInt32());

        // ⛔ Безпека: чужа задача не видна — ні колезі з тим самим правом на проєкт, ні погоджувачу (той, хто повернув).
        foreach (var client in new[] { colleagueClient, managerClient })
        {
            Assert.Empty(await ItemsAsync(app, client).ConfigureAwait(true));
        }

        // Повторне подання виводить аркуш із Rejected — задача зникає сама (живий запит) в усіх.
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        Assert.Empty(await ItemsAsync(app, submitterClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Учасник_з_рівнем_від_Write_бачить_повернений_аркуш_а_Read_і_надання_на_інший_аркуш_ні()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var writer = await NewUserAsync(s.ProjectId, "rtw", level: null, withView: false).ConfigureAwait(true);
        var submitter = await NewUserAsync(s.ProjectId, "rts", level: null, withView: false).ConfigureAwait(true);
        var reader = await NewUserAsync(s.ProjectId, "rtr", level: null, withView: false).ConfigureAwait(true);
        var otherSheet = await NewUserAsync(s.ProjectId, "rtx", level: null, withView: false).ConfigureAwait(true);
        var approver = await NewUserAsync(s.ProjectId, "rta", level: null, withView: false).ConfigureAwait(true);

        await GrantAsync(app, manager, s, null, null, writer, "Write").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode1, null, submitter, "Submit").ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, reader, "Read").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode2, null, otherSheet, "Submit").ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, approver, "Approve").ConfigureAwait(true);

        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        await RejectAsync(app, manager, s, s.Sheet1, Reason).ConfigureAwait(true);

        // Рівні впорядковані: Write, Submit і Approve (≥ Write) — задача.
        foreach (var person in new[] { writer, submitter, approver })
        {
            using var client = await SignedInAsync(app, person.UserName).ConfigureAwait(true);
            var item = Assert.Single(await ItemsAsync(app, client).ConfigureAwait(true));
            Assert.Equal(s.DocumentId, item.GetProperty("documentId").GetInt64());
            Assert.Equal(s.Sheet1, item.GetProperty("sheetDefId").GetInt32());
        }

        // Read — лише перегляд; надання на аркуш 2 повернення аркуша 1 не стосується
        // (і аркуш 1 від нього схований — ні назви, ні причини).
        foreach (var person in new[] { reader, otherSheet })
        {
            using var client = await SignedInAsync(app, person.UserName).ConfigureAwait(true);
            Assert.Empty(await ItemsAsync(app, client).ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Схований_від_власника_аркуш_не_розкривається()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        // Власник із правом на проєкт, але єдина таблиця аркуша 1 закрита забороною: аркуш 1 для нього не існує.
        var owner = await NewUserAsync(s.ProjectId, "rth", GrantLevel.Submit, withView: true, denyTableDefId: s.TableDefId1)
            .ConfigureAwait(true);
        await SetOwnerAsync(s, owner).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var ownerClient = await SignedInAsync(app, owner.UserName).ConfigureAwait(true);

        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        await RejectAsync(app, manager, s, s.Sheet1, "Hidden reason").ConfigureAwait(true);

        var hidden = await PageAsync(app, ownerClient, string.Empty).ConfigureAwait(true);
        Assert.Equal(0, hidden.GetProperty("items").GetArrayLength());
        Assert.True(IsNullOrMissing(hidden, "nextCursor"));
        Assert.DoesNotContain("Hidden reason", hidden.GetRawText(), StringComparison.Ordinal);

        // Контроль: повернення ВИДИМОГО аркуша 2 того самого документа власник бачить.
        await SubmitAsync(app, s, s.Sheet2).ConfigureAwait(true);
        await RejectAsync(app, manager, s, s.Sheet2, Reason).ConfigureAwait(true);
        var visible = Assert.Single(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        Assert.Equal(s.Sheet2, visible.GetProperty("sheetDefId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Заборона_проєкту_перемагає_надання_учасника()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var allowed = await NewUserAsync(s.ProjectId, "rtk", level: null, withView: false).ConfigureAwait(true);
        var denied = await NewUserAsync(s.ProjectId, "rtd", level: null, withView: false, denyProject: true).ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, [allowed, denied], "Write").ConfigureAwait(true);

        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        await RejectAsync(app, manager, s, s.Sheet1, Reason).ConfigureAwait(true);

        using (var allowedClient = await SignedInAsync(app, allowed.UserName).ConfigureAwait(true))
        {
            Assert.Single(await ItemsAsync(app, allowedClient).ConfigureAwait(true));
        }

        using var deniedClient = await SignedInAsync(app, denied.UserName).ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(app, deniedClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Відкликання_автором_не_задача_навіть_після_давнього_повернення_в_роботу()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var owner = await NewUserAsync(s.ProjectId, "rtv", GrantLevel.Submit, withView: true).ConfigureAwait(true);
        var reopener = await NewUserAsync(
            s.ProjectId, "rtq", GrantLevel.Manage, withView: true, permissions: ["Document.Reopen"]).ConfigureAwait(true);
        await SetOwnerAsync(s, owner).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var ownerClient = await SignedInAsync(app, owner.UserName).ConfigureAwait(true);
        using var submitterClient = await SignedInAsync(app, s.Submitter.UserName).ConfigureAwait(true);
        using var reopenerClient = await SignedInAsync(app, reopener.UserName).ConfigureAwait(true);

        // Колись аркуш повертали в роботу (ReopenedAt на стані лишається), потім подали знову…
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        await ReopenAsync(app, reopenerClient, s, s.Sheet1, "Давнє повернення").ConfigureAwait(true);
        Assert.Single(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(app, ownerClient).ConfigureAwait(true));

        // …а тепер автор відкликає (BE-31): Draft, але остання подія — Recall, не Reopen. У блоці нічого немає.
        using (var recalled = await submitterClient.PostAsJsonAsync(
                   new Uri($"/api/v1/documents/{Num(s.DocumentId)}/recall", UriKind.Relative),
                   new { sheetDefId = s.Sheet1, periodKey = s.PeriodKey, reason = "Забув рядок" }).ConfigureAwait(true))
        {
            Assert.True(recalled.StatusCode == HttpStatusCode.NoContent, $"{recalled.StatusCode}: {app.ErrorsText}");
        }

        Assert.Empty(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        Assert.Empty(await ItemsAsync(app, submitterClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Повернення_в_роботу_задача_з_причиною_й_автором_повернення_крім_того_хто_повернув_сам()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        // Власник сам має право повертати в роботу (Manage + Document.Reopen); подає інший — автор подань сценарію.
        var owner = await NewUserAsync(
            s.ProjectId, "rtn", GrantLevel.Manage, withView: true, permissions: ["Document.Reopen"]).ConfigureAwait(true);
        var reopener = await NewUserAsync(
            s.ProjectId, "rtg", GrantLevel.Manage, withView: true, permissions: ["Document.Reopen"]).ConfigureAwait(true);
        await SetOwnerAsync(s, owner).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var ownerClient = await SignedInAsync(app, owner.UserName).ConfigureAwait(true);
        using var reopenerClient = await SignedInAsync(app, reopener.UserName).ConfigureAwait(true);
        using var submitterClient = await SignedInAsync(app, s.Submitter.UserName).ConfigureAwait(true);

        // Повернув інший: задача власника й останнього подавача, з причиною й іменем із повернення в роботу.
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        await ReopenAsync(app, reopenerClient, s, s.Sheet1, Reason).ConfigureAwait(true);
        foreach (var client in new[] { ownerClient, submitterClient })
        {
            var item = Assert.Single(await ItemsAsync(app, client).ConfigureAwait(true));
            Assert.Equal(s.Sheet1, item.GetProperty("sheetDefId").GetInt32());
            Assert.Equal(Reason, item.GetProperty("reason").GetString());
            Assert.Equal(reopener.UserName, item.GetProperty("returnedByDisplayName").GetString());
        }

        Assert.Empty(await ItemsAsync(app, reopenerClient).ConfigureAwait(true));

        // Повторне подання — зникло в усіх.
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        Assert.Empty(await ItemsAsync(app, submitterClient).ConfigureAwait(true));

        // Повернув сам власник: для нього не задача, для останнього подавача — задача.
        await ReopenAsync(app, ownerClient, s, s.Sheet1, "Сам виправлю").ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(app, ownerClient).ConfigureAwait(true));
        var own = Assert.Single(await ItemsAsync(app, submitterClient).ConfigureAwait(true));
        Assert.Equal(owner.UserName, own.GetProperty("returnedByDisplayName").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Останній_подавач_без_права_писати_задачі_не_має()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        // Подавач — роль Submit на проєкт; після повернення роль знижено до Read (право писати втрачено, документ видно).
        var submitter = await NewUserAsync(s.ProjectId, "rtl", GrantLevel.Submit, withView: true).ConfigureAwait(true);
        using (var app = new EcrApiFactory(sql))
        {
            using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
            using var submitterClient = await SignedInAsync(app, submitter.UserName).ConfigureAwait(true);
            using (var submitted = await submitterClient.PostAsJsonAsync(
                       new Uri($"/api/v1/documents/{Num(s.DocumentId)}/submit", UriKind.Relative),
                       new { sheetDefId = s.Sheet1, periodKey = s.PeriodKey }).ConfigureAwait(true))
            {
                Assert.True(submitted.StatusCode == HttpStatusCode.NoContent, $"{submitted.StatusCode}: {app.ErrorsText}");
            }

            await RejectAsync(app, manager, s, s.Sheet1, Reason).ConfigureAwait(true);

            // Контроль: з правом писати — задача є.
            Assert.Single(await ItemsAsync(app, submitterClient).ConfigureAwait(true));
        }

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.ResourceGrant SET Level = {(byte)GrantLevel.Read} WHERE RoleId = {submitter.RoleId}").ConfigureAwait(true);
        }

        // Новий застосунок — без кешу профілю: подавач бачить документ (Read), але писати не може — задачі немає.
        using var fresh = new EcrApiFactory(sql);
        using var readerClient = await SignedInAsync(fresh, submitter.UserName).ConfigureAwait(true);
        Assert.Empty(await ItemsAsync(fresh, readerClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-449")]
    public async Task Зіпсований_курсор_422_і_анонім_401()
    {
        using var app = new EcrApiFactory(sql);
        using (var anonymous = app.CreateClient())
        using (var response = await anonymous.GetAsync(new Uri(Route, UriKind.Relative)).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var s = await ArrangeAsync().ConfigureAwait(true);
        using var client = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var bad = await client.GetAsync(new Uri($"{Route}?cursor=not-a-cursor", UriKind.Relative)).ConfigureAwait(true);
        var body = await bad.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(bad.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)bad.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(
            "err.ECR-REQ-0422.returnedSheetsCursorInvalid",
            JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// P3 (PM-EDITASSIGN-P3, закритий період): перевірка ролі кроку в <c>CanReopen</c> не відкриває закритий період -
    /// повернення затвердженого аркуша в закритому періоді відмовляє <c>ECR-PRD-4223 reopenPeriodFirst</c>
    /// (<c>ClosedPeriodGuard</c> діє незалежно від права на дію).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-CLOSED-PERIOD")]
    public async Task Повернення_затвердженого_аркуша_у_закритому_періоді_відмовляє_reopenPeriodFirst()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var reopener = await NewUserAsync(
            s.ProjectId, "rtr", GrantLevel.Manage, withView: true, permissions: ["Document.Reopen"]).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var managerClient = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var reopenerClient = await SignedInAsync(app, reopener.UserName).ConfigureAwait(true);

        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);
        using (var approve = await managerClient.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/approve", UriKind.Relative),
            new { sheetDefId = s.Sheet1, periodKey = s.PeriodKey, approved = true }).ConfigureAwait(true))
        {
            Assert.True(approve.StatusCode == HttpStatusCode.NoContent, $"{approve.StatusCode}\n{app.ErrorsText}");
        }

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.Period SET State = {(int)PeriodState.Closed} WHERE ProjectId = {s.ProjectId} AND PeriodKey = {s.PeriodKey}")
                .ConfigureAwait(true);
        }

        using var response = await reopenerClient.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/reopen", UriKind.Relative),
            new { sheetDefId = s.Sheet1, periodKey = s.PeriodKey, reason = "Закритий період" }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-PRD-4223.reopenPeriodFirst", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
    }
    private static bool IsNullOrMissing(JsonElement element, string name)
        => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static Task GrantAsync(
        EcrApiFactory app, HttpClient manager, Scenario s, string? sheet, string? table, Person recipient, string level)
        => GrantAsync(app, manager, s, sheet, table, [recipient], level);

    private static async Task GrantAsync(
        EcrApiFactory app, HttpClient manager, Scenario s, string? sheet, string? table, Person[] recipients, string level)
    {
        using var response = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/edit-assignments", UriKind.Relative),
            new
            {
                slice = new { sheetCode = sheet, tableCode = table },
                recipientUserIds = recipients.Select(r => r.Id).ToArray(),
                isExclusive = false,
                level,
            }).ConfigureAwait(false);
        await JsonAsync(response, HttpStatusCode.OK, app).ConfigureAwait(false);
    }

    private static async Task SubmitAsync(EcrApiFactory app, Scenario s, int sheetDefId)
    {
        using var submitter = await SignedInAsync(app, s.Submitter.UserName).ConfigureAwait(false);
        using var response = await submitter.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/submit", UriKind.Relative),
            new { sheetDefId, periodKey = s.PeriodKey }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
    }

    private static async Task RejectAsync(EcrApiFactory app, HttpClient approver, Scenario s, int sheetDefId, string reason)
    {
        using var response = await approver.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/approve", UriKind.Relative),
            new { sheetDefId, periodKey = s.PeriodKey, approved = false, reason }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
    }

    private static async Task ReopenAsync(EcrApiFactory app, HttpClient reopener, Scenario s, int sheetDefId, string reason)
    {
        using var response = await reopener.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/reopen", UriKind.Relative),
            new { sheetDefId, periodKey = s.PeriodKey, reason }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
    }

    private static async Task<List<JsonElement>> ItemsAsync(EcrApiFactory app, HttpClient client)
    {
        var page = await PageAsync(app, client, string.Empty).ConfigureAwait(false);
        return [.. page.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<JsonElement> PageAsync(EcrApiFactory app, HttpClient client, string query)
        => await JsonAsync(
                await client.GetAsync(new Uri(Route + query, UriKind.Relative)).ConfigureAwait(false), HttpStatusCode.OK, app)
            .ConfigureAwait(false);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
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

    private async Task SetOwnerAsync(Scenario s, Person owner)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {owner.Id} WHERE Id = {s.DocumentId}").ConfigureAwait(false);
    }

    private async Task<Person> NewUserAsync(
        int projectId,
        string prefix,
        GrantLevel? level,
        bool withView,
        int? denyTableDefId = null,
        bool denyProject = false,
        string[]? permissions = null)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DR_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Returned sheets test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withView)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        }

        foreach (var permission in permissions ?? [])
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (level is { } l)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, l));
        }

        if (denyTableDefId is { } table)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Table, table, GrantLevel.None, isDeny: true));
        }

        if (denyProject)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.None, isDeny: true));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Person(user.Id, userName, role.Id);
    }

    /// <summary>
    /// Документ із ДВОМА аркушами у складі (по таблиці й екземпляру за відкритий період), менеджер (Manage; погоджує й
    /// видає надання, він же власник за замовчуванням) і автор подань (Submit) — як у <c>DocumentParticipantsReachApiTests</c>.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(document).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create($"SHT{tag}"), Text("Sheet T"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table2 = new TableDef(sheet2.Id, EcrCode.Create($"TBT{tag}"), Text("Table T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ColumnDefs.Add(new ColumnDef(table2.Id, EcrCode.Create($"CT{tag}"), Text("Col T"), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table2.Id, RowKey.Create($"RT{tag}"), 1, Text("Row T"), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2 = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var row2 = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(document.PeriodKey, instance2, document.DocumentId, table2.Id, now));
        db.TableRows.Add(new TableRow(document.PeriodKey, row2, instance2, RowKey.Create($"RT{tag}"), 1, now));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheet2.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var documentKey = await db.Documents.Where(d => d.Id == document.DocumentId).Select(d => d.BusinessKey).SingleAsync().ConfigureAwait(false);

        var manager = await NewUserAsync(document.ProjectId, "rtm", GrantLevel.Manage, withView: true).ConfigureAwait(false);
        var submitter = await NewUserAsync(document.ProjectId, "rtp", GrantLevel.Submit, withView: true).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {manager.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, documentKey, document.ProjectId, document.PeriodKey.Value,
            document.SheetDefId, sheet2.Id, document.SheetCode, sheet2.Code, document.TableDefId,
            manager, submitter);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName, int RoleId);

    private sealed record Scenario(
        long DocumentId, string DocumentKey, int ProjectId, int PeriodKey,
        int Sheet1, int Sheet2, string SheetCode1, string SheetCode2, int TableDefId1,
        Person Manager, Person Submitter);
}
