// tests/Ecr.Api.Tests/ReportDefinitionVersionsHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-негативи версій опису звіту: <c>POST /reports/{id}/versions</c> і
/// <c>POST /reports/{id}/versions/{vid}/publish</c> (ФВ-10.4, ФВ-14.7).
/// </summary>
/// <remarks>
/// ⛔ Раніше ці дві дії мали лише обробникові тести (<c>ReportDefHandlersTests</c>, моки порту):
/// статус відповіді ставить <c>ExceptionHandlingMiddleware</c>, і відмови 403/404/409/422 на
/// справжньому HTTP не перевіряв ніхто. Тут перевіряється саме відповідь: статус, код каталогу й
/// ключ тексту, а після відмови — що в базі нічого не змінилось.
///
/// ⚠ Право — <c>Report.EditDefinition</c> на обидві дії (публікація версії звіту окремого права не
/// має: <c>PublishReportVersionHandler</c>). Порядок: право → наявність → стан/вміст, тож без права
/// неіснуючий опис відповідає так само, як наявний: <c>403</c>, а не <c>404</c> — існування опису
/// не розкривається.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>PermissionCheck.RequireAsync</c> з
/// <c>CreateReportVersionHandler</c>/<c>PublishReportVersionHandler</c> — червоніють «без права → 403»;
/// прибрати перевірку <c>version.ReportDefId != reportDefId</c> — червоніє «версія чужого опису».
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportDefinitionVersionsHttpTests(SqlServerFixture sql)
{
    private const int Missing = 999_999_999;

    private static readonly string[] Author = ["Report.EditDefinition", "Report.ViewRegulatory"];

    private static readonly string[] ReaderOnly = ["Report.ViewRegulatory"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Нова_версія_без_права_EditDefinition_403_однаково_для_наявного_й_неіснуючого_опису()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, ReaderOnly).ConfigureAwait(true);
        var (defId, _) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        using var existing = await reader.PostAsJsonAsync(VersionsUrl(defId), NewVersionBody("2.0")).ConfigureAwait(true);
        using var missing = await reader.PostAsJsonAsync(VersionsUrl(Missing), NewVersionBody("2.0")).ConfigureAwait(true);

        var a = await RefusalAsync(app, existing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        var b = await RefusalAsync(app, missing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);

        // Відповідь називає право і не відрізняє наявний опис від неіснуючого.
        Assert.Contains("Report.EditDefinition", a.Body, StringComparison.Ordinal);
        Assert.Equal(a.MessageKey, b.MessageKey);

        await using var db = Db();
        Assert.Equal(1, await db.ReportVersions.CountAsync(v => v.ReportDefId == defId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Нова_версія_неіснуючого_опису_404_ECR_RPT_0404()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);

        using var response = await author.PostAsJsonAsync(VersionsUrl(Missing), NewVersionBody("1.0")).ConfigureAwait(true);

        var refusal = await RefusalAsync(app, response, HttpStatusCode.NotFound, "ECR-RPT-0404").ConfigureAwait(true);
        Assert.Equal("err.ECR-RPT-0404.def", refusal.MessageKey);
    }

    [Theory]
    [InlineData("noColumns")]
    [InlineData("columnKind")]
    [InlineData("duplicateColumn")]
    [InlineData("version")]
    [InlineData("versionTooLong")]
    [InlineData("rowSource")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Нова_версія_з_неприйнятним_описом_422_з_ключем_а_не_500_і_нічого_не_створює(string problem)
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        var (defId, _) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        var good = new[] { new { code = "Value", kind = "number" } };
        object body = problem switch
        {
            "noColumns" => new { version = "2.0", columns = Array.Empty<object>(), rules = (object?)null },
            "columnKind" => new { version = "2.0", columns = new[] { new { code = "Value", kind = "money" } }, rules = (object?)null },
            "duplicateColumn" => new { version = "2.0", columns = new[] { good[0], good[0] }, rules = (object?)null },
            "version" => new { version = " ", columns = good, rules = (object?)null },
            "versionTooLong" => new { version = new string('9', 21), columns = good, rules = (object?)null },
            _ => new { version = "2.0", columns = good, rules = (object?)new { rowSource = "Nowhere" } },
        };

        using var response = await author.PostAsJsonAsync(VersionsUrl(defId), body).ConfigureAwait(true);

        var refusal = await RefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-RPT-0422").ConfigureAwait(true);
        var expectedKey = problem == "versionTooLong" ? "version" : problem;
        Assert.Equal($"err.ECR-RPT-0422.{expectedKey}", refusal.MessageKey);

        await using var db = Db();
        Assert.Equal(1, await db.ReportVersions.CountAsync(v => v.ReportDefId == defId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Нова_версія_з_кодом_колонки_поза_правилами_коду_422_ECR_CFG_0422()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        var (defId, _) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        using var response = await author.PostAsJsonAsync(
            VersionsUrl(defId),
            new { version = "2.0", columns = new[] { new { code = "bad code!", kind = "number" } }, rules = (object?)null })
            .ConfigureAwait(true);

        await RefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-CFG-0422").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.7")]
    public async Task Публікація_без_права_EditDefinition_403_і_версія_лишається_чернеткою()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, ReaderOnly).ConfigureAwait(true);
        var (defId, versionId) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        using var existing = await reader.PostAsJsonAsync(PublishUrl(defId, versionId), new { reason = "без права" })
            .ConfigureAwait(true);
        using var missing = await reader.PostAsJsonAsync(PublishUrl(Missing, Missing), new { reason = "без права" })
            .ConfigureAwait(true);

        var a = await RefusalAsync(app, existing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        var b = await RefusalAsync(app, missing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        Assert.Contains("Report.EditDefinition", a.Body, StringComparison.Ordinal);
        Assert.Equal(a.MessageKey, b.MessageKey);

        await using var db = Db();
        var version = await db.ReportVersions.AsNoTracking().SingleAsync(v => v.Id == versionId).ConfigureAwait(true);
        Assert.Equal("Draft", version.Status.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.7")]
    public async Task Публікація_неіснуючої_версії_404_а_версії_чужого_опису_404_і_нічого_не_публікує()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        var (ownId, _) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);
        var (_, foreignVersionId) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        using var missing = await author.PostAsJsonAsync(PublishUrl(ownId, Missing), new { reason = "тест" })
            .ConfigureAwait(true);
        var notFound = await RefusalAsync(app, missing, HttpStatusCode.NotFound, "ECR-RPT-0404").ConfigureAwait(true);
        Assert.Equal("err.ECR-RPT-0404.version", notFound.MessageKey);

        // `{id}` у шляху не декоративний: версія іншого опису за чужою адресою не публікується.
        using var foreign = await author.PostAsJsonAsync(PublishUrl(ownId, foreignVersionId), new { reason = "тест" })
            .ConfigureAwait(true);
        var wrongDef = await RefusalAsync(app, foreign, HttpStatusCode.NotFound, "ECR-RPT-0404").ConfigureAwait(true);
        Assert.Equal("err.ECR-RPT-0404.versionWrongDef", wrongDef.MessageKey);

        await using var db = Db();
        var version = await db.ReportVersions.AsNoTracking().SingleAsync(v => v.Id == foreignVersionId).ConfigureAwait(true);
        Assert.Equal("Draft", version.Status.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.7")]
    public async Task Публікація_без_причини_422_а_версія_лишається_чернеткою(string reason)
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        var (defId, versionId) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        using var response = await author.PostAsJsonAsync(PublishUrl(defId, versionId), new { reason }).ConfigureAwait(true);

        var refusal = await RefusalAsync(app, response, HttpStatusCode.UnprocessableEntity, "ECR-REQ-0422").ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.reportPublishReason", refusal.MessageKey);

        await using var db = Db();
        var version = await db.ReportVersions.AsNoTracking().SingleAsync(v => v.Id == versionId).ConfigureAwait(true);
        Assert.Equal("Draft", version.Status.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.7")]
    public async Task Повторна_публікація_версії_409_ECR_RPT_0409_а_перша_проходить()
    {
        using var app = new EcrApiFactory(sql);
        using var author = await SystemHealthControllerTests.SignedInAsync(sql, app, Author).ConfigureAwait(true);
        var (defId, versionId) = await CreateDefinitionAsync(app, author).ConfigureAwait(true);

        // Контроль: перша публікація — 200 зі станом Published.
        using var first = await author.PostAsJsonAsync(PublishUrl(defId, versionId), new { reason = "Затверджено комісією" })
            .ConfigureAwait(true);
        var firstBody = await first.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{(int)first.StatusCode}: {firstBody}\n{app.ErrorsText}");
        using (var json = JsonDocument.Parse(firstBody))
        {
            Assert.Equal("Published", json.RootElement.GetProperty("status").GetString());
        }

        using var second = await author.PostAsJsonAsync(PublishUrl(defId, versionId), new { reason = "Ще раз" })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, second, HttpStatusCode.Conflict, "ECR-RPT-0409").ConfigureAwait(true);
        Assert.Equal("err.ECR-RPT-0409.versionNotDraft", refusal.MessageKey);
    }

    private static Uri VersionsUrl(int defId) => new($"/api/v1/reports/{defId}/versions", UriKind.Relative);

    private static Uri PublishUrl(int defId, int versionId)
        => new($"/api/v1/reports/{defId}/versions/{versionId}/publish", UriKind.Relative);

    private static object NewVersionBody(string version)
        => new { version, columns = new[] { new { code = "Value", kind = "number" } }, rules = (object?)null };

    /// <summary>Заводить опис із першою версією-чернеткою; повертає ідентифікатори опису й версії.</summary>
    private static async Task<(int DefId, int VersionId)> CreateDefinitionAsync(EcrApiFactory app, HttpClient author)
    {
        using var response = await author.PostAsJsonAsync(
            new Uri("/api/v1/reports", UriKind.Relative),
            new
            {
                code = $"HN{Guid.NewGuid():N}"[..12],
                nameL10n = new Dictionary<string, string> { ["en"] = "HTTP negative report" },
                isRegulatory = false,
                version = "1.0",
                columns = new[] { new { code = "Value", kind = "number" } },
                rules = (object?)null,
            }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        return (root.GetProperty("id").GetInt32(), root.GetProperty("versions")[0].GetProperty("id").GetInt32());
    }

    private sealed record Refusal(string? ErrorCode, string? MessageKey, string Body);

    /// <summary>Перевіряє статус і код відмови; ключ тексту й подробиця — для дальших перевірок.</summary>
    private static async Task<Refusal> RefusalAsync(
        EcrApiFactory app, HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == status,
            $"очікували {(int)status}, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;

        // Код каталогу є завжди: відмова без нього — не контрольована.
        Assert.False(string.IsNullOrWhiteSpace(Str("errorCode")), body);

        if (errorCode is not null)
        {
            Assert.Equal(errorCode, Str("errorCode"));
        }

        return new Refusal(Str("errorCode"), Str("messageKey"), body);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
