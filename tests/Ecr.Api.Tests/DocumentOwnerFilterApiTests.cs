// tests/Ecr.Api.Tests/DocumentOwnerFilterApiTests.cs
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
/// ✎ D-450 наскрізно через HTTP: фільтр переліку документів за власником (<c>ownerUserId</c>) і вибір власників
/// (<c>GET /api/v1/documents/owners</c>) — лише серед документів, які читач бачить, і лише там, де ім'я власника йому
/// віддається.
/// </summary>
/// <remarks>
/// ⛔ Безпека: вибір, що називав би власників невидимих документів, — оракул «хто працює в чужому проєкті»; фільтр за
/// власником документа, де ім'я власника читачеві не віддається (звужений доступ), — оракул «чий це документ».
/// <para>Мутаційний доказ (локально, не в коміті):</para>
/// <list type="bullet">
/// <item>у <c>DocumentStore.ListOwnersAsync</c> замінити <c>VisibleDocuments(...)</c> на <c>db.Documents</c> → червоніють
/// <c>Вибір_власників_лише_серед_документів_видимих_за_роллю</c> і <c>Учасник_бачить_власника_документа_надання_і_нікого_більше</c>
/// (у виборі з'являється власник, чиї документи лише в проєкті B);</item>
/// <item>у <c>DocumentStore.ListAsync</c> / <c>ListOwnersAsync</c> прибрати <c>WhereOwnerDisclosed</c> → червоніє
/// <c>Частково_схований_документ_поза_фільтром_і_вибором_власників</c>;</item>
/// <item>у <c>ListDocumentsHandler.ValidateOwner</c> прибрати гілку <c>mine</c> → червоніє
/// <c>mine_разом_з_ownerUserId_і_неправильний_ownerUserId_422</c>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentOwnerFilterApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Owner-Filter-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Фільтр_ownerUserId_віддає_лише_документи_цієї_людини()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync("ofv", withView: true, (s.ProjectA, GrantLevel.Read), (s.ProjectB, GrantLevel.Read)).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer.UserName).ConfigureAwait(true);

        // Контроль сценарію: без фільтра видно обидва документи, власник у рядку — з ідентифікатором.
        var all = await ListAsync(app, client, s, string.Empty).ConfigureAwait(true);
        Assert.Contains(s.DocumentA, Ids(all));
        Assert.Contains(s.DocumentB, Ids(all));
        Assert.Equal(s.OwnerA.Id, Row(all, s.DocumentA).GetProperty("ownerUserId").GetInt32());

        var ofA = await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerA.Id)}").ConfigureAwait(true);
        Assert.Contains(s.DocumentA, Ids(ofA));
        Assert.DoesNotContain(s.DocumentB, Ids(ofA));
        Assert.All(ofA, d => Assert.Equal(s.OwnerA.Id, d.GetProperty("ownerUserId").GetInt32()));

        var ofB = await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerB.Id)}").ConfigureAwait(true);
        Assert.Equal([s.DocumentB], Ids(ofB).Where(id => id == s.DocumentA || id == s.DocumentB).ToList());

        // Поєднується з іншими фільтрами: проєкт A + власник B — порожньо.
        Assert.Empty(await ListAsync(app, client, s, $"&projectId={Num(s.ProjectA)}&ownerUserId={Num(s.OwnerB.Id)}").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Вибір_власників_лише_серед_документів_видимих_за_роллю()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Роль лише на проєкт A.
        var viewerA = await NewUserAsync("ova", withView: true, (s.ProjectA, GrantLevel.Read)).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewerA.UserName).ConfigureAwait(true);

        var owners = await OwnersAsync(app, client, s, string.Empty).ConfigureAwait(true);
        var a = owners.Single(o => o.GetProperty("userId").GetInt32() == s.OwnerA.Id);
        Assert.Equal(s.OwnerA.UserName, a.GetProperty("displayName").GetString());
        Assert.Equal(1, a.GetProperty("documentCount").GetInt32());

        // ⛔ Власник, чиї документи лише в проєкті B, не називається.
        Assert.DoesNotContain(owners, o => o.GetProperty("userId").GetInt32() == s.OwnerB.Id);
        Assert.Empty(await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerB.Id)}").ConfigureAwait(true));

        // Пошук за іменем: підрядок знаходить, чужий — ні.
        var found = await OwnersAsync(app, client, s, "&q=" + Uri.EscapeDataString(s.OwnerA.UserName[3..10])).ConfigureAwait(true);
        Assert.Contains(found, o => o.GetProperty("userId").GetInt32() == s.OwnerA.Id);
        Assert.Empty(await OwnersAsync(app, client, s, "&q=" + Uri.EscapeDataString(s.OwnerB.UserName)).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Учасник_бачить_власника_документа_надання_і_нікого_більше()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var ownerA = await SignedInAsync(app, s.OwnerA.UserName).ConfigureAwait(true);

        // Учасник без жодної ролі на проєкт, надання на ВЕСЬ документ A.
        var participant = await NewUserAsync("ofp", withView: false).ConfigureAwait(true);
        await GrantAsync(app, ownerA, s.DocumentA, null, null, participant).ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        var owners = await OwnersAsync(app, client, s, string.Empty).ConfigureAwait(true);
        var a = Assert.Single(owners);
        Assert.Equal(s.OwnerA.Id, a.GetProperty("userId").GetInt32());
        Assert.Equal(1, a.GetProperty("documentCount").GetInt32());

        Assert.Equal([s.DocumentA], Ids(await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerA.Id)}").ConfigureAwait(true)));
        Assert.Empty(await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerB.Id)}").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task Частково_схований_документ_поза_фільтром_і_вибором_власників()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var ownerA = await SignedInAsync(app, s.OwnerA.UserName).ConfigureAwait(true);

        // Надання лише на таблицю аркуша 1; аркуш 2 документа A учасникові схований.
        var participant = await NewUserAsync("ofn", withView: false).ConfigureAwait(true);
        await GrantAsync(app, ownerA, s.DocumentA, s.SheetCodeA1, s.TableCodeA1, participant).ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        // Контроль сценарію: документ у переліку є, а ім'я власника не віддається (DocumentSheetVisibility.For).
        var row = Row(await ListAsync(app, client, s, string.Empty).ConfigureAwait(true), s.DocumentA);
        Assert.Equal(JsonValueKind.Null, Field(row, "ownerDisplayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(row, "ownerUserId").ValueKind);

        // ⛔ Тож ні фільтр, ні вибір цього власника не підтверджують.
        Assert.Empty(await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerA.Id)}").ConfigureAwait(true));
        Assert.Empty(await OwnersAsync(app, client, s, string.Empty).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-450")]
    public async Task mine_разом_з_ownerUserId_і_неправильний_ownerUserId_422()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.OwnerA.UserName).ConfigureAwait(true);

        await RefusedAsync(app, client, s, $"&mine=true&ownerUserId={Num(s.OwnerA.Id)}", "err.ECR-REQ-0422.ownerWithMine").ConfigureAwait(true);
        await RefusedAsync(app, client, s, "&ownerUserId=0", "err.ECR-REQ-0422.ownerUserId").ConfigureAwait(true);
        await RefusedAsync(app, client, s, "&ownerUserId=-5", "err.ECR-REQ-0422.ownerUserId").ConfigureAwait(true);

        // Дзеркало: кожен окремо — 200.
        Assert.Contains(s.DocumentA, Ids(await ListAsync(app, client, s, "&mine=true").ConfigureAwait(true)));
        Assert.Contains(s.DocumentA, Ids(await ListAsync(app, client, s, $"&ownerUserId={Num(s.OwnerA.Id)}").ConfigureAwait(true)));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static List<long> Ids(List<JsonElement> items) => [.. items.Select(d => d.GetProperty("id").GetInt64())];

    private static JsonElement Row(List<JsonElement> items, long documentId) => items.Single(d => d.GetProperty("id").GetInt64() == documentId);

    /// <summary>Поле відповіді; відсутнє розглядається як <c>null</c> (серіалізатор може пропускати null).</summary>
    private static JsonElement Field(JsonElement doc, string name)
        => doc.TryGetProperty(name, out var value) ? value : JsonDocument.Parse("null").RootElement;

    private static async Task<List<JsonElement>> ListAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra)
    {
        var list = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents?limit=200&periodKey={Num(s.PeriodKey)}{extra}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);
        return [.. list.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<List<JsonElement>> OwnersAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra)
    {
        var body = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents/owners?periodKey={Num(s.PeriodKey)}{extra}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);
        return [.. body.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task RefusedAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra, string messageKey)
    {
        using var response = await client
            .GetAsync(new Uri($"/api/v1/documents?periodKey={Num(s.PeriodKey)}{extra}", UriKind.Relative)).ConfigureAwait(false);
        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(false);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
    }

    private static async Task GrantAsync(
        EcrApiFactory app, HttpClient owner, long documentId, string? sheet, string? table, Person recipient)
    {
        using var response = await owner.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(documentId)}/edit-assignments", UriKind.Relative),
            new
            {
                slice = new { sheetCode = sheet, tableCode = table },
                recipientUserIds = new[] { recipient.Id },
                isExclusive = false,
                level = "Read",
            }).ConfigureAwait(false);
        await JsonAsync(response, HttpStatusCode.OK, app).ConfigureAwait(false);
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

    /// <summary>Користувач із власною роллю: гранти на перелічені проєкти (порожньо — жодного) і, за потреби, <c>Document.View</c>.</summary>
    private async Task<Person> NewUserAsync(string prefix, bool withView, params (int ProjectId, GrantLevel Level)[] grants)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"OF_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Owner filter test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withView)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        foreach (var (projectId, level) in grants)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, level));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Person(user.Id, userName);
    }

    /// <summary>
    /// Документ A (проєкт A, ДВА аркуші у складі) власника A з <c>Manage</c> на A і документ B (проєкт B) власника B,
    /// у якого документи лише в B.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var documentA = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(documentA).ConfigureAwait(false);
        var documentB = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(documentB).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Другий аркуш документа A — щоб надання лише на аркуш 1 звужувало документ (D-443/D-446).
        var sheet2 = new SheetDef(documentA.TemplateVersionId, EcrCode.Create($"SHO{tag}"), Text("Sheet O"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table2 = new TableDef(sheet2.Id, EcrCode.Create($"TBO{tag}"), Text("Table O"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ColumnDefs.Add(new ColumnDef(table2.Id, EcrCode.Create($"CO{tag}"), Text("Col O"), 1, CellDataType.Decimal));
        db.RowDefs.Add(new RowDef(table2.Id, RowKey.Create($"RO{tag}"), 1, Text("Row O"), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2 = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var row2 = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(documentA.PeriodKey, instance2, documentA.DocumentId, table2.Id, now));
        db.TableRows.Add(new TableRow(documentA.PeriodKey, row2, instance2, RowKey.Create($"RO{tag}"), 1, now));
        db.DocumentSheets.Add(new DocumentSheet(documentA.DocumentId, documentA.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(documentA.DocumentId, sheet2.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableCodeA1 = await db.TableDefs.Where(t => t.Id == documentA.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        var ownerA = await NewUserAsync("ofa", withView: true, (documentA.ProjectId, GrantLevel.Manage)).ConfigureAwait(false);
        var ownerB = await NewUserAsync("ofb", withView: true, (documentB.ProjectId, GrantLevel.Manage)).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {ownerA.Id} WHERE Id = {documentA.DocumentId}").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {ownerB.Id} WHERE Id = {documentB.DocumentId}").ConfigureAwait(false);

        return new Scenario(
            documentA.DocumentId, documentA.ProjectId, documentB.DocumentId, documentB.ProjectId, documentA.PeriodKey.Value,
            documentA.SheetCode, tableCodeA1, ownerA, ownerB);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentA, int ProjectA, long DocumentB, int ProjectB, int PeriodKey, string SheetCodeA1, string TableCodeA1,
        Person OwnerA, Person OwnerB);
}
