// tests/Ecr.Api.Tests/SheetChangesApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Маркер змін аркуша (живе оновлення сітки, LIVE-REFRESH B1) наскрізно через HTTP: семантика маркера на справжньому
/// <c>PATCH …/cells</c> (своє не будить, чуже будить з автором і таблицею) і доступ (схована таблиця, схований аркуш,
/// чужий документ).
/// </summary>
/// <remarks>
/// Мутаційний доказ доступу: у <c>DocumentReadScope.VisibleTablesOfSheet</c> замінити <c>tables.Where(CanReadTable)</c>
/// на <c>tables</c> → <c>Схована_таблиця_не_потрапляє_в_зміни_а_схований_аркуш_404</c> червоніє (Denied бачить зміну
/// схованої таблиці).
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetChangesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-SheetChanges-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Своя_правка_лише_пересуває_маркер_а_чужа_будить_з_автором_і_таблицею()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app, s.Alice.UserName).ConfigureAwait(true);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);

        // Перше опитування: лише маркер.
        var first = await ChangesAsync(bob, s, since: null).ConfigureAwait(true);
        Assert.False(first.GetProperty("changed").GetBoolean());
        Assert.Empty(first.GetProperty("changedTables").EnumerateArray());
        var m0 = first.GetProperty("marker").GetString()!;

        // Нічого не змінилося — changed = false, маркер не відходить назад.
        var idle = await ChangesAsync(bob, s, m0).ConfigureAwait(true);
        Assert.False(idle.GetProperty("changed").GetBoolean(), idle.ToString());
        Assert.Empty(idle.GetProperty("changedTables").EnumerateArray());

        // Alice записує комірку справжнім PATCH.
        var (column, versions) = await OpenSliceAsync(alice, s, app).ConfigureAwait(true);
        var rowKey = versions.Keys.Order(StringComparer.Ordinal).First();
        await PatchAsync(alice, s, rowKey, versions[rowKey], column, "41.5", app).ConfigureAwait(true);

        // Для самої Alice — своє: банера немає, маркер пересунувся.
        var own = await ChangesAsync(alice, s, m0).ConfigureAwait(true);
        Assert.False(own.GetProperty("changed").GetBoolean(), own.ToString());
        Assert.NotEqual(m0, own.GetProperty("marker").GetString());
        Assert.False((await ChangesAsync(alice, s, own.GetProperty("marker").GetString()).ConfigureAwait(true)).GetProperty("changed").GetBoolean());

        // Для Боба — чуже: автор, час і таблиця з екземпляром.
        var foreign = await ChangesAsync(bob, s, m0).ConfigureAwait(true);
        Assert.True(foreign.GetProperty("changed").GetBoolean(), foreign.ToString());
        Assert.Equal(s.Alice.Id, foreign.GetProperty("lastChangedByUserId").GetInt32());
        Assert.Equal(s.Alice.UserName, foreign.GetProperty("lastChangedBy").GetString());
        Assert.Equal(JsonValueKind.String, foreign.GetProperty("lastChangedAt").ValueKind);
        var table = Assert.Single(foreign.GetProperty("changedTables").EnumerateArray());
        Assert.Equal(s.Table1Code, table.GetProperty("tableCode").GetString());
        Assert.Equal(s.Table1InstanceId, table.GetProperty("tableInstanceId").GetInt64());

        // Боб підхопив новий маркер — далі тиша.
        var m1 = foreign.GetProperty("marker").GetString()!;
        Assert.False((await ChangesAsync(bob, s, m1).ConfigureAwait(true)).GetProperty("changed").GetBoolean());

        // Тепер пише Боб: для нього — своє, для Alice (з маркером до його правки) — чуже від Боба.
        var aliceMarker = (await ChangesAsync(alice, s, null).ConfigureAwait(true)).GetProperty("marker").GetString();
        var (_, fresh) = await OpenSliceAsync(bob, s, app).ConfigureAwait(true);
        await PatchAsync(bob, s, rowKey, fresh[rowKey], column, "42", app).ConfigureAwait(true);

        Assert.False((await ChangesAsync(bob, s, m1).ConfigureAwait(true)).GetProperty("changed").GetBoolean());
        var byBob = await ChangesAsync(alice, s, aliceMarker).ConfigureAwait(true);
        Assert.True(byBob.GetProperty("changed").GetBoolean());
        Assert.Equal(s.Bob.Id, byBob.GetProperty("lastChangedByUserId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Схована_таблиця_не_потрапляє_в_зміни_а_схований_аркуш_404()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var bob = await SignedInAsync(app, s.Bob.UserName).ConfigureAwait(true);
        using var denied = await SignedInAsync(app, s.TableDenied.UserName).ConfigureAwait(true);
        using var sheetDenied = await SignedInAsync(app, s.SheetDenied.UserName).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, s.Stranger.UserName).ConfigureAwait(true);

        var bobMarker = (await ChangesAsync(bob, s, null).ConfigureAwait(true)).GetProperty("marker").GetString();
        var deniedMarker = (await ChangesAsync(denied, s, null).ConfigureAwait(true)).GetProperty("marker").GetString();

        // Alice змінює рядок СХОВАНОЇ від Denied таблиці (дотик рядка + запис журналу — те саме, що робить PATCH).
        await ForeignEditAsync(s, s.Table2RowId, s.Table2ColumnId, s.Alice.Id).ConfigureAwait(true);

        var forDenied = await ChangesAsync(denied, s, deniedMarker).ConfigureAwait(true);
        Assert.False(forDenied.GetProperty("changed").GetBoolean(), forDenied.ToString());
        Assert.Empty(forDenied.GetProperty("changedTables").EnumerateArray());
        Assert.DoesNotContain(s.Table2Code, forDenied.ToString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, forDenied.GetProperty("lastChangedByUserId").ValueKind);

        var forBob = await ChangesAsync(bob, s, bobMarker).ConfigureAwait(true);
        Assert.True(forBob.GetProperty("changed").GetBoolean());
        Assert.Equal(s.Table2Code, Assert.Single(forBob.GetProperty("changedTables").EnumerateArray()).GetProperty("tableCode").GetString());

        // Рядок змінено без журналу значень (наприклад, видалення рядка) — чуже з невідомим автором.
        var quiet = (await ChangesAsync(bob, s, null).ConfigureAwait(true)).GetProperty("marker").GetString();
        await TouchRowAsync(s, s.Table1RowId).ConfigureAwait(true);
        var unknown = await ChangesAsync(bob, s, quiet).ConfigureAwait(true);
        Assert.True(unknown.GetProperty("changed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("lastChangedByUserId").ValueKind);
        Assert.Equal(s.Table1Code, Assert.Single(unknown.GetProperty("changedTables").EnumerateArray()).GetProperty("tableCode").GetString());

        // Схований аркуш і чужий документ — та сама 404, що й неіснуючий документ.
        var hidden = await sheetDenied.GetAsync(Uri(s, s.DocumentId, s.SheetDefId, null)).ConfigureAwait(true);
        var missing = await bob.GetAsync(Uri(s, s.DocumentId + 100_000, s.SheetDefId, null)).ConfigureAwait(true);
        var unknownSheet = await bob.GetAsync(Uri(s, s.DocumentId, s.SheetDefId + 100_000, null)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownSheet.StatusCode);
        Assert.Equal(await KeyAsync(missing).ConfigureAwait(true), await KeyAsync(hidden).ConfigureAwait(true));
        Assert.False((await stranger.GetAsync(Uri(s, s.DocumentId, s.SheetDefId, null)).ConfigureAwait(true)).IsSuccessStatusCode);

        // Зіпсований маркер — як перше опитування; недійсний період — 422.
        var broken = await ChangesAsync(bob, s, "not-a-marker").ConfigureAwait(true);
        Assert.False(broken.GetProperty("changed").GetBoolean());
        var badPeriod = await bob.GetAsync(new Uri(
            $"/api/v1/documents/{Num(s.DocumentId)}/sheets/{Num(s.SheetDefId)}/changes?periodKey=0", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPeriod.StatusCode);
    }

    // ── помічники ───────────────────────────────────────────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static Uri Uri(Scenario s, long documentId, int sheetDefId, string? since)
        => new(
            $"/api/v1/documents/{Num(documentId)}/sheets/{Num(sheetDefId)}/changes?periodKey={Num(s.Period)}"
            + (since is null ? string.Empty : "&since=" + System.Uri.EscapeDataString(since)),
            UriKind.Relative);

    private static async Task<JsonElement> ChangesAsync(HttpClient client, Scenario s, string? since)
    {
        var response = await client.GetAsync(Uri(s, s.DocumentId, s.SheetDefId, since)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> KeyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("messageKey").GetString();

    private static async Task<(string Column, Dictionary<string, string> Versions)> OpenSliceAsync(
        HttpClient client, Scenario s, EcrApiFactory app)
    {
        var opened = await client.GetAsync(new Uri(
            $"/api/v1/documents/{Num(s.DocumentId)}/tables/{Num(s.Table1InstanceId)}", UriKind.Relative)).ConfigureAwait(false);
        var body = await opened.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}\n{body}\n{app.ErrorsText}");

        var root = JsonDocument.Parse(body).RootElement;
        var column = root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).Last();
        var versions = root.GetProperty("rows").EnumerateArray().ToDictionary(
            r => r.GetProperty("rowKey").GetString()!, r => r.GetProperty("rowVersion").GetString()!, StringComparer.Ordinal);
        return (column, versions);
    }

    private static async Task PatchAsync(
        HttpClient client, Scenario s, string rowKey, string baseVersion, string columnCode, string value, EcrApiFactory app)
    {
        var response = await client.PatchAsJsonAsync(new Uri($"/api/v1/documents/{Num(s.DocumentId)}/cells", UriKind.Relative), new
        {
            tableInstanceId = s.Table1InstanceId,
            periodKey = s.Period,
            origin = "UserEdit",
            rows = new[] { new { rowKey, baseVersion, cells = new[] { new { columnCode, value = (object)value } } } },
        }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"PATCH: {response.StatusCode}\n{body}\n{app.ErrorsText}");
    }

    /// <summary>Чужа правка без HTTP: «дотик» рядка (нова rowversion) і запис журналу значень від <paramref name="userId"/>.</summary>
    private async Task ForeignEditAsync(Scenario s, long rowId, int columnId, int userId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = {s.Period} AND Id = {rowId};
            INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                        ChangedByUserId, Origin, IsLateEdit)
            VALUES (SYSUTCDATETIME(), {s.Period}, {s.DocumentId}, {rowId}, N'R', {columnId}, NULL, N'7', {userId}, N'UserEdit', 0);
            """).ConfigureAwait(false);
    }

    private async Task TouchRowAsync(Scenario s, long rowId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = {s.Period} AND Id = {rowId};").ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Документ поточного періоду (відкритий), аркуш із двома таблицями: перша — для справжнього PATCH, друга — та,
    /// яку ховає заборона на таблицю. Користувачі: двоє з <c>Write</c>, заборона на таблицю, заборона на аркуш, без гранта.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: 2, rowMode: TableRowMode.Mixed).ConfigureAwait(false);
        var second = await builder.AddTableAsync(document, TableLayoutKind.PerPeriodInstance, monthColumns: 0).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var now = DateTime.UtcNow;
        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var codes = await db.TableDefs
            .Where(t => t.Id == document.TableDefId || t.Id == second.TableDefId)
            .ToDictionaryAsync(t => t.Id, t => t.Code).ConfigureAwait(false);

        async Task<Person> NewUserAsync(string prefix, string[] permissions, ResourceKind? deny = null, int denyId = 0, bool grant = true)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(
                EcrCode.Create($"SC_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet changes test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (grant)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
            }

            if (deny is { } kind)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, denyId, GrantLevel.None, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return new Person(user.Id, userName);
        }

        string[] view = ["Document.View"];
        return new Scenario(
            document.DocumentId, periodKey, document.SheetDefId,
            codes[document.TableDefId], document.TableInstanceId, document.RowIds[0],
            codes[second.TableDefId], second.RowIds[0], second.ColumnDefIds[^1],
            Alice: await NewUserAsync("sca", view).ConfigureAwait(false),
            Bob: await NewUserAsync("scb", view).ConfigureAwait(false),
            TableDenied: await NewUserAsync("sct", view, ResourceKind.Table, second.TableDefId).ConfigureAwait(false),
            SheetDenied: await NewUserAsync("scs", view, ResourceKind.Sheet, document.SheetDefId).ConfigureAwait(false),
            Stranger: await NewUserAsync("scx", [], grant: false).ConfigureAwait(false));
    }

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, int Period, int SheetDefId,
        string Table1Code, long Table1InstanceId, long Table1RowId,
        string Table2Code, long Table2RowId, int Table2ColumnId,
        Person Alice, Person Bob, Person TableDenied, Person SheetDenied, Person Stranger);
}
