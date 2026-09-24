// tests/Ecr.Api.Tests/TemplateVersionNotFoundTests.cs
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Diff і структура НЕІСНУЮЧОЇ версії шаблону — <c>404</c> з поясненням, а
/// не <c>500</c> (V-17, UX-прохід 2026-09-24, третій раунд).
/// </summary>
/// <remarks>
/// ⛔ До виправлення <c>MetadataCache.RevisionAsync</c> на відсутню версію
/// кидав голий <c>InvalidOperationException</c>, і конвеєр помилок чесно
/// перетворював його на <c>500</c> «внутрішня помилка» — хоча причина цілком
/// користувацька (застаріле посилання, опечатка в полі «id» порівняння). Тест
/// іде крізь справжній конвеєр і справжню базу: шлях до відмови — кеш
/// метаданих, а не обробник, і підміна кешу довела б не те.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionNotFoundTests(SqlServerFixture sql)
{
    private const int Missing = int.MaxValue - 7;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Diff_з_неіснуючою_версією_і_структура_неіснуючої_версії_дають_404_з_ключем()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View");

        await AssertNotFoundAsync(client, $"/api/v1/template-versions/{doc.TemplateVersionId}/diff/{Missing}");
        await AssertNotFoundAsync(client, $"/api/v1/template-versions/{Missing}/diff/{doc.TemplateVersionId}");
        await AssertNotFoundAsync(client, $"/api/v1/template-versions/{Missing}/structure");
    }

    private static async Task AssertNotFoundAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url}: {(int)response.StatusCode} {body}");

        using var problem = JsonDocument.Parse(body);
        Assert.Equal("ECR-TMPL-0404", problem.RootElement.GetProperty("errorCode").GetString());

        // Пояснення — мовою користувача з ключа `err.ECR-TMPL-0404.templateVersion`,
        // а не запасне українське речення.
        var detail = problem.RootElement.GetProperty("detail").GetString();
        Assert.Contains($"Template version {Missing} was not found", detail, StringComparison.Ordinal);
    }
}
