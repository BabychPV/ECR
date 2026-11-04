// tests/Ecr.Api.Tests/DocumentTemplateVersionListApiTests.cs
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
/// Сортування й фільтр переліку документів за версією шаблону (<c>sort=templateVersion</c>, <c>templateVersions=…</c>)
/// наскрізно через HTTP: порядок рахується СЕРВЕРОМ по всьому набору (а не по завантаженій сторінці), сторінкування
/// детерміноване, а фільтр і сортування не додають документів, яких читач не бачить.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, не в коміті): у <c>DocumentStore.ListAsync</c> замінити <c>VisibleDocuments(...)</c> в
/// <c>Build</c> на <c>db.Documents</c> → червоніє <c>Чужі_документи_не_потрапляють_ні_у_фільтр_ні_у_сортування</c>
/// (у сторінках з'являються документи проєкту C, на який у читача немає гранта).
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentTemplateVersionListApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Version-List-2026!";

    // Людський порядок: 2.9.0 < 2.10.0 < 3.0.0 (текстовий дав би 2.10.0 першим).
    private const string VersionA = "2.9.0";
    private const string VersionB = "2.10.0";
    private const string VersionC = "3.0.0";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сортування_за_версією_зростання_і_спадання_по_всіх_сторінках()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        // Сторінки по 2 - порядок має скластися з УСІХ сторінок, а не з однієї завантаженої.
        var asc = await AllPagesAsync(app, client, s, "&sort=templateVersion", 2).ConfigureAwait(true);
        Assert.Equal(s.AscendingIds, asc);

        var desc = await AllPagesAsync(app, client, s, "&sort=-templateVersion", 2).ConfigureAwait(true);
        Assert.Equal(s.DescendingIds, desc);

        // Без повторів і пропусків на межах сторінок, і розмір сторінки інший - той самий результат.
        Assert.Equal(asc.Count, asc.Distinct().Count());
        Assert.Equal(s.AscendingIds, await AllPagesAsync(app, client, s, "&sort=templateVersion", 4).ConfigureAwait(true));

        // Без сортування - за Id, як і раніше.
        var plain = await AllPagesAsync(app, client, s, string.Empty, 50).ConfigureAwait(true);
        Assert.Equal(s.AllIds.Order().ToList(), plain);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_templateVersions_віддає_лише_документи_цих_версій_і_поєднується_з_іншими()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        Assert.Equal(s.IdsOf(s.ProjectA), await AllPagesAsync(app, client, s, $"&templateVersions={VersionA}", 2).ConfigureAwait(true));

        // Кілька версій; пробіли, дублі й порожні елементи ігноруються. Разом із сортуванням - порядок версій.
        var two = await AllPagesAsync(app, client, s, $"&templateVersions={VersionC}, {VersionB},,{VersionC}&sort=-templateVersion", 2)
            .ConfigureAwait(true);
        Assert.Equal([.. s.IdsOf(s.ProjectC), .. s.IdsOf(s.ProjectB)], two);

        // Невідома версія нічого не знаходить (а не 422).
        Assert.Empty(await AllPagesAsync(app, client, s, "&templateVersions=9.9.9", 2).ConfigureAwait(true));

        // З фільтром за проєктом і пошуком за ключем: версія C + проєкт A - порожньо; версія B + ключ другого документа B.
        Assert.Empty(await AllPagesAsync(app, client, s, $"&templateVersions={VersionC}&projectId={Num(s.ProjectA)}", 2).ConfigureAwait(true));
        var keyed = await AllPagesAsync(
            app, client, s, $"&templateVersions={VersionB}&q={Uri.EscapeDataString(s.KeyOf(s.IdsOf(s.ProjectB)[1]))}&sort=templateVersion", 2)
            .ConfigureAwait(true);
        Assert.Equal([s.IdsOf(s.ProjectB)[1]], keyed);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_береться_за_період_як_у_колонці()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        // Проєкт B з 202602 переходить на 4.0.0 (шкала з двох сегментів); за 202601 - типова 2.10.0.
        Assert.Equal(
            s.IdsOf(s.ProjectB),
            await AllPagesAsync(app, client, s, $"&templateVersions={VersionB}", 5, periodKey: 202601).ConfigureAwait(true));
        Assert.Empty(await AllPagesAsync(app, client, s, $"&templateVersions={VersionB}", 5, periodKey: 202602).ConfigureAwait(true));
        Assert.Equal(
            s.IdsOf(s.ProjectB),
            await AllPagesAsync(app, client, s, "&templateVersions=4.0.0", 5, periodKey: 202602).ConfigureAwait(true));

        // Сортування за 202602: 2.9.0 (A) < 3.0.0 (C) < 4.0.0 (B).
        Assert.Equal(
            [.. s.IdsOf(s.ProjectA), .. s.IdsOf(s.ProjectC), .. s.IdsOf(s.ProjectB)],
            await AllPagesAsync(app, client, s, "&sort=templateVersion", 2, periodKey: 202602).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чужі_документи_не_потрапляють_ні_у_фільтр_ні_у_сортування()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        // Грант лише на A і B; проєкт C (версія 3.0.0) читачеві закритий.
        var viewer = await NewUserAsync(s.ProjectA, s.ProjectB).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        var sorted = await AllPagesAsync(app, client, s, "&sort=-templateVersion", 2).ConfigureAwait(true);
        Assert.Equal([.. s.IdsOf(s.ProjectB), .. s.IdsOf(s.ProjectA)], sorted);
        Assert.DoesNotContain(sorted, id => s.IdsOf(s.ProjectC).Contains(id));

        // ⛔ Межа видимості діє В ЗАПИТІ, а не лише постфільтром обробника: перша сторінка повна (2 з 2) і не починається
        // з порожньої «з'їденої» сторінки закритих документів C (версія 3.0.0 - перша в спаданні).
        var first = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents?limit=2&periodKey={Num(s.PeriodKey)}&sort=-templateVersion", UriKind.Relative)).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(
            s.IdsOf(s.ProjectB).Take(2).ToList(),
            [.. first.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt64())]);

        // ⛔ Фільтр за версією закритого проєкту нічого не підтверджує: порожньо, як і для невідомої версії.
        Assert.Empty(await AllPagesAsync(app, client, s, $"&templateVersions={VersionC}", 2).ConfigureAwait(true));
        Assert.Equal(
            s.IdsOf(s.ProjectA),
            await AllPagesAsync(app, client, s, $"&templateVersions={VersionA},{VersionC}", 2).ConfigureAwait(true));

        // Контроль сценарію: з грантом на C ті самі запити його бачать.
        var full = await NewUserAsync(s.ProjectA, s.ProjectB, s.ProjectC).ConfigureAwait(true);
        using var fullClient = await SignedInAsync(app, full).ConfigureAwait(true);
        Assert.Equal(
            s.IdsOf(s.ProjectC),
            await AllPagesAsync(app, fullClient, s, $"&templateVersions={VersionC}", 2).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Недопустимі_sort_і_templateVersions_422_з_наявним_ключем()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var viewer = await NewUserAsync(s.ProjectA).ConfigureAwait(true);
        using var client = await SignedInAsync(app, viewer).ConfigureAwait(true);

        await RefusedAsync(app, client, s, "&sort=code").ConfigureAwait(true);
        await RefusedAsync(app, client, s, "&sort=templateVersion,-createdAt").ConfigureAwait(true);
        await RefusedAsync(app, client, s, "&templateVersions=" + string.Join(',', Enumerable.Range(1, 21).Select(i => $"1.{Num(i)}"))).ConfigureAwait(true);
        await RefusedAsync(app, client, s, "&templateVersions=" + new string('9', 21)).ConfigureAwait(true);

        // Дзеркало: межа (20 версій, 20 символів) і порожні значення - 200.
        Assert.Empty(await AllPagesAsync(app, client, s, "&templateVersions=" + string.Join(',', Enumerable.Range(1, 20).Select(i => $"1.{Num(i)}")), 5).ConfigureAwait(true));
        Assert.Empty(await AllPagesAsync(app, client, s, "&templateVersions=" + new string('9', 20), 5).ConfigureAwait(true));
        Assert.NotEmpty(await AllPagesAsync(app, client, s, "&sort=&templateVersions=", 5).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Проходить усі сторінки за <c>nextCursor</c> і повертає Id документів СЦЕНАРІЮ в порядку відповіді.</summary>
    private static async Task<List<long>> AllPagesAsync(
        EcrApiFactory app, HttpClient client, Scenario s, string extra, int limit, int? periodKey = null)
    {
        var result = new List<long>();
        string? cursor = null;
        for (var guard = 0; guard < 50; guard++)
        {
            var url = $"/api/v1/documents?limit={Num(limit)}&periodKey={Num(periodKey ?? s.PeriodKey)}{extra}"
                + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await JsonAsync(await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false), HttpStatusCode.OK, app)
                .ConfigureAwait(false);
            result.AddRange(page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()));
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (cursor is null)
            {
                // Документи інших тестів колекції читач без їхніх грантів не бачить; для безпеки лишаємо лише свої.
                return [.. result.Where(s.AllIds.Contains)];
            }
        }

        throw new InvalidOperationException("Курсор не завершився за 50 сторінок.");
    }

    private static async Task RefusedAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra)
    {
        using var response = await client
            .GetAsync(new Uri($"/api/v1/documents?periodKey={Num(s.PeriodKey)}{extra}", UriKind.Relative)).ConfigureAwait(false);
        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(false);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REQ-0422.malformedRequest", problem.GetProperty("messageKey").GetString());
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

    /// <summary>Користувач з окремою роллю <c>Document.View</c> і грантами Read на перелічені проєкти.</summary>
    private async Task<string> NewUserAsync(params int[] projectIds)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"tv_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"TV_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Version list test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        foreach (var projectId in projectIds)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return userName;
    }

    /// <summary>
    /// Три проєкти з версіями 2.9.0 (A), 2.10.0 (B), 3.0.0 (C), у кожному по три документи (їхні Id навмисно
    /// переплетені: документи створюються по колу A, B, C). У B шкала: з 202602 - версія 4.0.0.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var b = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var c = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Version = {VersionA} WHERE Id = {a.TemplateVersionId}").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Version = {VersionB} WHERE Id = {b.TemplateVersionId}").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cfg.TemplateVersion SET Version = {VersionC} WHERE Id = {c.TemplateVersionId}").ConfigureAwait(false);

        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Шкала B: до 202602 - 2.10.0, з 202602 - 4.0.0 (період 202602 теж існує в проєкті).
        var v4 = new TemplateVersion(b.TemplateId, "4.0.0", 1, now);
        db.TemplateVersions.Add(v4);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(b.ProjectId, 200001, b.TemplateVersionId));
        db.ProjectTemplateVersions.Add(new ProjectTemplateVersion(b.ProjectId, 202602, v4.Id));
        db.Periods.Add(new Period(b.ProjectId, new PeriodKey(202602), 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Ще по два документи на проєкт - по колу, щоб Id різних версій чергувалися.
        var ids = new Dictionary<int, List<long>>
        {
            [a.ProjectId] = [a.DocumentId],
            [b.ProjectId] = [b.DocumentId],
            [c.ProjectId] = [c.DocumentId],
        };
        var keys = new Dictionary<long, string>();
        var sheets = new Dictionary<int, int> { [a.ProjectId] = a.SheetDefId, [b.ProjectId] = b.SheetDefId, [c.ProjectId] = c.SheetDefId };
        for (var round = 1; round <= 2; round++)
        {
            foreach (var projectId in new[] { a.ProjectId, b.ProjectId, c.ProjectId })
            {
                var key = $"TV{tag}{Num(round)}{Num(projectId)}";
                var document = new Document(projectId, key, 1, now);
                db.Documents.Add(document);
                await db.SaveChangesAsync().ConfigureAwait(false);
                db.DocumentSheets.Add(new DocumentSheet(document.Id, sheets[projectId]));
                await db.SaveChangesAsync().ConfigureAwait(false);
                ids[projectId].Add(document.Id);
                keys[document.Id] = key;
            }
        }

        return new Scenario(a.ProjectId, b.ProjectId, c.ProjectId, a.PeriodKey.Value, ids, keys);
    }

    private sealed record Scenario(
        int ProjectA, int ProjectB, int ProjectC, int PeriodKey, Dictionary<int, List<long>> ByProject, Dictionary<long, string> Keys)
    {
        public List<long> IdsOf(int projectId) => ByProject[projectId];

        public string KeyOf(long documentId) => Keys[documentId];

        public HashSet<long> AllIds => [.. ByProject.Values.SelectMany(v => v)];

        public List<long> AscendingIds => [.. IdsOf(ProjectA), .. IdsOf(ProjectB), .. IdsOf(ProjectC)];

        public List<long> DescendingIds => [.. IdsOf(ProjectC), .. IdsOf(ProjectB), .. IdsOf(ProjectA)];
    }
}
