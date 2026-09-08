using System.Net;
using System.Net.Http.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET .../structure</c> не сміє віддавати застарілий знімок чернетки.
/// </summary>
/// <remarks>
/// ⛔ Знайдено живим прогоном (`W5.9`, ручна звірка `S-04` в браузері):
/// `ETag` рахується як <c>v{id}:r{presentationRevision}</c> (`D-16`), а
/// структурний запис чернетки (`PUT .../sheets/{code}` та інші зрізи `W5`)
/// НЕ підіймає <c>PresentationRevision</c> — це не презентаційна правка.
/// Клієнт, що надіслав щойно отриманий `ETag` повторним запитом (типова
/// поведінка браузера з умовним GET), отримував `304` зі СТАРИМ тілом —
/// щойно доданий аркуш зникав із власного наступного читання.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateStructureCacheTests(SqlServerFixture sql)
{
    private const string LoginPassword = "Structure-Cache-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Структурний_запис_чернетки_видно_в_наступному_GET_навіть_з_умовним_ETag()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var (userName, templateVersionId) = await ArrangeAsync().ConfigureAwait(true);

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        var first = await client.GetAsync(
            new Uri($"/api/v1/template-versions/{templateVersionId}/structure", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag?.Tag;

        var addSheet = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{templateVersionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            }).ConfigureAwait(true);
        Assert.True(addSheet.IsSuccessStatusCode, $"{addSheet.StatusCode}: {app.ErrorsText}");

        // ⛔ Умовний GET — те, що робить реальний браузер сам, без жодної
        // участі клієнтського коду: `If-None-Match` із ETag попереднього
        // читання. Головне твердження тесту — саме тут.
        using var conditional = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"/api/v1/template-versions/{templateVersionId}/structure", UriKind.Relative));
        if (etag is not null)
        {
            conditional.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));
        }

        var second = await client.SendAsync(conditional).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().ConfigureAwait(true);
        var sheets = body.GetProperty("sheets").EnumerateArray().ToList();

        Assert.Contains(sheets, s => s.GetProperty("code").GetString() == "SHEET1");
    }

    /// <summary>Користувач із правом <c>Template.Edit</c>/<c>Template.View</c> і чернеткова версія без аркушів.</summary>
    private async Task<(string UserName, int TemplateVersionId)> ArrangeAsync()
    {
        var userName = $"structcache_{Guid.NewGuid():N}"[..20];

        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options);

        var user = new Ecr.Domain.Entities.Security.User(
            userName, userName, Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash(LoginPassword));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var role = new Ecr.Domain.Entities.Security.Role(
            Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Structure cache test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, "Template.Edit"));
        db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, "Template.View"));
        db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, user.Id, principalSid: null));

        var template = new Ecr.Domain.Entities.Configuration.Template(
            Ecr.Domain.ValueObjects.EcrCode.Create($"T{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Structure cache template" }),
            user.Id, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new Ecr.Domain.Entities.Configuration.TemplateVersion(
            template.Id, "1.0.0.0", user.Id, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userName, version.Id);
    }
}
