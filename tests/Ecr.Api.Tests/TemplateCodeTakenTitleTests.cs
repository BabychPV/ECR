// tests/Ecr.Api.Tests/TemplateCodeTakenTitleTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Приймальна №8, D-9: <c>POST /api/v1/templates</c> із зайнятим кодом давав <c>409 ECR-TMPL-0409</c> із
/// заголовком «The template version is published» (заголовок коду, не цього стану); <c>detail</c> був правильний.
/// Тепер стан має власний заголовок через ключ <c>err.ECR-TMPL-0409.templateCodeTaken.title</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class TemplateCodeTakenTitleTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-9")]
    public async Task Зайнятий_код_шаблону_дає_409_з_власним_заголовком_а_не_заголовком_опублікованої_версії()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.Edit").ConfigureAwait(true);

        var code = $"D9_{Guid.NewGuid():N}"[..20];
        var body = new { code, nameL10n = new Dictionary<string, string> { ["en"] = "D-9 template" } };

        using (var first = await editor.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), body).ConfigureAwait(true))
        {
            Assert.True(
                first.StatusCode == HttpStatusCode.Created,
                $"Перше створення: {first.StatusCode}\n{await first.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        }

        using var second = await editor.PostAsJsonAsync(new Uri("/api/v1/templates", UriKind.Relative), body).ConfigureAwait(true);
        var text = await second.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(second.StatusCode == HttpStatusCode.Conflict, $"{second.StatusCode}\n{text}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ECR-TMPL-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("Template code is already in use", problem.GetProperty("title").GetString());
        Assert.Contains(code, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
