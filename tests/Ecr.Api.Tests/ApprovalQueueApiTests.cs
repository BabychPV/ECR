// tests/Ecr.Api.Tests/ApprovalQueueApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-12.5: черга «До погодження» (<c>GET /api/v1/me/approval-queue</c>) справжнім HTTP на справжній базі:
/// справжні <c>AccessDecisionService</c>, маршрут погодження, права, видимість аркуша.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (локально, не в коміті): (1) перевірку <c>EditRules.CanApprove</c> в обробнику вимкнено —
/// «без права/чужий крок» і «маршрут» червоні; (2) вимкнено <c>CanReadSheet</c> — «усі таблиці аркуша сховані»
/// червоний; (3) повернуто фільтр власних подань F-25 (скасований D-440) — «власне подання погоджувача» червоний; (4) вимкнено фільтр
/// <c>SeesDocumentsOf</c> — тести лишаються ЗЕЛЕНИМИ: його випадок («проєкт не видимий») закриває й
/// <c>CanApprove</c> (немає гранта → <c>None</c>), фільтр — попередній відбір кандидатів, а не межа безпеки.
/// </remarks>
[Collection("SqlServer")]
public sealed class ApprovalQueueApiTests(SqlServerFixture sql)
{
    private const string Password = "Approval-Queue-1205!";
    private const string Route = "/api/v1/me/approval-queue";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Поданий_аркуш_бачить_лише_той_хто_може_його_затвердити()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var writer = await NewUserAsync(b, GrantLevel.Write).ConfigureAwait(true);
        var reader = await NewUserAsync(b, GrantLevel.Read).ConfigureAwait(true);
        var noGrant = await NewUserAsync(b, level: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver.Name).ConfigureAwait(true);
        using var writerClient = await SignInAsync(app, writer.Name).ConfigureAwait(true);
        using var readerClient = await SignInAsync(app, reader.Name).ConfigureAwait(true);
        using var strangerClient = await SignInAsync(app, noGrant.Name).ConfigureAwait(true);

        // До подання — порожньо в усіх.
        Assert.Empty(await ItemsAsync(app, approverClient).ConfigureAwait(true));

