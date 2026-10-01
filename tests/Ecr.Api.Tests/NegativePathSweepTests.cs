// tests/Ecr.Api.Tests/NegativePathSweepTests.cs
using System.Net;
using System.Net.Http.Headers;
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
/// Прохід по відмовах API (1.10): некоректне поле запиту — контрольована відмова з кодом
/// каталогу й ключем, а не <c>500 ECR-SYS-0500</c>.
/// </summary>
/// <remarks>
/// ⛔ Кожен випадок нижче давав 500 на <c>dev/integration</c> 19db066: зонд розіслав ~8,4 тис.
/// некоректних запитів (порожні й задовгі рядки, межові числа, порожні тіла, файли не-xlsx) по
/// всіх операціях <c>openapi.snapshot.json</c> під трьома профілями — повні права, без прав,
/// грант на чужий проєкт. Під двома останніми 500 не знайшлося; під першим — ці дев'ять.
/// Тест перевіряє і статус, і код, і ключ: «не 500» без коду довело б лише, що впало інакше.
/// </remarks>
[Collection("SqlServer")]
public sealed class NegativePathSweepTests(SqlServerFixture sql)
{
    private const string Password = "Api-Negative-Path-2026!";

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Permissions =
    [
        "Document.View", "Document.Import", "Calculation.View", "Calculation.EditFormula",
        "Project.Manage", "Period.Configure", "Integration.Manage", "System.ManageLocalization",
        "Security.ManageUsers",
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(13)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Перегляд_імпорту_файлу_коротшого_за_zip_каталог_дає_422_notAWorkbook(int length)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent("PK\u0003\u0004hello garbage"u8.ToArray()[..length]);
        file.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "book.xlsx");

        using var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{stand.DocumentId}/import/preview", UriKind.Relative), content)
            .ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-IMP-0422", "err.ECR-IMP-0422.notAWorkbook")
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Застосування_імпорту_з_порожнім_токеном_дає_422_previewExpired()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        foreach (var token in new[] { "", "   " })
        {
            using var response = await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{stand.DocumentId}/import/apply", UriKind.Relative),
                new { previewToken = token }).ConfigureAwait(true);

