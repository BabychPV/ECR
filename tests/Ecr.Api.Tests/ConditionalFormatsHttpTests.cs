// tests/Ecr.Api.Tests/ConditionalFormatsHttpTests.cs
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
/// ФВ-2.7 наскрізно: <c>GET/PUT /api/v1/template-versions/{id}/conditional-formats</c> —
/// <c>ETag</c> / <c>If-Match</c>, заморожена версія, права, невалідне правило.
/// Справжній SQL, вхід, HTTP (обробник перевіряють юніт-тести, цей клас — шов із контролером).
/// </summary>
[Collection("SqlServer")]
public sealed class ConditionalFormatsHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-CondFormat-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task GET_віддає_ETag_а_PUT_з_правильним_If_Match_зберігає_і_віддає_новий_ETag()
    {
        var s = await ArrangeAsync(["Template.View", "Template.Edit"]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var get = await client.GetAsync(Url(s)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var etag = get.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag), "GET не віддав ETag.");

        var put = await PutAsync(client, s, etag, [Rule(s.ColumnCode)]).ConfigureAwait(true);
        var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(put.StatusCode == HttpStatusCode.OK, $"{put.StatusCode}: {body}\n{app.ErrorsText}");
        var newEtag = put.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(newEtag));
        Assert.NotEqual(etag, newEtag);

        // Збережене читається назад і несе той самий ETag.
        var again = await client.GetAsync(Url(s)).ConfigureAwait(true);
        Assert.Equal(newEtag, again.Headers.ETag?.Tag);
        var rules = JsonDocument.Parse(await again.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal(s.ColumnCode, rules[0].GetProperty("columnCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task PUT_зі_застарілим_If_Match_дає_409_condFormatChanged_з_актуальною_версією()
    {
        var s = await ArrangeAsync(["Template.View", "Template.Edit"]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var stale = (await client.GetAsync(Url(s)).ConfigureAwait(true)).Headers.ETag?.Tag;
        var first = await PutAsync(client, s, stale, [Rule(s.ColumnCode)]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PutAsync(client, s, stale, []).ConfigureAwait(true);
        var body = await second.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(second.StatusCode == HttpStatusCode.Conflict, $"{second.StatusCode}: {body}\n{app.ErrorsText}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-TMPL-0409", root.GetProperty("errorCode").GetString());
        Assert.Contains("err.ECR-TMPL-0409.condFormatChanged", body, StringComparison.Ordinal);
        Assert.Equal(first.Headers.ETag?.Tag?.Trim('"'), FindVersion(root));

        // Відхилена заміна нічого не стерла.
        var after = await client.GetAsync(Url(s)).ConfigureAwait(true);
        var rules = JsonDocument.Parse(await after.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(1, rules.GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task PUT_без_If_Match_дає_422_condFormatIfMatch()
    {
        var s = await ArrangeAsync(["Template.View", "Template.Edit"]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var put = await PutAsync(client, s, null, [Rule(s.ColumnCode)]).ConfigureAwait(true);
        var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{put.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("err.ECR-REQ-0422.condFormatIfMatch", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task PUT_на_опублікованій_версії_відхиляється_409_ECR_TMPL_0409()
    {
        var s = await ArrangeAsync(["Template.View", "Template.Edit"]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);
        var etag = (await client.GetAsync(Url(s)).ConfigureAwait(true)).Headers.ETag?.Tag;

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == s.VersionId).ConfigureAwait(true);
            version.Publish(s.UserId, DateTime.UtcNow);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var put = await PutAsync(client, s, etag, [Rule(s.ColumnCode)]).ConfigureAwait(true);
        var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(put.StatusCode == HttpStatusCode.Conflict, $"{put.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("ECR-TMPL-0409", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());

        var after = await client.GetAsync(Url(s)).ConfigureAwait(true);
        var rules = JsonDocument.Parse(await after.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(0, rules.GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Без_Template_Edit_PUT_дає_403_а_GET_за_Template_View_працює()
    {
        var s = await ArrangeAsync(["Template.View"]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var get = await client.GetAsync(Url(s)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var put = await PutAsync(client, s, get.Headers.ETag?.Tag, [Rule(s.ColumnCode)]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Правило_з_невідомою_колонкою_дає_422_і_нічого_не_зберігає()
    {
        var s = await ArrangeAsync(["Template.View", "Template.Edit"]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);
        var etag = (await client.GetAsync(Url(s)).ConfigureAwait(true)).Headers.ETag?.Tag;

        var put = await PutAsync(client, s, etag, [Rule("NO_SUCH_COLUMN")]).ConfigureAwait(true);
        var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{put.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("err.ECR-CFG-0422.condFormatColumn", body, StringComparison.Ordinal);

        var after = await client.GetAsync(Url(s)).ConfigureAwait(true);
        Assert.Equal(etag, after.Headers.ETag?.Tag);
    }

    private static Uri Url(Scenario s) => new($"/api/v1/template-versions/{s.VersionId}/conditional-formats", UriKind.Relative);

    private static object Rule(string columnCode) => new
    {
        columnCode,
        @operator = "gt",
        value = "10",
        valueTo = (string?)null,
        backgroundHex = "#ffcc00",
        foregroundHex = (string?)null,
        isBold = true,
    };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Scenario s, string? ifMatch, object[] rules)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url(s)) { Content = JsonContent.Create(new { rules }) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static string? FindVersion(JsonElement root)
    {
        // `details.version` — місце, куди обробник кладе актуальну версію; форма обгортки — за ExceptionHandlingMiddleware.
        if (root.TryGetProperty("details", out var d) && d.TryGetProperty("version", out var v))
        {
            return v.GetString();
        }

        return root.TryGetProperty("version", out var top) ? top.GetString() : null;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<Scenario> ArrangeAsync(IEnumerable<string> permissions)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync().ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var columnCode = await db.ColumnDefs.Where(c => c.Id == document.ColumnDefIds[0])
            .Select(c => c.Code).SingleAsync().ConfigureAwait(false);

        var userName = $"cf_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"CF_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Template editor" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.TemplateVersionId, columnCode, userName, user.Id);
    }

    private sealed record Scenario(int VersionId, string ColumnCode, string UserName, int UserId);
}
