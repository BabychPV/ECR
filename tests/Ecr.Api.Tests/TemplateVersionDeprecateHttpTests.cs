// tests/Ecr.Api.Tests/TemplateVersionDeprecateHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-негативи <c>POST /template-versions/{id}/deprecate</c> — відкат версії шаблону без видалення
/// (ФВ-7.8): право, наявність, стан. Раніше: лише доменний тест (<c>TemplateVersionDeprecationTests</c>)
/// і виклик у клієнті; відповіді 403/404/409 на HTTP не перевіряв ніхто.
/// </summary>
/// <remarks>
/// ⚠ Окремих «deprecate» для методологій і довідників в API немає (методологія виводиться з обігу
/// публікацією наступної версії, довідник — <c>definition/publish</c>); цей маршрут — єдиний.
///
/// ⛔ Порядок серверний: право <c>Template.Publish</c> (403) → наявність (404) → стан (409). Без права
/// неіснуюча версія відповідає як наявна — існування не розкривається.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>RequireAsync(…, Permission, …)</c> з
/// <c>PublishTemplateVersionHandler.DeprecateAsync</c> — червоніє «без права → 403 і версія лишається
/// Published»; прибрати <c>Status != Published</c> у <c>TemplateVersion.Deprecate</c> — червоніє «чернетку
/// вивести з обігу не можна».
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionDeprecateHttpTests(SqlServerFixture sql)
{
    private const int Missing = 999_999_999;

    private static readonly string[] Publisher = ["Template.View", "Template.Edit", "Template.Publish"];

    private static readonly string[] EditorOnly = ["Template.View", "Template.Edit"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.8")]
    public async Task Без_права_Template_Publish_403_однаково_для_наявної_й_неіснуючої_версії_і_версія_лишається_опублікованою()
    {
        var versionId = await PublishedVersionAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, EditorOnly).ConfigureAwait(true);

        using var existing = await editor.PostAsJsonAsync(DeprecateUrl(versionId), new { reason = "без права" })
            .ConfigureAwait(true);
        using var missing = await editor.PostAsJsonAsync(DeprecateUrl(Missing), new { reason = "без права" })
            .ConfigureAwait(true);

        var a = await RefusalAsync(app, existing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        var b = await RefusalAsync(app, missing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);

        Assert.Contains("Template.Publish", a.Body, StringComparison.Ordinal);
        Assert.Equal(a.MessageKey, b.MessageKey);
        Assert.Equal(TemplateVersionStatus.Published, await StatusAsync(versionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.8")]
    public async Task Неіснуюча_версія_404_ECR_TMPL_0404()
    {
        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        using var response = await publisher.PostAsJsonAsync(DeprecateUrl(Missing), new { reason = "тест" })
            .ConfigureAwait(true);

        await RefusalAsync(app, response, HttpStatusCode.NotFound, "ECR-TMPL-0404").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.8")]
    public async Task Чернетку_вивести_з_обігу_не_можна_409_ECR_TMPL_0409_і_вона_лишається_чернеткою()
    {
        var draft = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: 1).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        using var response = await publisher.PostAsJsonAsync(DeprecateUrl(draft.TemplateVersionId), new { reason = "тест" })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, response, HttpStatusCode.Conflict, "ECR-TMPL-0409").ConfigureAwait(true);
        Assert.Equal("err.ECR-TMPL-0409.deprecateRequiresPublished", refusal.MessageKey);
        Assert.Equal(TemplateVersionStatus.Draft, await StatusAsync(draft.TemplateVersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.8")]
    public async Task Опублікована_версія_виводиться_з_обігу_204_з_записом_причини_а_повторно_409()
    {
        var versionId = await PublishedVersionAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        // Контроль: інакше 409 нижче доводив би лише, що дія завжди відмовляє.
        using var first = await publisher.PostAsJsonAsync(DeprecateUrl(versionId), new { reason = "Замінено версією 2" })
            .ConfigureAwait(true);
        Assert.True(
            first.StatusCode == HttpStatusCode.NoContent,
            $"{(int)first.StatusCode}: {await first.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Equal(TemplateVersionStatus.Deprecated, await StatusAsync(versionId).ConfigureAwait(true));
        Assert.Equal("Замінено версією 2", await PublicationReasonAsync(versionId).ConfigureAwait(true));

        // Версія вже виведена: стан не Published → той самий 409, а не 204 «ще раз» і не 500.
        using var second = await publisher.PostAsJsonAsync(DeprecateUrl(versionId), new { reason = "ще раз" })
            .ConfigureAwait(true);

        var refusal = await RefusalAsync(app, second, HttpStatusCode.Conflict, "ECR-TMPL-0409").ConfigureAwait(true);
        Assert.Equal("err.ECR-TMPL-0409.deprecateRequiresPublished", refusal.MessageKey);
    }

    private static Uri DeprecateUrl(int versionId)
        => new($"/api/v1/template-versions/{versionId}/deprecate", UriKind.Relative);

    private sealed record Refusal(string? ErrorCode, string? MessageKey, string Body);

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

        Assert.Equal(errorCode, Str("errorCode"));

        return new Refusal(Str("errorCode"), Str("messageKey"), body);
    }

    /// <summary>Нова версія шаблону, переведена в Published доменною дією (як у сусідніх HTTP-тестах).</summary>
    private async Task<int> PublishedVersionAsync()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: 1).ConfigureAwait(false);

        await using var db = Db();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == chain.TemplateVersionId).ConfigureAwait(false);
        version.Publish(1, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return chain.TemplateVersionId;
    }

    private async Task<TemplateVersionStatus> StatusAsync(int versionId)
    {
        await using var db = Db();

        return await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => v.Status)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<string?> PublicationReasonAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) ChangeReason FROM aud.PublicationEvent
            WHERE EntityType = N'TemplateVersion' AND EntityId = @id ORDER BY ChangedAt DESC
            """;
        command.Parameters.AddWithValue("@id", versionId);

        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
