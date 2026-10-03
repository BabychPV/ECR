using System.Net;
using System.Text.Json;
using Ecr.Api.Controllers;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// T2-09 (в): у контракті <c>GET /ui-strings/{lang}</c> параметр <c>scope</c> необов'язковий, а запит без нього
/// відповідав <c>422</c>. Тепер: анонім без <c>scope</c> — ПУБЛІЧНА область (екран входу не ламається; клієнт і так
/// завжди передає <c>scope</c>, але зовнішній споживач — ні), автентифікований — приватна; явне не-"public" — приватна.
/// Приватна область (D-114): підписи адміністративних областей і назви прав — анонім їх не бачить.
/// </summary>
[Collection("SqlServer")]
public sealed class UiStringsScopeOptionalApiTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData("public", false, true)]
    [InlineData("PUBLIC", true, true)]
    [InlineData("private", false, false)]
    [InlineData("privat", false, false)]
    [InlineData("", false, false)]
    public void Яку_область_віддавати(string? scope, bool authenticated, bool expectedPublic)
        => Assert.Equal(expectedPublic, UiStringsController.IsPublicOnly(scope, authenticated));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Анонім_без_scope_отримує_лише_публічні_ключі_як_і_зі_scope_public()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var without = await client.GetAsync(new Uri("/api/v1/ui-strings/en", UriKind.Relative));
        var pub = await client.GetAsync(new Uri("/api/v1/ui-strings/en?scope=public", UriKind.Relative));
        var priv = await client.GetAsync(new Uri("/api/v1/ui-strings/en?scope=private", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pub.StatusCode);

        // Анонімові приватна область закрита й за явним scope.
        Assert.Equal(HttpStatusCode.Unauthorized, priv.StatusCode);

        var withoutKeys = await KeysAsync(without);
        var publicKeys = await KeysAsync(pub);

        Assert.Contains("login.title", publicKeys);
        Assert.Equal(publicKeys, withoutKeys);
    }

    private static async Task<List<string>> KeysAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return [.. doc.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }
}