            await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-IMP-0422", "err.ECR-IMP-0422.previewExpired")
                .ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Версія_методології_з_порожнім_або_задовгим_номером_дає_422(string versionNumber)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions", UriKind.Relative),
            new { versionNumber, copyFromVersionId = (int?)null, level = "Configuration" }).ConfigureAwait(true);

        var problem = await AssertRefusalAsync(
            app, response, HttpStatusCode.UnprocessableEntity, "ECR-CALC-0422", "err.ECR-CALC-0422.versionNumber").ConfigureAwait(true);
        Assert.Equal("20", problem.GetProperty("maxLength").GetString());

        // Межа не зсунута: рівно 20 символів — законний номер.
        using var ok = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions", UriKind.Relative),
            new { versionNumber = "12345678901234567890", copyFromVersionId = (int?)null, level = "Configuration" }).ConfigureAwait(true);
        Assert.True(ok.IsSuccessStatusCode, $"{(int)ok.StatusCode} {await ok.Content.ReadAsStringAsync().ConfigureAwait(true)}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Задовгий_вираз_формули_дає_422_formulaTooLong()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        var url = $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.DraftId}/formulas/";

        using var response = await client.PutAsJsonAsync(
            new Uri(url + "tons", UriKind.Relative),
            new { expression = "1+" + new string('1', 1999), resultType = 0, outputUnitId = (int?)null, argumentsCsv = (string?)null })
            .ConfigureAwait(true);

        var problem = await AssertRefusalAsync(
            app, response, HttpStatusCode.UnprocessableEntity, "ECR-CALC-0422", "err.ECR-CALC-0422.formulaTooLong").ConfigureAwait(true);
        Assert.Equal("tons", problem.GetProperty("code").GetString());

        // Рівно 2000 символів — межа колонки — зберігається.
        using var ok = await client.PutAsJsonAsync(
            new Uri(url + "edge", UriKind.Relative),
            new { expression = "1+" + new string('1', 1998), resultType = 0, outputUnitId = (int?)null, argumentsCsv = (string?)null })
            .ConfigureAwait(true);
        Assert.True(ok.IsSuccessStatusCode, $"{(int)ok.StatusCode} {await ok.Content.ReadAsStringAsync().ConfigureAwait(true)}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Від_ємний_допуск_тесту_дає_422_і_для_нового_і_для_наявного()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        // `case1` уже є (правка), `case2` — новий (створення): два різні шляхи сутності.
        foreach (var code in new[] { "case1", "case2" })
        {
            using var response = await client.PutAsJsonAsync(
                new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.DraftId}/tests/{code}", UriKind.Relative),
                new { inputJson = """{"periodKey":{"value":0},"arguments":[]}""", expectedJson = """{"tons":1}""", tolerance = -0.5m }).ConfigureAwait(true);

            var problem = await AssertRefusalAsync(
                app, response, HttpStatusCode.UnprocessableEntity, "ECR-CALC-0422", "err.ECR-CALC-0422.testToleranceNegative").ConfigureAwait(true);
            Assert.Equal(code, problem.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(int.MaxValue, 15, 45, 45)]
    [InlineData(0, 15, int.MaxValue, 45)]
    [InlineData(int.MinValue, 15, 45, 45)]
    [InlineData(0, 15, 45, 3661)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Зсув_політики_періодів_поза_межами_дає_422_і_не_змінює_політику(
        int open, int grace, int hardClose, int yearGrace)
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        var body = new { openOffsetDays = open, graceOffsetDays = grace, hardCloseOffsetDays = hardClose, yearGraceOffsetDays = yearGrace };

        using var update = await client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/period-policies/{stand.PeriodPolicyId}", UriKind.Relative), body).ConfigureAwait(true);
        var problem = await AssertRefusalAsync(
            app, update, HttpStatusCode.UnprocessableEntity, "ECR-PRD-4225", "err.ECR-PRD-4225.offsetOutOfRange").ConfigureAwait(true);
        Assert.Equal("3660", problem.GetProperty("max").GetString());

        using var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/projects/period-policies", UriKind.Relative),
            new { code = $"NP{Guid.NewGuid():N}"[..16], body.openOffsetDays, body.graceOffsetDays, body.hardCloseOffsetDays, body.yearGraceOffsetDays })
            .ConfigureAwait(true);
        await AssertRefusalAsync(app, create, HttpStatusCode.UnprocessableEntity, "ECR-PRD-4225", "err.ECR-PRD-4225.offsetOutOfRange")
            .ConfigureAwait(true);

        await using var db = Db();
        var policy = await db.PeriodPolicies.AsNoTracking().SingleAsync(p => p.Id == stand.PeriodPolicyId).ConfigureAwait(true);
        Assert.Equal((0, 15, 45, 45), (policy.OpenOffsetDays, policy.GraceOffsetDays, policy.HardCloseOffsetDays, policy.YearGraceOffsetDays));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Задовга_причина_фіксації_поточного_періоду_дає_422()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        var url = new Uri($"/api/v1/projects/{stand.ProjectId}/current-period", UriKind.Relative);

        using var response = await client.PutAsJsonAsync(url, new { pinnedPeriodId = stand.PeriodId, reason = new string('r', 401) })
            .ConfigureAwait(true);
        var problem = await AssertRefusalAsync(
            app, response, HttpStatusCode.UnprocessableEntity, "ECR-PRD-0422", "err.ECR-PRD-0422.pinReasonTooLong").ConfigureAwait(true);
        Assert.Equal("400", problem.GetProperty("max").GetString());

        using var ok = await client.PutAsJsonAsync(url, new { pinnedPeriodId = stand.PeriodId, reason = new string('r', 400) })
            .ConfigureAwait(true);
        Assert.True(ok.StatusCode == HttpStatusCode.NoContent, $"{(int)ok.StatusCode} {await ok.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Прив_язка_вікна_без_колонок_дає_404_колонки_а_не_500()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        // Порожнє тіло: усі колонки — 0. І селектор, явно заданий нулем, поряд зі справжніми колонками.
        foreach (var body in new object[]
                 {
                     new { },
                     new
                     {
                         tableDefId = stand.TableDefId, targetColumnDefId = stand.ColumnIds[0],
                         startColumnDefId = stand.ColumnIds[1], endColumnDefId = stand.ColumnIds[2],
                         selectorColumnDefId = 0, summary = "Total", isStep = false, targetUnitId = 1,
                     },
                 })
        {
            using var response = await client.PostAsJsonAsync(new Uri("/api/v1/row-window-maps", UriKind.Relative), body)
                .ConfigureAwait(true);

            var problem = await AssertRefusalAsync(
                app, response, HttpStatusCode.NotFound, "ECR-INT-0405", "err.ECR-INT-0405.column").ConfigureAwait(true);
            Assert.Equal("0", problem.GetProperty("columnDefId").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Задовгий_ключ_рядка_каталогу_дає_422_uiStringUnknownKey()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        using var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/ui-strings/en/{new string('k', 201)}", UriKind.Relative),
            new { value = "x", scope = "Public" }).ConfigureAwait(true);

        await AssertRefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-REQ-0422", "err.ECR-REQ-0422.uiStringUnknownKey")
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "negative-path-sweep")]
    public async Task Задовгий_разовий_пароль_дає_422_tooLong_і_не_створює_користувача()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stand.UserName).ConfigureAwait(true);

        var userName = $"np_{Guid.NewGuid():N}"[..20];
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new { userName, displayName = "Negative path", provider = "Local", sid = (string?)null, initialPassword = "Aa1" + new string('x', 254) })
            .ConfigureAwait(true);

        var problem = await AssertRefusalAsync(
            app, response, HttpStatusCode.UnprocessableEntity, "ECR-PWD-0422", "err.ECR-PWD-0422.tooLong").ConfigureAwait(true);
        Assert.Equal("256", problem.GetProperty("maxLength").GetString());

        // ⛔ ФВ-6.11: ні пароля, ні його фрагмента у відповіді.
        Assert.DoesNotContain(new string('x', 20), problem.GetRawText(), StringComparison.Ordinal);

        await using var db = Db();
        Assert.False(await db.Users.AnyAsync(u => u.UserName == userName).ConfigureAwait(true));
    }

    private static async Task<JsonElement> AssertRefusalAsync(
        EcrApiFactory app, HttpResponseMessage response, HttpStatusCode status, string code, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"очікували {(int)status}, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());

        // Ключ знайдено в каталозі: інакше клієнт бачив би сирий український запасний текст.
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.DoesNotContain("{", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        return problem;
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
    /// Документ, методологія з чернеткою, власна політика періодів і користувач із правами та
    /// грантом <c>Manage</c> на проєкт документа.
    /// </summary>
    private async Task<Stand> ArrangeAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: 1).ConfigureAwait(false);
        await using var db = Db();

        var methodology = new Methodology(
            EcrCode.Create($"NP{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Negative path" }));
        db.Methodologies.Add(methodology);

        // Своя політика: тест змінює її (відмовою), а не спільну з сіду.
        var policy = new Ecr.Domain.Entities.Documents.PeriodPolicy(EcrCode.Create($"NP{Guid.NewGuid():N}"[..16]), 0, 15, 45, 45);
        db.PeriodPolicies.Add(policy);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var draft = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(draft);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyTestCases.Add(draft.AddTestCase("case1", "{}", """{"tons":1}""", 0m));

        var userName = $"np_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"NP_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Negative path" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in Permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Manage));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var periodId = await db.Periods.AsNoTracking()
            .Where(p => p.ProjectId == document.ProjectId)
            .Select(p => p.Id)
            .FirstAsync().ConfigureAwait(false);

        return new Stand(
            document.DocumentId, document.ProjectId, periodId, document.TableDefId, document.ColumnDefIds,
            methodology.Id, draft.Id, policy.Id, userName);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed record Stand(
        long DocumentId,
        int ProjectId,
        int PeriodId,
        int TableDefId,
        IReadOnlyList<int> ColumnIds,
        int MethodologyId,
        int DraftId,
        int PeriodPolicyId,
        string UserName);
}
