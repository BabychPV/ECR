// tests/Ecr.Api.Tests/TemplateVersionDiscardHttpTests.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>DELETE /template-versions/{id}</c> — відкинути незавершену чернетку разом з її структурою.
/// </summary>
/// <remarks>
/// ⛔ Порядок серверний: право <c>Template.Publish</c> (403) → наявність (404) → стан (409
/// <c>discardRequiresDraft</c>) → використання (409 <c>discardVersionInUse</c>).
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>version.EnsureDiscardable()</c> з
/// <c>DiscardTemplateVersionHandler</c> — червоніє «опубліковану відкинути не можна» (інша відмова замість
/// <c>discardRequiresDraft</c>); прибрати перевірку <c>usages.Count &gt; 0</c> — червоніє «версію з
/// проєктом відкинути не можна» (FK зупиняє пакет, але вже без переліку використань).
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionDiscardHttpTests(SqlServerFixture sql)
{
    private const int Missing = 999_999_999;

    private static readonly string[] Publisher = ["Template.View", "Template.Edit", "Template.Publish"];

    private static readonly string[] EditorOnly = ["Template.View", "Template.Edit"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Без_права_Template_Publish_403_і_чернетка_лишається()
    {
        var draftId = await UnusedDraftAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, EditorOnly).ConfigureAwait(true);

        using var existing = await editor.DeleteAsync(Url(draftId)).ConfigureAwait(true);
        using var missing = await editor.DeleteAsync(Url(Missing)).ConfigureAwait(true);

        var a = await RefusalAsync(app, existing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);
        var b = await RefusalAsync(app, missing, HttpStatusCode.Forbidden, "ECR-AUTH-0403").ConfigureAwait(true);

        Assert.Contains("Template.Publish", a.Body, StringComparison.Ordinal);
        Assert.Equal(a.MessageKey, b.MessageKey);
        Assert.True(await ExistsAsync(draftId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Неіснуюча_версія_404_ECR_TMPL_0404()
    {
        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        using var response = await publisher.DeleteAsync(Url(Missing)).ConfigureAwait(true);

        await RefusalAsync(app, response, HttpStatusCode.NotFound, "ECR-TMPL-0404").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Опубліковану_версію_відкинути_не_можна_409_і_вона_лишається()
    {
        var draftId = await UnusedDraftAsync().ConfigureAwait(true);
        await using (var db = Db())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == draftId).ConfigureAwait(true);
            version.Publish(1, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        using var response = await publisher.DeleteAsync(Url(draftId)).ConfigureAwait(true);

        var refusal = await RefusalAsync(app, response, HttpStatusCode.Conflict, "ECR-TMPL-0409").ConfigureAwait(true);
        Assert.Equal("err.ECR-TMPL-0409.discardRequiresDraft", refusal.MessageKey);
        Assert.Equal(TemplateVersionStatus.Published, await StatusAsync(draftId).ConfigureAwait(true));
        Assert.True(await SheetCountAsync(draftId).ConfigureAwait(true) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Чернетку_з_проєктом_і_документом_відкинути_не_можна_409_з_переліком_використань()
    {
        // TestDocumentBuilder лишає версію чернеткою, але прив'язує до неї проєкт і документ.
        var used = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: 1).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        using var response = await publisher.DeleteAsync(Url(used.TemplateVersionId)).ConfigureAwait(true);

        var refusal = await RefusalAsync(app, response, HttpStatusCode.Conflict, "ECR-TMPL-0409").ConfigureAwait(true);
        Assert.Equal("err.ECR-TMPL-0409.discardVersionInUse", refusal.MessageKey);
        Assert.Contains("doc.Project", refusal.Body, StringComparison.Ordinal);
        Assert.Equal(TemplateVersionStatus.Draft, await StatusAsync(used.TemplateVersionId).ConfigureAwait(true));
        Assert.True(await SheetCountAsync(used.TemplateVersionId).ConfigureAwait(true) > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "TMPL-DISCARD")]
    public async Task Невикористану_чернетку_відкинуто_204_зі_структурою_і_слідом_у_журналі_безпеки()
    {
        var draftId = await UnusedDraftAsync().ConfigureAwait(true);
        Assert.True(await SheetCountAsync(draftId).ConfigureAwait(true) > 0);

        using var app = new EcrApiFactory(sql);
        using var publisher = await SystemHealthControllerTests.SignedInAsync(sql, app, Publisher).ConfigureAwait(true);

        using var response = await publisher.DeleteAsync(Url(draftId)).ConfigureAwait(true);
        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        Assert.False(await ExistsAsync(draftId).ConfigureAwait(true));
        Assert.Equal(0, await SheetCountAsync(draftId).ConfigureAwait(true));

        var details = await SecurityEventAsync(draftId).ConfigureAwait(true);
        Assert.NotNull(details);
        using var json = JsonDocument.Parse(details);
        Assert.Equal(draftId, json.RootElement.GetProperty("templateVersionId").GetInt32());
        Assert.True(json.RootElement.GetProperty("deleted").GetProperty("cfg.SheetDef").GetInt32() > 0);

        // Повтор — версії вже немає: 404, а не 500.
        using var again = await publisher.DeleteAsync(Url(draftId)).ConfigureAwait(true);
        await RefusalAsync(app, again, HttpStatusCode.NotFound, "ECR-TMPL-0404").ConfigureAwait(true);
    }

    private static Uri Url(int versionId) => new($"/api/v1/template-versions/{versionId}", UriKind.Relative);

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

    /// <summary>Клон-чернетка версії з документом: структура є, посилань поза конфігурацією немає.</summary>
    private async Task<int> UnusedDraftAsync()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: 1).ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await using var db = Db();
        return await new TemplateVersionStore(db).CloneAsync(
            chain.TemplateVersionId, $"8.{tag.GetHashCode() & 0xFFF}.0.1", 1,
            new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> ExistsAsync(int versionId)
    {
        await using var db = Db();
        return await db.TemplateVersions.AsNoTracking().AnyAsync(v => v.Id == versionId).ConfigureAwait(false);
    }

    private async Task<TemplateVersionStatus> StatusAsync(int versionId)
    {
        await using var db = Db();
        return await db.TemplateVersions.AsNoTracking()
            .Where(v => v.Id == versionId).Select(v => v.Status).SingleAsync().ConfigureAwait(false);
    }

    private async Task<int> SheetCountAsync(int versionId)
    {
        await using var db = Db();
        return await db.SheetDefs.AsNoTracking().CountAsync(s => s.TemplateVersionId == versionId).ConfigureAwait(false);
    }

    private async Task<string?> SecurityEventAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) DetailsJson FROM aud.SecurityEvent
            WHERE EventType = N'TemplateVersionDiscarded'
              AND JSON_VALUE(DetailsJson, '$.templateVersionId') = CAST(@id AS nvarchar(20))
            ORDER BY Id DESC
            """;
        command.Parameters.AddWithValue("@id", versionId);

        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
