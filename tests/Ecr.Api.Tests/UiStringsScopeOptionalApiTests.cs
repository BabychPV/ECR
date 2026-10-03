using System.Net;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// T2-09 (в): у контракті <c>GET /ui-strings/{lang}</c> параметр <c>scope</c> необов'язковий, а запит без
/// нього відповідав <c>422 malformedRequest</c> (клієнт завжди передає <c>scope</c>, тож розбіжність була
/// невидимою). Відсутній <c>scope</c> — приватна область: анонімові — 401 (а не 422), публічна — як і раніше 200.
/// </summary>
[Collection("SqlServer")]
public sealed class UiStringsScopeOptionalApiTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запит_без_scope_не_є_malformedRequest_а_закритою_приватною_областю()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var withoutScope = await client.GetAsync(new Uri("/api/v1/ui-strings/en", UriKind.Relative));
        var publicScope = await client.GetAsync(new Uri("/api/v1/ui-strings/en?scope=public", UriKind.Relative));

        Assert.True(
            withoutScope.StatusCode == HttpStatusCode.Unauthorized,
            $"{withoutScope.StatusCode}: {await withoutScope.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, publicScope.StatusCode);
    }
}
