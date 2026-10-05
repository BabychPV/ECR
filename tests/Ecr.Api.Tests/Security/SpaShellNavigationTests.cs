using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Повне завантаження сторінки віддає застосунок, а не сирий JSON воріт сесії
/// (<c>A1-01</c>, <c>A1-09</c>, приймальний прохід A1).
/// </summary>
/// <remarks>
/// ⛔ До фіксу F5, нова вкладка чи закладка (навіть <c>/login</c>) з разовим
/// паролем давали <c>428 ECR-PWD-0428</c> JSON-ом, а після зміни прав —
/// <c>401</c>: SPA не вантажився, і з цього стану не було виходу без ручного
/// очищення cookie. Друга половина кожного тесту — що <c>/api/**</c> ворота
/// тримають як і раніше: пропуск оболонки не мав відчинити дані.
/// </remarks>
[Collection("SqlServer")]
public sealed class SpaShellNavigationTests(SqlServerFixture sql)
{
    private const string Password = "Spa-Shell-Nav-2026!";
    private const string SpaMarker = "SPA-MARKER-A1-01";

    private readonly string _name = $"spa_{Guid.NewGuid():N}"[..20];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.18")]
    [Trait("Finding", "A1-01")]
    public async Task Разовий_пароль_повне_завантаження_сторінки_дає_застосунок_а_api_428()
    {
        await ArrangeUserAsync(mustChangePassword: true).ConfigureAwait(true);
        var webRoot = CreateWebRoot();
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();
            await LoginAsync(client, app).ConfigureAwait(true);

            foreach (var path in new[] { "/", "/login", "/documents/42", "/change-password" })
            {
                await AssertShellAsync(client, path).ConfigureAwait(true);
            }

            // Ворота на API — як і раніше.
            var api = await client.GetAsync(new Uri("/api/v1/audit/security", UriKind.Relative)).ConfigureAwait(true);
            var body = await api.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(api.StatusCode == HttpStatusCode.PreconditionRequired, $"{api.StatusCode}: {body}");
            Assert.Equal("ECR-PWD-0428", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());

            // А звідси застосунок і дізнається, що треба на зміну пароля.
            var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);
            var meBody = await me.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(me.StatusCode == HttpStatusCode.OK, $"{me.StatusCode}: {meBody}");
            Assert.True(JsonDocument.Parse(meBody).RootElement.GetProperty("mustChangePassword").GetBoolean());
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    [Trait("Finding", "A1-09")]
    public async Task Застарілий_штамп_повне_завантаження_сторінки_дає_застосунок_а_api_401_з_причиною()
    {
        await ArrangeUserAsync(mustChangePassword: false).ConfigureAwait(true);
        var webRoot = CreateWebRoot();
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();
            await LoginAsync(client, app).ConfigureAwait(true);

            // Зміна прав крутить штамп — чинна cookie стає застарілою.
            await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
            {
                var user = await db.Users.FirstAsync(u => u.UserName == _name).ConfigureAwait(true);
                user.RefreshSecurityStamp();
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            foreach (var path in new[] { "/", "/login", "/documents/42" })
            {
                await AssertShellAsync(client, path).ConfigureAwait(true);
            }

            // ⚠ Причину клієнт має отримати саме тут — тому оболонка не
            // розлогінює: інакше `/api/v1/me` віддав би голий анонімний 401.
            var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);
            var body = await me.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(me.StatusCode == HttpStatusCode.Unauthorized, $"{me.StatusCode}: {body}");
            Assert.Contains("err.ECR-AUTH-0401.securityStampStale", body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static async Task AssertShellAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode}: {body}");
        Assert.Contains(SpaMarker, body, StringComparison.Ordinal);
    }

    private async Task LoginAsync(HttpClient client, EcrApiFactory app)
    {
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = _name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
    }

    private async Task ArrangeUserAsync(bool mustChangePassword)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = new User(_name, _name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        if (mustChangePassword)
        {
            user.RequirePasswordChange();
        }

        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static string CreateWebRoot()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<!doctype html><html><body>{SpaMarker}</body></html>");
        return dir;
    }
}
