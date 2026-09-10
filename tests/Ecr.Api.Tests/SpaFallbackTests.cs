using System.Net;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// SPA-хостинг (Q-214): застосунок сам віддає зібраний <c>src/Ecr.Web</c>
/// із <c>wwwroot</c>, і жодний невідомий <c>/api/**</c>-шлях при цьому не
/// підміняється сторінкою застосунку.
/// </summary>
[Collection("SqlServer")]
public sealed class SpaFallbackTests(SqlServerFixture sql)
{
    private const string SpaMarker = "SPA-MARKER-Q214";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомий_клієнтський_шлях_повертає_index_html()
    {
        var webRoot = CreateWebRootWithIndexHtml();
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();

            // "/documents/42" — форма клієнтського маршруту React Router,
            // не існуючий контролер: саме це має розв'язати SPA-фолбек.
            var response = await client.GetAsync(new Uri("/documents/42", UriKind.Relative));

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(SpaMarker, body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомий_api_шлях_лишається_404_не_index_html()
    {
        // ⛔ Класична пастка спільного хостингу SPA+API: без явного
        // виключення префіксів у MapFallbackToFile цей запит отримав би 200
        // з index.html замість 404 — контракт ECR-DOC-0404 порушився б
        // мовчки, і клієнт бачив би незрозумілу помилку парсингу JSON
        // замість чіткого "не знайдено".
        var webRoot = CreateWebRootWithIndexHtml();
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();

            var response = await client.GetAsync(
                new Uri("/api/v1/definitely-not-a-real-endpoint", UriKind.Relative));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(SpaMarker, body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Корінь_повертає_index_html()
    {
        // Порожній залишок шляху ("/") — окремий випадок від "/documents/42"
        // вище: реальний прогін показав, що єдиний regex-catch-all
        // (`{*path:nonfile:regex(...)}`) ламався саме на порожньому
        // залишку, хоча на непорожніх шляхах працював коректно (Q-214).
        var webRoot = CreateWebRootWithIndexHtml();
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.WithWebHostBuilder(b => b.UseWebRoot(webRoot)).CreateClient();

            var response = await client.GetAsync(new Uri("/", UriKind.Relative));

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(SpaMarker, body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static string CreateWebRootWithIndexHtml()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<!doctype html><html><body>{SpaMarker}</body></html>");
        return dir;
    }
}
