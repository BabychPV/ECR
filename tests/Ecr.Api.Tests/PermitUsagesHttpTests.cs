// tests/Ecr.Api.Tests/PermitUsagesHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// PERMUSE: <c>GET /api/v1/permits/{id}/usages</c> («де використано» дозвіл), лічильник <c>usedInDocumentCount</c> у переліку
/// дозволів і вибір дозволу в колонці <c>Lookup</c> таблиці документа (приховані реєстри типів дозволів як ціль).
/// </summary>
/// <remarks>
/// Мутаційні докази: (1) у <c>PermitStore.FindUsagesAsync</c> прибрати <c>where !r.IsDeleted</c> в запиті комірок - червоніє
/// «Використання_шапки_і_рядків…» (логічно видалений рядок попадає у відповідь); (2) прибрати фільтр
/// <c>projects.Contains(d.ProjectId)</c> - червоніє «Читач_без_доступу_до_документів…» (чужий документ видно); (3) прибрати
/// шапкову гілку - зникає перший елемент і лічильник.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitUsagesHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Permit-Usage-2026!";
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Використання_шапки_і_рядків_рахується_по_документах_і_періодах_без_видалених_рядків()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var usages = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);

        // Один документ, три місця: поле шапки + дві комірки колонки (поточний і попередній період). Видалений рядок не рахується.
        Assert.Equal((s.Active, 1, 3, false), (
            usages.GetProperty("permitId").GetInt64(),
            usages.GetProperty("documentCount").GetInt32(),
            usages.GetProperty("linkCount").GetInt32(),
            usages.GetProperty("truncated").GetBoolean()));
        var items = usages.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var header = items[0];
        Assert.Equal("header", header.GetProperty("kind").GetString());
        Assert.Equal("PERMIT", header.GetProperty("fieldCode").GetString());
        Assert.Equal(s.Document.DocumentId, header.GetProperty("documentId").GetInt64());
        Assert.Equal(JsonValueKind.Null, header.GetProperty("periodKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, header.GetProperty("tableCode").ValueKind);

        var cells = items.Skip(1).ToList();
        Assert.All(cells, c => Assert.Equal("cell", c.GetProperty("kind").GetString()));
        Assert.Equal([s.PreviousPeriod, s.Period], cells.Select(c => c.GetProperty("periodKey").GetInt32()).ToList());
        Assert.All(cells, c => Assert.Equal(s.PermitColumnCode, c.GetProperty("fieldCode").GetString()));
        Assert.All(cells, c => Assert.Equal(s.TableCode, c.GetProperty("tableCode").GetString()));
        Assert.All(cells, c => Assert.Equal(s.SheetCode, c.GetProperty("sheetCode").GetString()));
        Assert.All(cells, c => Assert.Equal(s.FirstRowKey, c.GetProperty("rowKey").GetString()));

        // Протермінований дозвіл (історичне посилання) теж показується: одна комірка, шапки немає.
        var expired = await GetAsync(client, app, $"/api/v1/permits/{s.Expired}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1), (expired.GetProperty("documentCount").GetInt32(), expired.GetProperty("linkCount").GetInt32()));

        // Ніде не використаний.
        var unused = await GetAsync(client, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (unused.GetProperty("documentCount").GetInt32(), unused.GetProperty("linkCount").GetInt32()));
        Assert.Empty(unused.GetProperty("items").EnumerateArray());

        // limit обрізає перелік, лічильники лишаються повними.
        var limited = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?limit=1").ConfigureAwait(true);
        Assert.Equal((3, true, 1), (
            limited.GetProperty("linkCount").GetInt32(),
            limited.GetProperty("truncated").GetBoolean(),
            limited.GetProperty("items").GetArrayLength()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Фільтр_періоду_лишає_комірки_періоду_і_шапку_документів_з_таблицями_в_ньому()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Writer).ConfigureAwait(true);

        var current = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?periodKey={s.Period}").ConfigureAwait(true);
        Assert.Equal(["header", "cell"], current.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToList());
        Assert.Equal(
            [s.Period],
            current.GetProperty("items").EnumerateArray().Skip(1).Select(i => i.GetProperty("periodKey").GetInt32()).ToList());

        var previous = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?periodKey={s.PreviousPeriod}").ConfigureAwait(true);
        Assert.Equal(2, previous.GetProperty("linkCount").GetInt32());

        // Період, у якому документ не має таблиць: нічого.
        var none = await GetAsync(client, app, $"/api/v1/permits/{s.Active}/usages?periodKey=209912").ConfigureAwait(true);
        Assert.Equal(0, none.GetProperty("linkCount").GetInt32());

        var bad = await client.GetAsync(new Uri($"/api/v1/permits/{s.Active}/usages?periodKey=7", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Читач_без_доступу_до_документів_не_бачить_їх_у_використанні_а_без_права_Registry_View_отримує_403()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var hidden = await GetAsync(outsider, app, $"/api/v1/permits/{s.Active}/usages").ConfigureAwait(true);
        Assert.Equal((0, 0), (hidden.GetProperty("documentCount").GetInt32(), hidden.GetProperty("linkCount").GetInt32()));
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());

        using var nobody = await SignedInAsync(app, s.NoRegistry).ConfigureAwait(true);
        var forbidden = await nobody.GetAsync(new Uri($"/api/v1/permits/{s.Active}/usages", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var missing = await writer.GetAsync(new Uri("/api/v1/permits/2000000000/usages", UriKind.Relative)).ConfigureAwait(true);
        var body = await missing.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(missing.StatusCode == HttpStatusCode.NotFound, $"{missing.StatusCode}: {body}");
        Assert.Equal("ECR-PRM-0404", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Перелік_дозволів_віддає_у_скількох_документах_використано_кожен()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var list = await GetAsync(writer, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.Equal(
            new Dictionary<long, int> { [s.Active] = 1, [s.Expired] = 1, [s.Unused] = 0 },
            list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt64(), i => i.GetProperty("usedInDocumentCount").GetInt32()));

        // Читач без доступу до документів бачить нулі: лічильник не розкриває чужі документи.
        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var blind = await GetAsync(outsider, app, $"/api/v1/permits?type={s.TypeCode}").ConfigureAwait(true);
        Assert.All(blind.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));
    }

    /// <remarks>
    /// Мутації: у <c>PermitStore.CountTypeUsedInDocumentsAsync</c> прибрати фінальний <c>.Distinct()</c> пар (тип, документ) —
    /// writer отримує 2 замість 1 (шапка + комірки одного документа); у обробниках прибрати умову <c>readable.Count &gt; 0</c>
    /// разом із фільтром проєктів — outsider бачить 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Тип_дозволу_віддає_у_скількох_видимих_документах_використано_його_дозволи()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Один документ: шапка (active), комірки active/expired у двох періодах — рахується ОДИН документ.
        using var writer = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var usage = await GetAsync(writer, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(1, usage.GetProperty("usedInDocumentCount").GetInt32());
        var list = await GetAsync(writer, app, $"/api/v1/permit-types?q={s.TypeCode}").ConfigureAwait(true);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("code").GetString() == s.TypeCode);
        Assert.Equal(1, item.GetProperty("usedInDocumentCount").GetInt32());

        // Читач без Document.View у проєкті бачить 0: лічильник не розкриває чужі документи.
        using var outsider = await SignedInAsync(app, s.Outsider).ConfigureAwait(true);
        var blind = await GetAsync(outsider, app, $"/api/v1/permit-types/{s.TypeCode}/usage").ConfigureAwait(true);
        Assert.Equal(0, blind.GetProperty("usedInDocumentCount").GetInt32());
        var blindList = await GetAsync(outsider, app, $"/api/v1/permit-types?q={s.TypeCode}").ConfigureAwait(true);
        Assert.All(blindList.GetProperty("items").EnumerateArray(), i => Assert.Equal(0, i.GetProperty("usedInDocumentCount").GetInt32()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Колонка_Lookup_на_прихований_реєстр_дозволів_приймає_дозвіл_як_поле_шапки_і_відхиляє_протермінований_та_чужий()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Writer).ConfigureAwait(true);
        var patchUri = new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative);

        async Task<HttpResponseMessage> PatchAsync(string rowKey, long entryId)
            => await client.PatchAsJsonAsync(patchUri, new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.Period,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = s.PermitColumnCode, value = (object)entryId } },
                    },
                },
            }).ConfigureAwait(true);

        // Чинний дозвіл - вибір приймається (колонка вказує на прихований реєстр шапки типу).
        var accepted = await PatchAsync($"NEW{Guid.NewGuid():N}"[..12], s.Unused).ConfigureAwait(true);
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"{accepted.StatusCode}: {await accepted.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        // ...і він одразу видно в «де використано».
        var usages = await GetAsync(client, app, $"/api/v1/permits/{s.Unused}/usages").ConfigureAwait(true);
        Assert.Equal((1, 1), (usages.GetProperty("documentCount").GetInt32(), usages.GetProperty("linkCount").GetInt32()));
        Assert.Equal("cell", usages.GetProperty("items")[0].GetProperty("kind").GetString());

        // Протермінований на кінець періоду - відмова тим самим кодом, що й для вибору в списку.
        var expired = await PatchAsync($"NEW{Guid.NewGuid():N}"[..12], s.Expired).ConfigureAwait(true);
        var expiredBody = await expired.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(expired.StatusCode == HttpStatusCode.UnprocessableEntity, $"{expired.StatusCode}: {expiredBody}");
        Assert.Equal("ECR-CELL-4223", JsonDocument.Parse(expiredBody).RootElement.GetProperty("errorCode").GetString());

        // Запис чужого довідника - так само відмова.
        var foreign = await PatchAsync($"NEW{Guid.NewGuid():N}"[..12], s.Foreign).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> GetAsync(HttpClient client, EcrApiFactory app, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static User NewUser(Ecr.Infrastructure.Persistence.EcrDbContext db, string prefix, out string name)
    {
        name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        return user;
    }

    /// <summary>
    /// Документ з шапкою <c>PERMIT</c> і колонкою <c>Lookup</c> на прихований реєстр шапки опублікованого типу дозволів; чинний,
    /// протермінований і невикористаний дозвіл; чужий запис; три користувачі.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var period = (siteToday.Year * 100) + siteToday.Month;
        var previousMonth = siteToday.AddMonths(-1);
        var previous = (previousMonth.Year * 100) + previousMonth.Month;

        var document = await builder.BuildAsync(period, columnCount: 1, rowCount: 3, rowMode: TableRowMode.Mixed).ConfigureAwait(false);
        var previousInstance = await builder.AddPeriodInstanceAsync(document, new PeriodKey(previous)).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = DateTime.UtcNow;

        // Тип дозволів: прихований реєстр шапки (PRM_…) - ціль Lookup; чужий довідник - для відмови.
        var header = new RegistryDef(EcrCode.Create($"PRM_PU{tag}"), Text("Permit header"), isTemporal: true);
        var other = new RegistryDef(EcrCode.Create($"OTHER_{tag}"), Text("Other"), isTemporal: false);
        db.RegistryDefs.AddRange(header, other);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var type = new PermitTypeDef(EcrCode.Create($"PU{tag}"), Text("Usage type"), PermitCategory.Air, header.Id);
        db.PermitTypeDefs.Add(type);
        await db.SaveChangesAsync().ConfigureAwait(false);
        type.Publish(1, now);

        RegistryEntry Entry(RegistryDef def, string code, DateOnly from, DateOnly to)
        {
            var entry = new RegistryEntry(def.Id, EcrCode.Create(code), Text(code), 1, now);
            entry.SetValidity(from, to);
            db.RegistryEntries.Add(entry);
            return entry;
        }

        var active = Entry(header, $"ACT{tag}", new DateOnly(2020, 1, 1), new DateOnly(2099, 1, 1));
        var expired = Entry(header, $"EXP{tag}", new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1));
        var unused = Entry(header, $"UNU{tag}", new DateOnly(2020, 1, 1), new DateOnly(2099, 1, 1));
        var foreign = Entry(other, $"FOR{tag}", new DateOnly(2020, 1, 1), new DateOnly(2099, 1, 1));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Структура шаблону: поле шапки та колонка Lookup на реєстр шапки типу.
        var headerField = new HeaderFieldDef(document.TemplateVersionId, EcrCode.Create("PERMIT"), Text("Permit"), 1, CellDataType.Lookup);
        headerField.SetLookup(header.Id);
        db.HeaderFieldDefs.Add(headerField);
        var column = new ColumnDef(document.TableDefId, EcrCode.Create($"PCOL{tag}"), Text("Permit"), 2, CellDataType.Lookup);
        column.SetLookup(header.Id);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Дані: шапка -> active; поточний період: рядок 1 -> active, рядок 2 -> expired, рядок 3 (ВИДАЛЕНИЙ) -> active;
        // попередній період: рядок 1 -> active.
        db.DocumentHeaderValues.Add(new DocumentHeaderValue(
            document.DocumentId, headerField.Id, new DocumentHeaderValueData { ValueRegistryEntryId = active.Id }));

        void Cell(int periodKey, long rowId, long entryId)
            => db.CellValues.Add(new CellValue(
                new CellAddress(new PeriodKey(periodKey), rowId, column.Id), document.TableDefId,
                new CellValueData { ValueRegistryEntryId = entryId }));

        Cell(period, document.RowIds[0], active.Id);
        Cell(period, document.RowIds[1], expired.Id);
        Cell(period, document.RowIds[2], active.Id);
        Cell(previous, previousInstance.RowIds[0], active.Id);
        var deletedRow = await db.TableRows
            .SingleAsync(r => r.PeriodKeyValue == period && r.Id == document.RowIds[2]).ConfigureAwait(false);
        deletedRow.SoftDelete(now);

        var firstRowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period && r.Id == document.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        var tableCode = await db.TableDefs.AsNoTracking().Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        // Користувачі: writer (читає й пише документ, бачить дозволи), outsider (дозволи без доступу до проєкту),
        // noRegistry (документ без права читати довідники).
        var writer = NewUser(db, "pu_w", out var writerName);
        var outsider = NewUser(db, "pu_o", out var outsiderName);
        var noRegistry = NewUser(db, "pu_n", out var noRegistryName);
        var role = new Role(EcrCode.Create($"PU_{Guid.NewGuid():N}"), Text("Permit usage"));
        var viewOnly = new Role(EcrCode.Create($"PUV_{Guid.NewGuid():N}"), Text("Permit view only"));
        var docOnly = new Role(EcrCode.Create($"PUD_{Guid.NewGuid():N}"), Text("Document only"));
        db.Roles.AddRange(role, viewOnly, docOnly);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.AddRange(
            new RolePermission(role.Id, "Registry.View"), new RolePermission(role.Id, "Document.View"),
            new RolePermission(viewOnly.Id, "Registry.View"), new RolePermission(docOnly.Id, "Document.View"));
        db.RoleAssignments.AddRange(
            new RoleAssignment(role.Id, writer.Id, null), new RoleAssignment(viewOnly.Id, outsider.Id, null),
            new RoleAssignment(docOnly.Id, noRegistry.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        // Проєкт активний, поточний період відкритий (як у CellWriteRoundTripTests): інакше запис комірки законно відмовляє.
        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var currentPeriod = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == period).ConfigureAwait(false);
        currentPeriod.RecomputeBoundaries(policy, SiteZone);
        currentPeriod.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document, document.SheetCode, tableCode, column.Code, firstRowKey, period, previous, type.Code,
            active.Id, expired.Id, unused.Id, foreign.Id, writerName, outsiderName, noRegistryName);
    }

    private sealed record Scenario(
        TestDocument Document, string SheetCode, string TableCode, string PermitColumnCode, string FirstRowKey,
        int Period, int PreviousPeriod, string TypeCode, long Active, long Expired, long Unused, long Foreign,
        string Writer, string Outsider, string NoRegistry);
}
