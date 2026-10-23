// tests/Ecr.Api.Tests/DocumentParticipantsReachApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// D-444 п.3 (AN-131) наскрізно через HTTP: учасник документа (надання D-443) з'являється в черзі «До погодження»,
/// у зведенні переліку документів і в пошуку палітри — рівно відповідно до свого надання.
/// </summary>
/// <remarks>
/// ⛔ Черга — та сама відповідь, що й затвердження: для кожного учасника «є в черзі» ⇔ «POST approve пройшов»
/// (D-444 п.2: з маршрутом потрібна ще й роль поточного кроку; без маршруту вистачає надання <c>Approve</c>).
/// <para>Мутаційний доказ (локально, не в коміті):</para>
/// <list type="bullet">
/// <item>у <c>ListMyApprovalQueueHandler</c> не передавати <c>participatedIds</c> у сховище (лише проєкти) → червоніють
/// обидва тести черги (учасника з <c>Approve</c> немає в черзі, хоча затвердження проходить);</item>
/// <item>у <c>ListMyApprovalQueueHandler.Evaluate</c> прибрати <c>ParticipationLevel</c> → те саме;</item>
/// <item>у <c>Evaluate</c> замість <c>LevelAt</c> брати найвищий рівень усіх надань документа (без області) → червоніє
/// перший тест (учасник з <c>Approve</c> на таблицю з'являється в черзі, а затвердити не може);</item>
/// <item>у <c>EditRules.CanApprove</c> прибрати перевірку ролі кроку → червоніє тест маршруту (і черга, і затвердження);</item>
/// <item>у <c>DocumentListSummaryStore</c> прибрати гілку <c>participantDocsJson</c> → червоніє тест зведення (лічильники 0);</item>
/// <item>прибрати <c>NOT EXISTS … participantHiddenJson</c> → той самий тест (відхилений схований аркуш робить документ
/// «Відхиленим» і додає аркуш у <c>sheetsTotal</c>);</item>
/// <item>у <c>DocumentStore.WhereState</c> прибрати <c>HiddenParticipantSheetRows</c> → той самий тест (фільтр
/// <c>state=Rejected</c> знаходить документ за схованим аркушем);</item>
/// <item>у <c>SearchStore</c> прибрати гілку <c>participatedJson</c> → червоніє тест пошуку.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentParticipantsReachApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Participants-Reach-2026!";
    private const string QueueRoute = "/api/v1/me/approval-queue";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-444")]
    public async Task Без_маршруту_черга_учасника_збігається_з_правом_затвердити()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var approveDocument = await NewParticipantAsync(s, "rqa").ConfigureAwait(true);
        var readDocument = await NewParticipantAsync(s, "rqr").ConfigureAwait(true);
        var approveTable = await NewParticipantAsync(s, "rqt").ConfigureAwait(true);
        var approveOtherSheet = await NewParticipantAsync(s, "rqo").ConfigureAwait(true);

        await GrantAsync(app, manager, s, null, null, approveDocument, "Approve").ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, readDocument, "Read").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode1, s.TableCode1, approveTable, "Approve").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode2, null, approveOtherSheet, "Approve").ConfigureAwait(true);

        using var approveDocumentClient = await SignedInAsync(app, approveDocument.UserName).ConfigureAwait(true);

        // До подання черга порожня.
        Assert.False(await InQueueAsync(app, approveDocumentClient, s).ConfigureAwait(true));

        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);

        // ⛔ Консистентність: для КОЖНОГО учасника «у черзі» == «затвердження пройшло». Той, хто може, — останнім
        // (його затвердження змінює стан аркуша).
        var cases = new[]
        {
            (Person: readDocument, Expected: false),
            (Person: approveTable, Expected: false),
            (Person: approveOtherSheet, Expected: false),
            (Person: approveDocument, Expected: true),
        };
        foreach (var (person, expected) in cases)
        {
            using var client = await SignedInAsync(app, person.UserName).ConfigureAwait(true);
            var inQueue = await InQueueAsync(app, client, s).ConfigureAwait(true);
            using var approve = await ApproveAsync(client, s, s.Sheet1).ConfigureAwait(true);
            var approved = approve.StatusCode == HttpStatusCode.NoContent;

            Assert.True(inQueue == approved, $"{person.UserName}: у черзі={inQueue}, затвердження={approve.StatusCode}");
            Assert.True(expected == approved, $"{person.UserName}: очікувано {expected}, затвердження={approve.StatusCode}\n{app.ErrorsText}");
        }

        // Аркуш, схований від учасника (надання лише на інший аркуш), — 404, а не 403 (D-444 п.1).
        using (var other = await SignedInAsync(app, approveOtherSheet.UserName).ConfigureAwait(true))
        using (var hidden = await ApproveAsync(other, s, s.Sheet1).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        // Затверджено — з черги зникло.
        Assert.False(await InQueueAsync(app, approveDocumentClient, s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-444")]
    public async Task З_маршрутом_учасник_Approve_потребує_ролі_кроку_і_в_черзі_і_в_затвердженні()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var withoutRole = await NewParticipantAsync(s, "rrn").ConfigureAwait(true);
        var withRole = await NewParticipantAsync(s, "rrw").ConfigureAwait(true);
        await RouteAsync(s, withRole.RoleId).ConfigureAwait(true);

        await GrantAsync(app, manager, s, null, null, withoutRole, "Approve").ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, withRole, "Approve").ConfigureAwait(true);
        await SubmitAsync(app, s, s.Sheet1).ConfigureAwait(true);

        // Надання Approve без ролі кроку: ні в черзі, ні затвердження (403 з причиною — документ і аркуш він бачить).
        using (var client = await SignedInAsync(app, withoutRole.UserName).ConfigureAwait(true))
        {
            Assert.False(await InQueueAsync(app, client, s).ConfigureAwait(true));
            using var refused = await ApproveAsync(client, s, s.Sheet1).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // З роллю кроку — у черзі з номером кроку, затверджує, і черга порожніє.
        using var approver = await SignedInAsync(app, withRole.UserName).ConfigureAwait(true);
        var item = Assert.Single(await ItemsAsync(app, approver).ConfigureAwait(true));
        Assert.Equal(s.DocumentId, item.GetProperty("documentId").GetInt64());
        Assert.Equal(1, item.GetProperty("stepOrdinal").GetInt32());
        using (var approved = await ApproveAsync(approver, s, s.Sheet1).ConfigureAwait(true))
        {
            Assert.True(approved.StatusCode == HttpStatusCode.NoContent, $"{approved.StatusCode}: {app.ErrorsText}");
        }

        Assert.False(await InQueueAsync(app, approver, s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-444")]
    public async Task Зведення_переліку_лічить_документ_учасника_лише_за_його_аркушами()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Аркуш 2 подано й відхилено; учасникові надано лише таблицю аркуша 1 (чернетка).
        await SubmitAsync(app, s, s.Sheet2).ConfigureAwait(true);
        using (var rejected = await manager.PostAsJsonAsync(
                   new Uri($"/api/v1/documents/{Num(s.DocumentId)}/approve", UriKind.Relative),
                   new { sheetDefId = s.Sheet2, periodKey = s.PeriodKey, approved = false, reason = "Fix" }).ConfigureAwait(true))
        {
            Assert.True(rejected.StatusCode == HttpStatusCode.NoContent, $"{rejected.StatusCode}: {app.ErrorsText}");
        }

        var participant = await NewParticipantAsync(s, "rsm").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode1, s.TableCode1, participant, "Read").ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        // Перелік: рівно один документ (сусідній документ проєкту — ні).
        var listed = await IdsAsync(app, client, $"/api/v1/documents?limit=100&periodKey={s.PeriodKey}").ConfigureAwait(true);
        Assert.Equal([s.DocumentId], listed);

        // Зведення — той самий документ, і лише за видимим аркушем 1: чернетка, не «відхилено», один аркуш.
        var summary = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(1, summary.GetProperty("draft").GetInt32());
        Assert.Equal(0, summary.GetProperty("submitted").GetInt32());
        Assert.Equal(0, summary.GetProperty("approved").GetInt32());
        Assert.Equal(0, summary.GetProperty("rejected").GetInt32());
        Assert.Equal(1, summary.GetProperty("sheetsTotal").GetInt32());
        Assert.Equal(0, summary.GetProperty("sheetsApproved").GetInt32());
        Assert.Equal(0, summary.GetProperty("withIssues").GetInt32());
        Assert.Equal(listed.Count, summary.GetProperty("draft").GetInt32() + summary.GetProperty("submitted").GetInt32()
                                   + summary.GetProperty("approved").GetInt32() + summary.GetProperty("rejected").GetInt32());

        // Фільтр за станом збігається з лічильниками: «Чернетка» — документ є, «Відхилено» (лише схований аркуш) — немає.
        Assert.Equal(
            [s.DocumentId],
            await IdsAsync(app, client, $"/api/v1/documents?limit=100&periodKey={s.PeriodKey}&state=Draft").ConfigureAwait(true));
        Assert.Empty(await IdsAsync(app, client, $"/api/v1/documents?limit=100&periodKey={s.PeriodKey}&state=Rejected").ConfigureAwait(true));

        // Менеджер (роль на проєкт) бачить відхилений аркуш — контроль, що сценарій справді має «відхилене».
        using var managerClient = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        Assert.Contains(
            s.DocumentId,
            await IdsAsync(app, managerClient, $"/api/v1/documents?limit=100&periodKey={s.PeriodKey}&state=Rejected").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-444")]
    public async Task Пошук_знаходить_лише_наданий_документ_учасника()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var participant = await NewParticipantAsync(s, "rfs").ConfigureAwait(true);
        var stranger = await NewParticipantAsync(s, "rfx").ConfigureAwait(true);

        using (var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true))
        {
            // До надання — нічого.
            Assert.DoesNotContain(s.DocumentId, await SearchAsync(app, client, s.DocumentKey).ConfigureAwait(true));
        }

        await GrantAsync(app, manager, s, s.SheetCode1, s.TableCode1, participant, "Read").ConfigureAwait(true);

        using var participantClient = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);
        Assert.Contains(s.DocumentId, await SearchAsync(app, participantClient, s.DocumentKey).ConfigureAwait(true));

        // Сусідній документ того самого проєкту (без надання) — не знаходиться; аркуш поза наданням пошук не називає.
        Assert.Empty(await SearchAsync(app, participantClient, s.OtherDocumentKey).ConfigureAwait(true));
        Assert.Empty(await SearchAsync(app, participantClient, s.SheetCode2).ConfigureAwait(true));

        // Користувач без надань — нічого (той самий проєкт, той самий запит).
        using var strangerClient = await SignedInAsync(app, stranger.UserName).ConfigureAwait(true);
        Assert.DoesNotContain(s.DocumentId, await SearchAsync(app, strangerClient, s.DocumentKey).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task GrantAsync(
        EcrApiFactory app, HttpClient manager, Scenario s, string? sheet, string? table, Person recipient, string level)
    {
        using var response = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/edit-assignments", UriKind.Relative),
            new
            {
                slice = new { sheetCode = sheet, tableCode = table },
                recipientUserIds = new[] { recipient.Id },
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

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Scenario s, int sheetDefId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/approve", UriKind.Relative),
            new { sheetDefId, periodKey = s.PeriodKey, approved = true, reason = (string?)null });

    private static async Task<bool> InQueueAsync(EcrApiFactory app, HttpClient client, Scenario s)
        => (await ItemsAsync(app, client).ConfigureAwait(false))
            .Exists(i => i.GetProperty("documentId").GetInt64() == s.DocumentId
                         && i.GetProperty("sheetDefId").GetInt32() == s.Sheet1);

    private static async Task<List<JsonElement>> ItemsAsync(EcrApiFactory app, HttpClient client)
    {
        var page = await JsonAsync(
            await client.GetAsync(new Uri(QueueRoute, UriKind.Relative)).ConfigureAwait(false), HttpStatusCode.OK, app)
            .ConfigureAwait(false);
        return [.. page.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<List<long>> IdsAsync(EcrApiFactory app, HttpClient client, string uri)
    {
        var list = await JsonAsync(await client.GetAsync(new Uri(uri, UriKind.Relative)).ConfigureAwait(false), HttpStatusCode.OK, app)
            .ConfigureAwait(false);
        return [.. list.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetInt64())];
    }

    private static async Task<List<long>> SearchAsync(EcrApiFactory app, HttpClient client, string term)
    {
        var hits = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/search?q={Uri.EscapeDataString(term)}&limit=20", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);
        return [.. hits.EnumerateArray()
            .Where(h => h.GetProperty("kind").GetString() == "document")
            .Select(h => h.GetProperty("id").GetInt64())];
    }

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

    /// <summary>Маршрут ПРОЄКТУ документа з одним кроком за роллю.</summary>
    private async Task RouteAsync(Scenario s, int roleId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var route = new ApprovalRoute(
            EcrCode.Create($"PQ_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-444 route" }),
            s.ProjectId);
        route.AddStep(roleId);
        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Учасник без жодного гранта на проєкт: власна роль без прав (її можна зробити роллю кроку маршруту).</summary>
    private async Task<Person> NewParticipantAsync(Scenario s, string prefix)
        => await NewUserAsync(s.ProjectId, prefix, level: null, withView: false).ConfigureAwait(false);

    private async Task<Person> NewUserAsync(int projectId, string prefix, GrantLevel? level, bool withView)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DR_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Participants reach test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withView)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (level is { } l)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, l));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Person(user.Id, userName, role.Id);
    }

    /// <summary>
    /// Документ із ДВОМА аркушами у складі (по таблиці й екземпляру за відкритий період), сусідній документ того самого
    /// проєкту, менеджер (Manage) і автор подань (Submit).
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(document).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create($"SHR{tag}"), Text("Sheet R"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table2 = new TableDef(sheet2.Id, EcrCode.Create($"TBR{tag}"), Text("Table R"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ColumnDefs.Add(new ColumnDef(table2.Id, EcrCode.Create($"CR{tag}"), Text("Col R"), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table2.Id, RowKey.Create($"RR{tag}"), 1, Text("Row R"), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2 = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var row2 = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(document.PeriodKey, instance2, document.DocumentId, table2.Id, now));
        db.TableRows.Add(new TableRow(document.PeriodKey, row2, instance2, RowKey.Create($"RR{tag}"), 1, now));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheet2.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableCode1 = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        var documentKey = await db.Documents.Where(d => d.Id == document.DocumentId).Select(d => d.BusinessKey).SingleAsync().ConfigureAwait(false);

        var manager = await NewUserAsync(document.ProjectId, "rpm", GrantLevel.Manage, withView: true).ConfigureAwait(false);
        var submitter = await NewUserAsync(document.ProjectId, "rps", GrantLevel.Submit, withView: true).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {manager.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        var otherKey = $"RPO{Guid.NewGuid():N}"[..14];
        var other = new Document(document.ProjectId, otherKey, manager.Id, now);
        db.Documents.Add(other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, documentKey, other.Id, otherKey, document.ProjectId, document.PeriodKey.Value,
            document.SheetDefId, sheet2.Id, document.SheetCode, sheet2.Code, tableCode1,
            manager, submitter);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName, int RoleId);

    private sealed record Scenario(
        long DocumentId, string DocumentKey, long OtherDocumentId, string OtherDocumentKey, int ProjectId, int PeriodKey,
        int Sheet1, int Sheet2, string SheetCode1, string SheetCode2, string TableCode1,
        Person Manager, Person Submitter);
}
