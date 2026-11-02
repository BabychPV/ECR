// tests/Ecr.Api.Tests/MethodologyCategoriesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
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
/// План CONST-CATEGORY і похідні аргументи на справжньому SQL: <c>GET/PUT/DELETE …/categories</c>, вибір категорії
/// константою зі списку, фільтр констант, клон, права; формула — похідний <c>argumentsCsv</c>/<c>arguments</c> і
/// перенос маркерів застарілого поля запиту.
/// </summary>
/// <remarks>
/// ⛔ Мутації: зняти <c>scope.RequireAsync</c> у дії <c>SaveCategory</c> — червоні 403/404 матриці; прибрати
/// <c>MethodologyCategoryChecks.RequireListed</c> із запису константи — червоний 422 «constantCategoryUnknown».
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCategoriesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Meth-Categories-2026!";

    private static readonly string[] EditorPermissions =
        ["Calculation.View", "Calculation.EditConstant", "Calculation.EditFormula"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_категорій_обмежує_константи_і_захищає_зайняту_категорію()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        await OkAsync(await client.PutAsJsonAsync(Category(stand, "Diesel"), new { description = "Diesel fuel" }).ConfigureAwait(true)).ConfigureAwait(true);
        await OkAsync(await client.PutAsJsonAsync(Category(stand, "LPG"), new { }).ConfigureAwait(true)).ConfigureAwait(true);

        // Константа з категорією зі списку — так; описка — 422 з ключем; спільна — так.
        await OkAsync(await PutConstantAsync(client, stand, "EF_1", "Diesel").ConfigureAwait(true)).ConfigureAwait(true);
        var typo = await ProblemAsync(await PutConstantAsync(client, stand, "EF_2", "Desiel").ConfigureAwait(true), HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.constantCategoryUnknown", typo.GetProperty("messageKey").GetString());
        await OkAsync(await PutConstantAsync(client, stand, "RHO", null).ConfigureAwait(true)).ConfigureAwait(true);

        var list = await OkAsync(await client.GetAsync(Categories(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(
            [("Diesel", 1, "Diesel fuel"), ("LPG", 0, (string?)null)],
            list.EnumerateArray().Select(c => (
                c.GetProperty("code").GetString(),
                c.GetProperty("constantCount").GetInt32(),
                c.GetProperty("description").GetString())));

        // Зайняту не видаляють і не виводять з ужитку; вільну — видаляють.
        var inUse = await ProblemAsync(await client.DeleteAsync(Category(stand, "Diesel")).ConfigureAwait(true), HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.categoryInUse", inUse.GetProperty("messageKey").GetString());
        var retire = await ProblemAsync(await client.PutAsJsonAsync(Category(stand, "Diesel"), new { isRetired = true }).ConfigureAwait(true), HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.categoryInUse", retire.GetProperty("messageKey").GetString());

        var rename = await ProblemAsync(await client.PutAsJsonAsync(Category(stand, "diesel"), new { }).ConfigureAwait(true), HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.categoryRename", rename.GetProperty("messageKey").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Category(stand, "LPG")).ConfigureAwait(true)).StatusCode);
        Assert.Equal(["Diesel"], await CodesAsync(stand.VersionId).ConfigureAwait(true));

        // Фільтр констант на сервері: категорія + спільні; рядок несе категорію.
        var filtered = await OkAsync(await client.GetAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/constants?category=Diesel&shared=true&sort=Category",
            UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(
            [("RHO", (string?)null), ("EF_1", "Diesel")],
            filtered.EnumerateArray().Select(c => (c.GetProperty("code").GetString(), c.GetProperty("category").GetString())));

        // Клон версії переносить перелік.
        var clone = await OkAsync(await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions", UriKind.Relative),
            new { versionNumber = "2.0", copyFromVersionId = stand.VersionId, level = "Configuration" }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(["Diesel"], await CodesAsync(clone.GetProperty("id").GetInt32()).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Матриця_прав_і_опублікована_версія()
    {
        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(app, ["Calculation.View"]).ConfigureAwait(true);
        using var formulaEditor = await SignedInAsync(app, ["Calculation.View", "Calculation.EditFormula"]).ConfigureAwait(true);
        using var editor = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var draft = await StandAsync().ConfigureAwait(true);
        var published = await StandAsync(published: true).ConfigureAwait(true);

        // View читає; писати — лише EditConstant (право на константи, а не на формули).
        await OkAsync(await viewer.GetAsync(Categories(draft)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(Category(draft, "A"), new { }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await formulaEditor.PutAsJsonAsync(Category(draft, "A"), new { }).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(Category(draft, "A")).ConfigureAwait(true)).StatusCode);
        await OkAsync(await editor.PutAsJsonAsync(Category(draft, "A"), new { }).ConfigureAwait(true)).ConfigureAwait(true);

        // Чужа адреса — 404; опублікована версія — 409 draftRequired.
        var foreign = await editor.GetAsync(new Uri(
            $"/api/v1/methodologies/{draft.MethodologyId + 1000}/versions/{draft.VersionId}/categories", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var frozen = await ProblemAsync(await editor.PutAsJsonAsync(Category(published, "A"), new { }).ConfigureAwait(true), HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.draftRequired", frozen.GetProperty("messageKey").GetString());
        Assert.Empty(await CodesAsync(published.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формула_несе_похідний_список_а_застаріле_поле_переносить_лише_маркер()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        // Новий спосіб: маркер у виразі, поля списку немає.
        var saved = await OkAsync(await client.PutAsJsonAsync(
            Formula(stand, "Total"),
            new { expression = "@Land_Measure_RunningHours? * CST.K + @Power", resultType = "Number" }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("@Land_Measure_RunningHours?;CST.K;@Power", saved.GetProperty("argumentsCsv").GetString());
        Assert.Equal(
            [("Argument", "Land_Measure_RunningHours", true), ("Constant", "K", false), ("Argument", "Power", false)],
            saved.GetProperty("arguments").EnumerateArray().Select(a => (
                a.GetProperty("kind").GetString(), a.GetProperty("name").GetString(), a.GetProperty("isOptional").GetBoolean())));

        // Застаріле поле (`tools/land`): маркер переходить у вираз, решта списку ігнорується.
        var legacy = await OkAsync(await client.PutAsJsonAsync(
            Formula(stand, "Other"),
            new { expression = "@Hours * 2", resultType = "Number", argumentsCsv = "Hours?;Ghost;CST.Unused" }).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("@Hours? * 2", legacy.GetProperty("expression").GetString());
        Assert.Equal("@Hours?", legacy.GetProperty("argumentsCsv").GetString());

        await using var db = new EcrDbContext(Options());
        var stored = await db.MethodologyFormulas.AsNoTracking()
            .SingleAsync(f => f.MethodologyVersionId == stand.VersionId && f.Code == "Other").ConfigureAwait(true);
        Assert.Equal("@Hours? * 2", stored.Expression);
        Assert.Equal("@Hours?", stored.ArgumentsCsv);
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static Uri Categories(Stand stand)
        => new($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/categories", UriKind.Relative);

    private static Uri Category(Stand stand, string code)
        => new($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/categories/{Uri.EscapeDataString(code)}", UriKind.Relative);

    private static Uri Formula(Stand stand, string code)
        => new($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/formulas/{code}", UriKind.Relative);

    private async Task<HttpResponseMessage> PutConstantAsync(HttpClient client, Stand stand, string code, string? category)
    {
        int unit;
        await using (var db = new EcrDbContext(Options()))
        {
            unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        }

        return await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/constants/{code}", UriKind.Relative),
            new { kind = "Numeric", value = 1.5m, unitId = unit, category }).ConfigureAwait(false);
    }

    private static Task<JsonElement> OkAsync(HttpResponseMessage response) => ProblemAsync(response, HttpStatusCode.OK);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<List<string>> CodesAsync(int versionId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.MethodologyCategories.AsNoTracking()
            .Where(c => c.MethodologyVersionId == versionId)
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Code)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private async Task<Stand> StandAsync(bool published = false)
    {
        await using var db = new EcrDbContext(Options());
        var now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var methodology = new Methodology(
            EcrCode.Create($"CT{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Categories" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (published)
        {
            version.Publish(publishedByUserId: 2, "CONST-CATEGORY", new DateOnly(2026, 1, 1), testsPassed: true, now);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Stand(methodology.Id, version.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"mcat_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Categories test" }));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private sealed record Stand(int MethodologyId, int VersionId);
}
