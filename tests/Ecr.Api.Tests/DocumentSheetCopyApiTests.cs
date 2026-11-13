// tests/Ecr.Api.Tests/DocumentSheetCopyApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): контракт API — додати копію, перейменувати групу, видалити копію; таблиці
/// документа (порядок, <c>sheetCopyId</c>, <c>copyOrdinal</c>, <c>displayNameL10n</c>) і ключі <c>sheetStates</c>.
/// </summary>
/// <remarks>
/// Наскрізно, справжнім HTTP: 409 із доменного винятку і ключі каталогу дає <c>ExceptionHandlingMiddleware</c>.
/// ⛔ Мутація (локально, не комітиться): без перенумерації в <c>DocumentSheetCopyStore.DeleteCopyAsync</c> — червоніє
/// <see cref="Видалення_оригінала_і_затвердженої_копії_дає_409_з_ключами_а_копії_2_із_3_перенумеровує_третю"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentSheetCopyApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Sheet-Copy-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Додавання_копій_віддає_номери_а_таблиці_і_стани_знають_копії_у_правильному_порядку()
    {
        var s = await ArrangeAsync(DocumentSheetCopyHandler.Permission).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var first = await PostCopyAsync(client, s).ConfigureAwait(true);
        var second = await PostCopyAsync(client, s).ConfigureAwait(true);
        Assert.True(first.Status == HttpStatusCode.Created, $"{first.Status}: {app.ErrorsText}");
        Assert.Equal(2, first.Ordinal);
        Assert.Equal(3, second.Ordinal);

        var tables = await TablesAsync(client, s).ConfigureAwait(true);

        // Оригінал першим, копії за номером; усі діляться sheetCode/sheetOrdinal.
        Assert.Equal(3, tables.Count);
        Assert.Equal([1, 2, 3], tables.Select(t => t.GetProperty("copyOrdinal").GetInt32()));
        Assert.Equal(
            [null, first.SheetCopyId, second.SheetCopyId],
            tables.Select(t => t.GetProperty("sheetCopyId").ValueKind == JsonValueKind.Null
                ? (long?)null
                : t.GetProperty("sheetCopyId").GetInt64()));
        Assert.Single(tables.Select(t => t.GetProperty("sheetCode").GetString()).Distinct());
        Assert.Single(tables.Select(t => t.GetProperty("sheetOrdinal").GetInt32()).Distinct());
        Assert.All(tables, t => Assert.True(t.GetProperty("isCopyable").GetBoolean()));

        // Копії мають власні екземпляри таблиць.
        Assert.Equal(3, tables.Select(t => t.GetProperty("tableInstanceId").GetInt64()).Distinct().Count());

        var sheetName = s.SheetEnglishName;
        Assert.Equal(
            [$"{sheetName} 1", $"{sheetName} 2", $"{sheetName} 3"],
            tables.Select(t => t.GetProperty("displayNameL10n").GetProperty("values").GetProperty("en").GetString()));

        // Ключі sheetStates: "КОД" / "КОД#sheetCopyId".
        var document = await GetJsonAsync(client, $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}?periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(true);
        var states = document.GetProperty("sheetStates").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(
            [s.SheetCode, $"{s.SheetCode}#{first.SheetCopyId.ToString(CultureInfo.InvariantCulture)}",
             $"{s.SheetCode}#{second.SheetCopyId.ToString(CultureInfo.InvariantCulture)}"],
            states);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Перейменування_групи_міняє_назву_оригінала_і_всіх_копій()
    {
        var s = await ArrangeAsync(DocumentSheetCopyHandler.Permission).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        await PostCopyAsync(client, s).ConfigureAwait(true);

        var rename = await client.PutAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheets/{s.SheetDefId.ToString(CultureInfo.InvariantCulture)}/name", UriKind.Relative),
            new { nameL10n = new Dictionary<string, string> { ["en"] = "Piles" } }).ConfigureAwait(true);
        Assert.True(rename.StatusCode == HttpStatusCode.NoContent, $"{rename.StatusCode}: {app.ErrorsText}");

        var tables = await TablesAsync(client, s).ConfigureAwait(true);
        Assert.Equal(
            ["Piles 1", "Piles 2"],
            tables.Select(t => t.GetProperty("displayNameL10n").GetProperty("values").GetProperty("en").GetString()));

        // Порожня назва — 422 з ключем каталогу.
        var bad = await client.PutAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheets/{s.SheetDefId.ToString(CultureInfo.InvariantCulture)}/name", UriKind.Relative),
            new { nameL10n = new Dictionary<string, string> { ["en"] = " " } }).ConfigureAwait(true);
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Видалення_оригінала_і_затвердженої_копії_дає_409_з_ключами_а_копії_2_із_3_перенумеровує_третю()
    {
        var s = await ArrangeAsync(DocumentSheetCopyHandler.Permission).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var two = await PostCopyAsync(client, s).ConfigureAwait(true);
        var three = await PostCopyAsync(client, s).ConfigureAwait(true);

        // Оригінал.
        var original = await client.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheet-copies/{s.OriginalId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(true);
        var originalBody = JsonDocument.Parse(await original.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(HttpStatusCode.Conflict, original.StatusCode);
        Assert.Equal("ECR-DOC-0409", originalBody.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-0409.sheetCopyIsOriginal", originalBody.GetProperty("messageKey").GetString());

        // Затверджена копія №3 в іншому періоді.
        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var state = new ApprovalState(s.DocumentId, s.SheetDefId, 202601, three.SheetCopyId);
            state.Submit(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            state.Approve(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            arrange.ApprovalStates.Add(state);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        var approved = await client.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheet-copies/{three.SheetCopyId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(true);
        var approvedBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(HttpStatusCode.Conflict, approved.StatusCode);
        Assert.Equal("err.ECR-DOC-0409.sheetCopyHasWorkflow", approvedBody.GetProperty("messageKey").GetString());
        Assert.Equal("202601", approvedBody.GetProperty("periodKey").ToString());
        Assert.Equal("Approved", approvedBody.GetProperty("workflowStatus").GetString());

        // Видалення копії №2 → копія №3 стає №2.
        var removed = await client.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheet-copies/{two.SheetCopyId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");

        var tables = await TablesAsync(client, s).ConfigureAwait(true);
        Assert.Equal([1, 2], tables.Select(t => t.GetProperty("copyOrdinal").GetInt32()));
        Assert.Equal(three.SheetCopyId, tables[1].GetProperty("sheetCopyId").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Без_права_на_склад_документа_403_а_не_копіюваний_аркуш_422()
    {
        var noRight = await ArrangeAsync("Document.View").ConfigureAwait(true);
        using (var app = new EcrApiFactory(sql))
        {
            using var client = await SignedInAsync(app, noRight.UserName).ConfigureAwait(true);
            var response = await PostCopyAsync(client, noRight).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        }

        var notCopyable = await ArrangeAsync(DocumentSheetCopyHandler.Permission, copyable: false).ConfigureAwait(true);
        using var factory = new EcrApiFactory(sql);
        using var signedIn = await SignedInAsync(factory, notCopyable.UserName).ConfigureAwait(true);
        var refused = await signedIn.PostAsync(
            new Uri($"/api/v1/documents/{notCopyable.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheets/{notCopyable.SheetDefId.ToString(CultureInfo.InvariantCulture)}/copies", UriKind.Relative),
            null).ConfigureAwait(true);
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("err.ECR-DOC-0422.sheetNotCopyable", body.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Журнал_без_sheetCopyId_віддає_лише_оригінал_а_з_ним_лише_свою_копію()
    {
        var s = await ArrangeAsync(DocumentSheetCopyHandler.Permission).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var copy = await PostCopyAsync(client, s).ConfigureAwait(true);

        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var at = new DateTime(2026, 3, 20, 9, 0, 0, DateTimeKind.Utc);
            arrange.ApprovalEvents.Add(new ApprovalEvent(
                s.DocumentId, s.SheetDefId, s.PeriodKey, DocumentStatus.Draft, DocumentStatus.Submitted, ApprovalAction.Submit, null, at));
            arrange.ApprovalEvents.Add(new ApprovalEvent(
                s.DocumentId, s.SheetDefId, s.PeriodKey, DocumentStatus.Draft, DocumentStatus.Submitted, ApprovalAction.Submit, null, at.AddMinutes(1),
                sheetCopyId: copy.SheetCopyId));
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        var path = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/workflow/history?periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}";
        var original = await GetJsonAsync(client, path).ConfigureAwait(true);
        var ofCopy = await GetJsonAsync(client, path + "&sheetCopyId=" + copy.SheetCopyId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);

        var originalEvents = original.EnumerateArray().ToList();
        var copyEvents = ofCopy.EnumerateArray().ToList();
        Assert.Single(originalEvents);
        Assert.Equal(JsonValueKind.Null, originalEvents[0].GetProperty("sheetCopyId").ValueKind);
        Assert.Single(copyEvents);
        Assert.Equal(copy.SheetCopyId, copyEvents[0].GetProperty("sheetCopyId").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Ціль_подання_бере_стани_періодів_із_копії_а_approver_edit_приймає_sheetCopyId()
    {
        var s = await ArrangeAsync(DocumentSheetCopyHandler.Permission).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var copy = await PostCopyAsync(client, s).ConfigureAwait(true);

        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // Проєкт-чернетка: рішення бере ЗБЕРЕЖЕНІ стани, тож період робимо Open, щоб він потрапив до переліку.
            var projectId = await arrange.Documents.Where(d => d.Id == s.DocumentId).Select(d => d.ProjectId).SingleAsync().ConfigureAwait(true);
            var period = await arrange.Periods.SingleAsync(p => p.ProjectId == projectId && p.PeriodKeyValue == s.PeriodKey).ConfigureAwait(true);
            period.AdvanceTo(PeriodState.Open, new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc));
            var state = new ApprovalState(s.DocumentId, s.SheetDefId, s.PeriodKey, copy.SheetCopyId);
            state.Submit(1, new DateTime(2026, 3, 20, 9, 0, 0, DateTimeKind.Utc));
            arrange.ApprovalStates.Add(state);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        var basePath = $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/submission-target?sheetDefId={s.SheetDefId.ToString(CultureInfo.InvariantCulture)}&periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}";
        var original = await GetJsonAsync(client, basePath).ConfigureAwait(true);
        var ofCopy = await GetJsonAsync(client, basePath + "&sheetCopyId=" + copy.SheetCopyId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);

        string StatusOf(JsonElement target) => target.GetProperty("periods").EnumerateArray()
            .Single(p => p.GetProperty("periodKey").GetInt32() == s.PeriodKey).GetProperty("sheetStatus").ToString();

        // Оригінал неподаний (Draft), копія подана: стани періодів не змішуються.
        Assert.NotEqual(StatusOf(original), StatusOf(ofCopy));

        var edit = await GetJsonAsync(
            client,
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/approver-edit?sheetDefId={s.SheetDefId.ToString(CultureInfo.InvariantCulture)}&periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}&sheetCopyId={copy.SheetCopyId.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(true);
        Assert.False(edit.GetProperty("canApproverEdit").GetBoolean());
    }
    /// <summary>
    /// D-3 (аудит 2026-11-10, L5): видалення копії прибирає й історію дозволів копії (<c>doc.PermitSegment</c>,
    /// <c>doc.CellPermitPart</c>) — зовнішнього ключа на копію немає, сироти лишались би. Історія іншої копії — цілою.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація (локально, не комітиться): прибрати блок D-3 з <c>DocumentSheetCopyStore.DeleteCopyAsync</c> — червоніє.
    /// Історія пишеться напряму (не через <c>SaveCellSplits</c>): перевіряється каскад видалення, а не побудова поділу.
    /// Частини копії лежать у двох періодах — доводить, що періоди беруться з самих частин.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-3")]
    public async Task DeleteCopy_removes_permit_history_of_the_copy()
    {
        var s = await ArrangeAsync(DocumentSheetCopyHandler.Permission).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var two = await PostCopyAsync(client, s).ConfigureAwait(true);
        var three = await PostCopyAsync(client, s).ConfigureAwait(true);

        var now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var permitFrom = new DateOnly(2026, 3, 15);
        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var tableDefId = await arrange.TableDefs.Where(t => t.SheetDefId == s.SheetDefId)
                .Select(t => t.Id).FirstAsync().ConfigureAwait(true);
            var columnDefId = await arrange.ColumnDefs.Where(c => c.TableDefId == tableDefId)
                .Select(c => c.Id).FirstAsync().ConfigureAwait(true);
            var userId = await arrange.Users.Where(u => u.UserName == s.UserName).Select(u => u.Id).SingleAsync().ConfigureAwait(true);

            var registry = new RegistryDef(
                EcrCode.Create($"PRMD3_{Guid.NewGuid():N}"[..20]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit D-3" }),
                isTemporal: true);
            arrange.RegistryDefs.Add(registry);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
            var entry = new RegistryEntry(
                registry.Id, EcrCode.Create("D3ENTRY"), new LocalizedText(new Dictionary<string, string> { ["en"] = "D3" }));
            entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
            arrange.RegistryEntries.Add(entry);
            await arrange.SaveChangesAsync().ConfigureAwait(true);

            foreach (var copyId in new[] { two.SheetCopyId, three.SheetCopyId })
            {
                arrange.PermitSegments.Add(PermitSegment.ForRow(
                    s.DocumentId, tableDefId, copyId, "r1", columnDefId, permitFrom, entry.Id, userId, now));
                foreach (var period in new[] { 202603, 202604 })
                {
                    arrange.CellPermitParts.Add(new CellPermitPart(
                        period, s.DocumentId, tableDefId, copyId, "r1", columnDefId, 1, entry.Id,
                        new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14), 5m));
                    arrange.CellPermitParts.Add(new CellPermitPart(
                        period, s.DocumentId, tableDefId, copyId, "r1", columnDefId, 2, entry.Id,
                        permitFrom, new DateOnly(2026, 3, 31), 6m));
                }
            }

            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        var removed = await client.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheet-copies/{two.SheetCopyId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");

        await using var verify = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(0, await verify.PermitSegments.CountAsync(x => x.SheetCopyId == two.SheetCopyId).ConfigureAwait(true));
        Assert.Equal(0, await verify.CellPermitParts.CountAsync(x => x.SheetCopyId == two.SheetCopyId).ConfigureAwait(true));
        Assert.Equal(1, await verify.PermitSegments.CountAsync(x => x.SheetCopyId == three.SheetCopyId).ConfigureAwait(true));
        Assert.Equal(4, await verify.CellPermitParts.CountAsync(x => x.SheetCopyId == three.SheetCopyId).ConfigureAwait(true));
    }

    private static async Task<(HttpStatusCode Status, long SheetCopyId, int Ordinal)> PostCopyAsync(
        HttpClient client, Scenario s)
    {
        var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheets/{s.SheetDefId.ToString(CultureInfo.InvariantCulture)}/copies", UriKind.Relative),
            null).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            return (response.StatusCode, 0, 0);
        }

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

        return (response.StatusCode, body.GetProperty("sheetCopyId").GetInt64(), body.GetProperty("ordinal").GetInt32());
    }

    private static async Task<List<JsonElement>> TablesAsync(HttpClient client, Scenario s)
    {
        var json = await GetJsonAsync(
            client,
            $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables?periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);

        return [.. json.EnumerateArray()];
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
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

    /// <summary>Документ з оригіналом аркуша у складі, аркуш копіюваний, користувач із правом <paramref name="permission"/>.</summary>
    private async Task<Scenario> ArrangeAsync(string permission, bool copyable = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 202603, columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var sheet = await db.SheetDefs.SingleAsync(x => x.Id == document.SheetDefId).ConfigureAwait(false);
        sheet.SetCopyable(copyable);

        var original = new DocumentSheet(document.DocumentId, document.SheetDefId);
        db.DocumentSheets.Add(original);

        var userName = $"sheetcopy_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"SHEETCOPY_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet copies" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Для читання таблиць і документа потрібне й Document.View.
        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        if (permission != "Document.View")
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var english = sheet.NameL10n.Values.TryGetValue("en", out var en) && !string.IsNullOrEmpty(en) ? en : sheet.Code;

        return new Scenario(
            document.DocumentId, document.SheetDefId, document.SheetCode, original.Id, document.PeriodKey.Value, userName,
            english);
    }

    private sealed record Scenario(
        long DocumentId, int SheetDefId, string SheetCode, long OriginalId, int PeriodKey, string UserName,
        string SheetEnglishName);
}
