// tests/Ecr.Api.Tests/PermitTypeUsageHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-контракт <c>GET /api/v1/permit-types/{code}/usage</c> (Permits, PM-03b2): 200 з кількостями, 403 без
/// <c>Registry.View</c>, 404 на невідомий тип.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>GetPermitTypeUsageHandler.Permission</c> поставити право, якого немає в читача
/// (<c>Permit.EditType</c>) - «Читач…» дістає 403 замість 200; прибрати у <c>PermitTypesController.Usage</c> атрибут
/// <c>[HttpGet("{code}/usage")]</c> - усі три тести червоні (маршрут зникає, 404/405).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeUsageHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Використання_типу_рахує_дозволи_а_читач_лише_читає()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        var code = await PublishTypeAsync(editor, app);
        var route = new Uri($"/api/v1/permit-types/{code}/usage", UriKind.Relative);

        var empty = await Json(await editor.GetAsync(route));
        Assert.Equal(0, empty.GetProperty("instanceCount").GetInt32());
        var table = Assert.Single(empty.GetProperty("tables").EnumerateArray());
        Assert.Equal(("Rows", 0), (table.GetProperty("code").GetString(), table.GetProperty("rowCount").GetInt32()));

        foreach (var number in new[] { "U-1", "U-2" })
        {
            var created = await editor.PostAsJsonAsync(
                new Uri("/api/v1/permits", UriKind.Relative),
                new { typeCode = code, number, location = (string?)null, issueDate = (string?)null, validFrom = "2026-01-01", validTo = "2027-01-01", reportingFlags = (object?)null });
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        }

        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        var counted = await reader.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, counted.StatusCode);
        Assert.Equal(2, (await Json(counted)).GetProperty("instanceCount").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Registry_View_403_а_невідомий_тип_404()
    {
        using var app = new EcrApiFactory(sql);
        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app, "Document.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(new Uri($"/api/v1/permit-types/ANY{_tag}/usage", UriKind.Relative))).StatusCode);

        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        var missing = await reader.GetAsync(new Uri($"/api/v1/permit-types/NOPE{_tag}/usage", UriKind.Relative));
        var body = await missing.Content.ReadAsStringAsync();
        Assert.True(missing.StatusCode == HttpStatusCode.NotFound, $"{missing.StatusCode}: {body}");
        Assert.Equal("ECR-PRM-0404", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    private async Task<string> PublishTypeAsync(HttpClient client, EcrApiFactory app)
    {
        var code = $"US{_tag}";
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = code }, category = "Air" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rowVersion = (await Json(created)).GetProperty("rowVersion").GetString()!;

        var replaced = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}", UriKind.Relative),
            new
            {
                rowVersion,
                nameL10n = new { en = code },
                category = "Air",
                tables = new[]
                {
                    new
                    {
                        code = "Rows",
                        nameL10n = new { en = "Rows" },
                        order = 1,
                        columns = new[]
                        {
                            new
                            {
                                code = "Name", nameL10n = new { en = "Name" }, dataType = "String", unitId = (int?)null, required = true,
                                key = true, group = (string?)null, precision = (int?)null, scale = (int?)null, listScope = (string?)null,
                                listRegistryCode = (string?)null, siblingTableCode = (string?)null, choices = (string[]?)null, order = 1,
                            },
                        },
                        rules = Array.Empty<object>(),
                    },
                },
                rules = Array.Empty<object>(),
            });
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, $"{replaced.StatusCode}: {app.ErrorsText}");
        var fresh = (await Json(replaced)).GetProperty("rowVersion").GetString()!;
        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "публікація для тесту кількостей", rowVersion = fresh });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {app.ErrorsText}");
        return code;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
}