        using (var submitted = await SubmitAsync(submitterClient, b).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, submitted.StatusCode);
        }

        // Погоджувач бачить рівно цей аркуш з повним набором полів контракту.
        var item = Assert.Single(await ItemsAsync(app, approverClient).ConfigureAwait(true));
        Assert.Equal(b.DocumentId, item.GetProperty("documentId").GetInt64());
        Assert.Equal(b.SheetDefId, item.GetProperty("sheetDefId").GetInt32());
        Assert.Equal(b.SheetCode, item.GetProperty("sheetCode").GetString());
        Assert.Equal(b.PeriodKey.Value, item.GetProperty("periodKey").GetInt32());
        Assert.Equal(submitter.Name, item.GetProperty("submittedByDisplayName").GetString());
        Assert.True(IsNullOrMissing(item, "stepOrdinal"));
        Assert.True(IsNullOrMissing(item, "stepCount"));
        Assert.True(IsNullOrMissing(item, "stepRoleCode"));
        Assert.False(string.IsNullOrEmpty(item.GetProperty("projectCode").GetString()));
        Assert.False(string.IsNullOrEmpty(item.GetProperty("templateCode").GetString()));
        Assert.False(string.IsNullOrEmpty(item.GetProperty("documentKey").GetString()));
        Assert.True(item.GetProperty("submittedAt").GetDateTime() > DateTime.UtcNow.AddMinutes(-10));

        // Без рівня Approve (автор подання тут має лише Submit), без гранта — порожньо; жодних слідів схованого.
        foreach (var client in new[] { writerClient, readerClient, strangerClient, submitterClient })
        {
            Assert.Empty(await ItemsAsync(app, client).ConfigureAwait(true));
        }

        // Після рішення аркуш зникає з черги.
        using (var approved = await ApproveAsync(approverClient, b, approved: true).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        }

        Assert.Empty(await ItemsAsync(app, approverClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task D440_власне_подання_погоджувача_в_його_черзі_і_він_сам_його_затверджує()
    {
        // D-440: правило чотирьох очей скасоване — автор із рівнем Approve бачить власне подання в черзі
        // (як і колега) і затверджує його сам. Мутація: повернути фільтр `row.SubmittedByUserId == userId`
        // у `ListMyApprovalQueueHandler` — черга автора порожня, тест червоний.
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var author = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var colleague = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var authorClient = await SignInAsync(app, author.Name).ConfigureAwait(true);
        using var colleagueClient = await SignInAsync(app, colleague.Name).ConfigureAwait(true);

        using (var submitted = await SubmitAsync(authorClient, b).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, submitted.StatusCode);
        }

        var own = Assert.Single(await ItemsAsync(app, authorClient).ConfigureAwait(true));
        Assert.Equal(author.Name, own.GetProperty("submittedByDisplayName").GetString());
        Assert.Single(await ItemsAsync(app, colleagueClient).ConfigureAwait(true));

        using (var approved = await ApproveAsync(authorClient, b, approved: true).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        }

        Assert.Empty(await ItemsAsync(app, authorClient).ConfigureAwait(true));
        Assert.Empty(await ItemsAsync(app, colleagueClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Відхилений_аркуш_не_чекає_погодження()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var approver = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver.Name).ConfigureAwait(true);

        (await SubmitAsync(submitterClient, b).ConfigureAwait(true)).Dispose();
        Assert.Single(await ItemsAsync(app, approverClient).ConfigureAwait(true));

        using (var rejected = await ApproveAsync(approverClient, b, approved: false, reason: "Fix totals").ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);
        }

        Assert.Empty(await ItemsAsync(app, approverClient).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Маршрут_черга_належить_лише_ролі_поточного_кроку()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var second = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        await RouteAsync(b, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);

        (await SubmitAsync(submitterClient, b).ConfigureAwait(true)).Dispose();

        // Крок 1: бачить лише перший погоджувач, з номером кроку й кодом ролі.
        var step1 = Assert.Single(await ItemsAsync(app, firstClient).ConfigureAwait(true));
        Assert.Equal(1, step1.GetProperty("stepOrdinal").GetInt32());
        Assert.Equal(2, step1.GetProperty("stepCount").GetInt32());
        Assert.Equal(await RoleCodeAsync(first.RoleId).ConfigureAwait(true), step1.GetProperty("stepRoleCode").GetString());
        Assert.Empty(await ItemsAsync(app, secondClient).ConfigureAwait(true));

        // Підпис першого: черга переходить до другого, перший її більше не бачить.
        using (var signed = await ApproveAsync(firstClient, b, approved: true).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, signed.StatusCode);
        }

        Assert.Empty(await ItemsAsync(app, firstClient).ConfigureAwait(true));
        var step2 = Assert.Single(await ItemsAsync(app, secondClient).ConfigureAwait(true));
        Assert.Equal(2, step2.GetProperty("stepOrdinal").GetInt32());
        Assert.Equal(await RoleCodeAsync(second.RoleId).ConfigureAwait(true), step2.GetProperty("stepRoleCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Аркуш_усі_таблиці_якого_сховані_і_чужий_проєкт_не_розкриваються()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var other = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var otherSubmitter = await NewUserAsync(other, GrantLevel.Submit).ConfigureAwait(true);
        var open = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);

        // Approve на проєкт, але єдина таблиця аркуша закрита забороною: аркуш для нього не існує
        // (`RequireSheetVisible` у затвердженні дав би 404), хоч рівень на аркуш — Approve.
        var hidden = await NewUserAsync(b, GrantLevel.Approve, denyTableDefId: b.TableDefId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var otherSubmitterClient = await SignInAsync(app, otherSubmitter.Name).ConfigureAwait(true);
        using var openClient = await SignInAsync(app, open.Name).ConfigureAwait(true);
        using var hiddenClient = await SignInAsync(app, hidden.Name).ConfigureAwait(true);

        (await SubmitAsync(submitterClient, b).ConfigureAwait(true)).Dispose();
        (await SubmitAsync(otherSubmitterClient, other).ConfigureAwait(true)).Dispose();

        // Контроль: без заборони аркуш видно (і лише свого проєкту, не чужого).
        var visible = Assert.Single(await ItemsAsync(app, openClient).ConfigureAwait(true));
        Assert.Equal(b.DocumentId, visible.GetProperty("documentId").GetInt64());

        // Схований аркуш — порожня відповідь, як у користувача без жодного аркуша.
        var hiddenPage = await PageAsync(app, hiddenClient, string.Empty).ConfigureAwait(true);
        Assert.Equal(0, hiddenPage.GetProperty("items").GetArrayLength());
        Assert.True(IsNullOrMissing(hiddenPage, "nextCursor"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Сторінки_за_курсором_від_найдавнішого_і_межі_ліміту()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var approver = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);

        // Три документи проєкту, подані з відомими моментами (від найдавнішого): фікстура задає порядок сама.
        var ids = new List<long>();
        await using (var db = Context())
        {
            var submitterId = await db.Users.Where(u => u.UserName == submitter.Name).Select(u => u.Id).SingleAsync().ConfigureAwait(true);
            var baseTime = DateTime.UtcNow.AddHours(-3);
            for (var i = 0; i < 3; i++)
            {
                var document = new Document(b.ProjectId, $"Q{Guid.NewGuid():N}"[..14], submitterId, baseTime);
                db.Documents.Add(document);
                await db.SaveChangesAsync().ConfigureAwait(true);
                db.DocumentSheets.Add(new DocumentSheet(document.Id, b.SheetDefId));

                // Подано в порядку, ЗВОРОТНОМУ створенню: порядок черги — за давністю подання, не за id.
                var state = new ApprovalState(document.Id, b.SheetDefId, b.PeriodKey.Value);
                state.Submit(submitterId, baseTime.AddMinutes(10 - i * 3));
                db.ApprovalStates.Add(state);
                await db.SaveChangesAsync().ConfigureAwait(true);
                ids.Add(document.Id);
            }
        }

        var expected = ids.AsEnumerable().Reverse().ToList();

        using var app = new EcrApiFactory(sql);
        using var approverClient = await SignInAsync(app, approver.Name).ConfigureAwait(true);

        var page1 = await PageAsync(app, approverClient, "?limit=2").ConfigureAwait(true);
        var items1 = page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetInt64()).ToList();
        Assert.Equal(expected.Take(2), items1);
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var page2 = await PageAsync(app, approverClient, $"?limit=2&cursor={Uri.EscapeDataString(cursor!)}").ConfigureAwait(true);
        var items2 = page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("documentId").GetInt64()).ToList();
        Assert.Equal(expected.Skip(2), items2);
        Assert.True(IsNullOrMissing(page2, "nextCursor"));

        // Ліміт стискається: 0 → 1 запис, понад 200 → не більше 200 (тут — усі три).
        var tiny = await PageAsync(app, approverClient, "?limit=0").ConfigureAwait(true);
        Assert.Single(tiny.GetProperty("items").EnumerateArray());
        var huge = await PageAsync(app, approverClient, "?limit=100000").ConfigureAwait(true);
        Assert.Equal(3, huge.GetProperty("items").GetArrayLength());

        // Зіпсований курсор — 422 з ключем каталогу, без 5xx.
        using var bad = await approverClient.GetAsync(new Uri($"{Route}?cursor=not-a-cursor", UriKind.Relative)).ConfigureAwait(true);
        var body = await bad.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(bad.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)bad.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(
            "err.ECR-REQ-0422.approvalQueueCursorInvalid",
            JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Анонім_не_бачить_черги()
    {
        using var app = new EcrApiFactory(sql);
        using var anonymous = app.CreateClient();
        using var response = await anonymous.GetAsync(new Uri(Route, UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static async Task<List<JsonElement>> ItemsAsync(EcrApiFactory app, HttpClient client)
    {
        var page = await PageAsync(app, client, string.Empty).ConfigureAwait(false);
        return [.. page.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<JsonElement> PageAsync(EcrApiFactory app, HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri(Route + query, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static bool IsNullOrMissing(JsonElement element, string name)
        => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value });

    private static Task<HttpResponseMessage> ApproveAsync(
        HttpClient client, TestDocument b, bool approved, string? reason = null)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value, approved, reason });

    private async Task<TestDocument> ArrangeDocumentAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync().ConfigureAwait(false);

        // D-441 (AN-128): Scheduled-період не подається — період документа відкрито.
        await builder.OpenPeriodAsync(b).ConfigureAwait(false);

        await using var db = Context();
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return b;
    }

    /// <summary>Маршрут ПРОЄКТУ документа з кроками за ролями в заданому порядку.</summary>
    private async Task RouteAsync(TestDocument b, params int[] roleIds)
    {
        await using var db = Context();
        var route = new ApprovalRoute(
            EcrCode.Create($"Q125_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FV-12.5 route" }),
            b.ProjectId);
        foreach (var roleId in roleIds)
        {
            route.AddStep(roleId);
        }

        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Користувач із власною роллю; грант <paramref name="level"/> на проєкт (або без гранта).</summary>
    private async Task<(string Name, int RoleId)> NewUserAsync(
        TestDocument b, GrantLevel? level, int? denyTableDefId = null)
    {
        var name = $"q125_{Guid.NewGuid():N}"[..20];

        await using var db = Context();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FV-12.5 approver" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        if (level is { } l)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, l));
        }

        if (denyTableDefId is { } table)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Table, table, GrantLevel.None, isDeny: true));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (name, role.Id);
    }

    private async Task<string> RoleCodeAsync(int roleId)
    {
        await using var db = Context();

        return await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.Code).SingleAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
