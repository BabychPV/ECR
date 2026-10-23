// tests/Ecr.Api.Tests/DocumentParticipantsApiTests.cs
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
/// D-443 (AN-130), DoD П.2 наскрізно через HTTP: учасник документа без ролі на проєкт, якому надано одну таблицю,
/// бачить документ у переліку, відкриває лише цю таблицю, чужий аркуш — недоступний; <c>Approve</c> видає лише
/// власник <c>Approve</c>; заборона ролі перемагає надання; передача власника працює й журналюється.
/// </summary>
/// <remarks>
/// ⚠ Сховані аркуш і таблиця відповідають <c>404</c> (та сама відповідь, що й для <c>Deny</c> ролі, B-08/S6 — різниця
/// 403/404 була б оракулом існування); видима таблиця без рівня запису — <c>403</c> з причиною.
/// <para>Мутаційний доказ:</para>
/// <list type="bullet">
/// <item>прибрати гілку надання в <c>AccessDecisionService.CanReadDocumentAsync</c> → червоніє
/// <c>Без_ролі_з_однією_таблицею_…</c> (GET документа 404);</item>
/// <item>прибрати підзапит <c>Participation</c> у <c>DocumentStore.ListAsync</c> → той самий тест (документа немає в переліку);</item>
/// <item>прибрати <c>profile.HasDocumentParticipations ||</c> у <c>DocumentSheetVisibility.HasRestrictions</c> → той самий тест
/// (зріз таблиці другого аркуша віддається 200, а перелік таблиць містить її);</item>
/// <item>прибрати перевірку <c>ownLevel &lt; level</c> в <c>UpsertEditAssignmentHandler.PrepareAsync</c> → червоніє
/// <c>Approve_видає_лише_той_хто_сам_має_Approve</c>;</item>
/// <item>прибрати <c>!profile.DeniesProject(projectId)</c> у <c>CanReadDocumentAsync</c> → червоніє
/// <c>Заборона_ролі_перемагає_надання</c> (документ видно попри Deny проєкту);</item>
/// <item>прибрати <c>audit.WriteSecurityEventAsync</c> у <c>TransferDocumentOwnerHandler</c> → червоніє
/// <c>Передача_власника_змінює_власника_і_журналюється</c>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentParticipantsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Participants-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Без_ролі_з_однією_таблицею_бачить_документ_відкриває_лише_її_а_зняття_ховає_знову()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // До надання: документа для учасника немає (без ролі — навіть права переліку).
        using (var before = await SignedInAsync(app, s.Stranger.UserName).ConfigureAwait(true))
        {
            Assert.False((await before.GetAsync(DocumentUri(s)).ConfigureAwait(true)).IsSuccessStatusCode);
        }

        var granted = await JsonAsync(
            await manager.PostAsJsonAsync(AssignmentsUri(s), Grant(s.SheetCode1, s.TableCode1, s.Stranger.Id, "Read")).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Read", granted.GetProperty("level").GetString());
        Assert.Equal("Table", granted.GetProperty("scope").GetString());
        var assignmentId = granted.GetProperty("id").GetInt64();

        using var participant = await SignedInAsync(app, s.Stranger.UserName).ConfigureAwait(true);

        // Перелік документів: документ є, сусідній документ того самого проєкту — ні.
        var list = await JsonAsync(await participant.GetAsync(new Uri("/api/v1/documents?limit=100", UriKind.Relative)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var ids = list.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).ToList();
        Assert.Contains(s.DocumentId, ids);
        Assert.DoesNotContain(s.OtherDocumentId, ids);

        // Картка документа відкривається; таблиці — лише надана.
        await JsonAsync(await participant.GetAsync(DocumentUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var tables = await JsonAsync(
            await participant.GetAsync(new Uri($"{DocumentUri(s)}/tables?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var visible = tables.EnumerateArray().Select(t => t.GetProperty("tableInstanceId").GetInt64()).ToList();
        Assert.Equal([s.TableInstance1], visible);

        // Зріз наданої таблиці — 200; таблиця іншого аркуша — 404 (як для Deny ролі: не оракул).
        Assert.Equal(HttpStatusCode.OK, (await participant.GetAsync(SliceUri(s, s.TableInstance1)).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync(SliceUri(s, s.TableInstance2)).ConfigureAwait(true)).StatusCode);

        // Чужий документ того самого проєкту — 404.
        Assert.Equal(HttpStatusCode.NotFound, (await participant.GetAsync(
            new Uri($"/api/v1/documents/{Num(s.OtherDocumentId)}", UriKind.Relative)).ConfigureAwait(true)).StatusCode);

        // Зняття надання — документ зникає одразу (надання читаються з бази, не з кешу).
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(
            new Uri($"{AssignmentsUri(s)}/{Num(assignmentId)}", UriKind.Relative)).ConfigureAwait(true)).StatusCode);
        Assert.False((await participant.GetAsync(DocumentUri(s)).ConfigureAwait(true)).IsSuccessStatusCode);
        Assert.False((await participant.GetAsync(SliceUri(s, s.TableInstance1)).ConfigureAwait(true)).IsSuccessStatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Approve_видає_лише_той_хто_сам_має_Approve()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignedInAsync(app, s.Author.UserName).ConfigureAwait(true);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Власник документа з Write на проєкт: Approve — 403 «не вище власного», нічого не створено.
        var refused = await author.PostAsJsonAsync(AssignmentsUri(s), Grant(s.SheetCode1, null, s.Stranger.Id, "Approve")).ConfigureAwait(true);
        var body = await refused.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{refused.StatusCode}: {body}");
        Assert.Equal("err.ECR-ACCS-0403.editAssignmentLevelAboveOwn", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());

        // Той самий власник видає Read (≤ власного) — 200.
        await JsonAsync(
            await author.PostAsJsonAsync(AssignmentsUri(s), Grant(s.SheetCode1, null, s.Stranger.Id, "Read")).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);

        // Менеджер (Manage ≥ Approve) видає Approve — 200.
        var approve = await JsonAsync(
            await manager.PostAsJsonAsync(AssignmentsUri(s), Grant(s.SheetCode1, null, s.Stranger.Id, "Approve")).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Approve", approve.GetProperty("level").GetString());

        // Місяць для рівня, відмінного від Write, — 422.
        var monthly = await manager.PostAsJsonAsync(
            AssignmentsUri(s),
            new { slice = new { sheetCode = s.SheetCode1, monthNumber = 1 }, recipientUserIds = new[] { s.Stranger.Id }, isExclusive = false, level = "Read" })
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, monthly.StatusCode);

        // Журнал: видача несе рівень і область.
        var granted = await EventDetailsAsync(s, "EditAssignmentGranted").ConfigureAwait(true);
        Assert.Contains(granted, d => d.Contains("\"level\":\"Approve\"", StringComparison.Ordinal)
                                      && d.Contains("\"scope\":\"Sheet\"", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Заборона_ролі_перемагає_надання()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Учасник із забороною аркуша 1 від ролі отримує Approve на весь документ: аркуш 1 схований, аркуш 2 — видно.
        await JsonAsync(
            await manager.PostAsJsonAsync(AssignmentsUri(s), Grant(null, null, s.SheetDenied.Id, "Approve")).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        using (var sheetDenied = await SignedInAsync(app, s.SheetDenied.UserName).ConfigureAwait(true))
        {
            var tables = await JsonAsync(
                await sheetDenied.GetAsync(new Uri($"{DocumentUri(s)}/tables?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true),
                HttpStatusCode.OK, app).ConfigureAwait(true);
            Assert.Equal([s.TableInstance2], tables.EnumerateArray().Select(t => t.GetProperty("tableInstanceId").GetInt64()).ToList());
            Assert.Equal(HttpStatusCode.NotFound, (await sheetDenied.GetAsync(SliceUri(s, s.TableInstance1)).ConfigureAwait(true)).StatusCode);
        }

        // Заборона проєкту: надання на весь документ не відкриває нічого — ні картки, ні переліку.
        await JsonAsync(
            await manager.PostAsJsonAsync(AssignmentsUri(s), Grant(null, null, s.ProjectDenied.Id, "Read")).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        using var projectDenied = await SignedInAsync(app, s.ProjectDenied.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, (await projectDenied.GetAsync(DocumentUri(s)).ConfigureAwait(true)).StatusCode);
        var list = await projectDenied.GetAsync(new Uri("/api/v1/documents?limit=100", UriKind.Relative)).ConfigureAwait(true);
        if (list.StatusCode == HttpStatusCode.OK)
        {
            var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement
                .GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetInt64());
            Assert.DoesNotContain(s.DocumentId, listed);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Передача_власника_змінює_власника_і_журналюється()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var writer = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);

        // Не власник і не менеджер — 403.
        var forbidden = await writer.PostAsJsonAsync(OwnerUri(s), new { newOwnerUserId = s.Writer.Id }).ConfigureAwait(true);
        var forbiddenBody = await forbidden.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(forbidden.StatusCode == HttpStatusCode.Forbidden, $"{forbidden.StatusCode}: {forbiddenBody}");
        Assert.Equal(
            "err.ECR-ACCS-0403.documentOwnerTransferForbidden",
            JsonDocument.Parse(forbiddenBody).RootElement.GetProperty("messageKey").GetString());

        // Кандидат без права запису в проєкті (учасник без ролі) — 422, власник не змінився.
        var invalid = await manager.PostAsJsonAsync(OwnerUri(s), new { newOwnerUserId = s.Stranger.Id }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(s.Author.Id, await OwnerAsync(s).ConfigureAwait(true));

        // Менеджер передає власність автору Writer — 200, власник змінився, подія з обома ідентифікаторами.
        var moved = await JsonAsync(
            await manager.PostAsJsonAsync(OwnerUri(s), new { newOwnerUserId = s.Writer.Id }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(s.Writer.Id, moved.GetProperty("ownerUserId").GetInt32());
        Assert.Equal(s.Author.Id, moved.GetProperty("previousOwnerUserId").GetInt32());
        Assert.Equal(s.Writer.Id, await OwnerAsync(s).ConfigureAwait(true));

        var events = await EventDetailsAsync(s, "DocumentOwnerTransferred").ConfigureAwait(true);
        var detail = Assert.Single(events);
        Assert.Contains($"\"previousOwnerUserId\":{s.Author.Id.ToString(CultureInfo.InvariantCulture)}", detail, StringComparison.Ordinal);
        Assert.Contains($"\"newOwnerUserId\":{s.Writer.Id.ToString(CultureInfo.InvariantCulture)}", detail, StringComparison.Ordinal);

        // Новий власник (Write) тепер керує учасниками, а колишній — ні.
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync(AssignmentsUri(s)).ConfigureAwait(true)).StatusCode);
        using var author = await SignedInAsync(app, s.Author.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync(AssignmentsUri(s)).ConfigureAwait(true)).StatusCode);
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static Uri DocumentUri(Scenario s) => new($"/api/v1/documents/{Num(s.DocumentId)}", UriKind.Relative);

    private static Uri AssignmentsUri(Scenario s) => new($"/api/v1/documents/{Num(s.DocumentId)}/edit-assignments", UriKind.Relative);

    private static Uri OwnerUri(Scenario s) => new($"/api/v1/documents/{Num(s.DocumentId)}/owner", UriKind.Relative);

    private static Uri SliceUri(Scenario s, long instance)
        => new($"/api/v1/documents/{Num(s.DocumentId)}/tables/{Num(instance)}", UriKind.Relative);

    private static object Grant(string? sheet, string? table, int recipient, string level)
        => new
        {
            slice = new { sheetCode = sheet, tableCode = table },
            recipientUserIds = new[] { recipient },
            isExclusive = false,
            level,
        };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> OwnerAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Documents.Where(d => d.Id == s.DocumentId).Select(d => d.CreatedByUserId).SingleAsync().ConfigureAwait(false);
    }

    private async Task<List<string>> EventDetailsAsync(Scenario s, string eventType)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var docMarker = $"\"documentId\":{Num(s.DocumentId)}";
        return await db.Database
            .SqlQuery<string>($"SELECT DetailsJson AS Value FROM aud.SecurityEvent WHERE EventType = {eventType} AND DetailsJson LIKE {"%" + docMarker + "%"}")
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

    /// <summary>
    /// Документ із ДВОМА аркушами (по таблиці й екземпляру за період) і користувачі: менеджер (Manage), автор-власник
    /// (Write), інший автор (Write), сторонній (жодної ролі на проєкт), заборона аркуша 1, заборона проєкту.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Другий аркуш тієї самої версії з таблицею, екземпляром документа за той самий період і рядком.
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

        var tableCode1 = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        async Task<Person> NewUserAsync(string prefix, GrantLevel? level, bool withView, ResourceKind? deny = null, int denyId = 0)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"DP_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Participants test" }));
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

            if (deny is { } kind)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, denyId, GrantLevel.None, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return new Person(user.Id, userName);
        }

        var manager = await NewUserAsync("dpm", GrantLevel.Manage, withView: true).ConfigureAwait(false);
        var author = await NewUserAsync("dpa", GrantLevel.Write, withView: true).ConfigureAwait(false);
        var writer = await NewUserAsync("dpw", GrantLevel.Write, withView: true).ConfigureAwait(false);
        var stranger = await NewUserAsync("dps", null, withView: false).ConfigureAwait(false);
        var sheetDenied = await NewUserAsync("dpd", null, withView: false, ResourceKind.Sheet, document.SheetDefId).ConfigureAwait(false);
        var projectDenied = await NewUserAsync("dpp", null, withView: false, ResourceKind.Project, document.ProjectId).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {author.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        var other = new Document(document.ProjectId, $"DPO{Guid.NewGuid():N}"[..14], author.Id, now);
        db.Documents.Add(other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, other.Id, document.PeriodKey.Value,
            document.SheetCode, tableCode1, document.TableInstanceId, instance2,
            manager, author, writer, stranger, sheetDenied, projectDenied);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, long OtherDocumentId, int PeriodKey,
        string SheetCode1, string TableCode1, long TableInstance1, long TableInstance2,
        Person Manager, Person Author, Person Writer, Person Stranger, Person SheetDenied, Person ProjectDenied);
}
