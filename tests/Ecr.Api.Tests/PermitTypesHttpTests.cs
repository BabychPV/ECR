// tests/Ecr.Api.Tests/PermitTypesHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-контракт <c>PermitTypesController</c> (Permits, PM-03): статуси 200/201/204, 403 за правом і відображення помилок
/// <c>ECR-PRM-*</c> у <c>ExceptionHandlingMiddleware</c> (0404→404, 0422→422, 4091/4093/4094→409).
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути в <c>PublishPermitTypeHandler.Permission</c> / <c>CreatePermitTypeHandler.Permission</c>
/// <c>Registry.Publish</c> / <c>Registry.EditDefinition</c> - «Адміністратор_реєстрів…» дістає 200/201 замість 403.
/// ⚠ Арми 4091/4094 у middleware тут НЕ несуть навантаження: 409 дає й правило суфікса коду, тож тест фіксує статус
/// контракту, а не конкретний арм.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypesHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повний_цикл_типу_дає_201_200_204_і_409_422_404_за_кодами_помилок()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType");
        var code = $"HTTP{_tag}";

        // 201 + Location, повторне заведення - 409 ECR-PRM-4091.
        var created = await client.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), Create(code));
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        Assert.EndsWith($"/permit-types/{code}", created.Headers.Location!.AbsolutePath, StringComparison.Ordinal);
        var rowVersion = (await Json(created)).GetProperty("rowVersion").GetString()!;

        var duplicate = await client.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), Create(code));
        await AssertErrorAsync(duplicate, HttpStatusCode.Conflict, "ECR-PRM-4091");

        // 200 читання; 404 ECR-PRM-0404 на невідомий код.
        var read = await client.GetAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        await AssertErrorAsync(await client.GetAsync(new Uri($"/api/v1/permit-types/NOPE{_tag}", UriKind.Relative)), HttpStatusCode.NotFound, "ECR-PRM-0404");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/permit-types?limit=50", UriKind.Relative))).StatusCode);

        // 422 ECR-PRM-0422: таблиця без ключового стовпця.
        var invalid = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}", UriKind.Relative), Replace(rowVersion, keyed: false));
        await AssertErrorAsync(invalid, HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");

        // 200 заміна чернетки; чужий токен - 409 ECR-PRM-4093.
        var replaced = await client.PutAsJsonAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative), Replace(rowVersion, keyed: true));
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, $"{replaced.StatusCode}: {app.ErrorsText}");
        var fresh = (await Json(replaced)).GetProperty("rowVersion").GetString()!;
        var stale = await client.PutAsJsonAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative), Replace(rowVersion, keyed: true));
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "ECR-PRM-4093");

        // 200 публікація; повторна - 409 ECR-PRM-4093 (немає чернетки); видалення опублікованого - 409 ECR-PRM-4094.
        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "перший випуск типу", rowVersion = fresh });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {app.ErrorsText}");
        var again = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "повторна публікація", rowVersion = fresh });
        await AssertErrorAsync(again, HttpStatusCode.Conflict, "ECR-PRM-4093");
        await AssertErrorAsync(await client.DeleteAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative)), HttpStatusCode.Conflict, "ECR-PRM-4094");

        // 200 нова версія; 204 видалення ніколи не опублікованого типу.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(new Uri($"/api/v1/permit-types/{code}/new-version", UriKind.Relative), null)).StatusCode);
        var draftOnly = $"DRAFT{_tag}";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), Create(draftOnly))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(new Uri($"/api/v1/permit-types/{draftOnly}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Адміністратор_реєстрів_і_читач_не_керують_типами_а_читач_читає()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Permit.EditType", "Permit.PublishType");
        var code = $"AUTH{_tag}";
        var created = await admin.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), Create(code));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var rowVersion = (await Json(created)).GetProperty("rowVersion").GetString()!;

        // Registry.EditDefinition/Publish (Land) типів дозволів не відкривають; редактор без публікації не публікує.
        using var registryAdmin = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition", "Registry.Publish");
        Assert.Equal(HttpStatusCode.Forbidden, (await registryAdmin.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), Create($"X{_tag}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await registryAdmin.PutAsJsonAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative), Replace(rowVersion, keyed: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await registryAdmin.DeleteAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await registryAdmin.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "публікація без права", rowVersion })).StatusCode);

        // Нова версія типу - теж лише з Permit.EditType (PM-04b): 403 для адміністратора реєстрів (Registry.EditDefinition/Publish).
        Assert.Equal(HttpStatusCode.Forbidden, (await registryAdmin.PostAsync(new Uri($"/api/v1/permit-types/{code}/new-version", UriKind.Relative), null)).StatusCode);

        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View", "Permit.EditType");
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "публікація без права", rowVersion })).StatusCode);

        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(new Uri($"/api/v1/permit-types/{code}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), Create($"Y{_tag}"))).StatusCode);

        // Читання теж потребує права: без Registry.View - 403.
        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app, "Document.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(new Uri("/api/v1/permit-types", UriKind.Relative))).StatusCode);
    }

    private static object Create(string code) => new { code, nameL10n = new { en = code }, category = "Air" };

    private static object Replace(string rowVersion, bool keyed)
        => new
        {
            rowVersion,
            nameL10n = new { en = "Http type" },
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
                            code = "Name",
                            nameL10n = new { en = "Name" },
                            dataType = "String",
                            unitId = (int?)null,
                            required = true,
                            key = keyed,
                            group = (string?)null,
                            precision = (int?)null,
                            scale = (int?)null,
                            listScope = (string?)null,
                            listRegistryCode = (string?)null,
                            siblingTableCode = (string?)null,
                            choices = (string[]?)null,
                            order = 1,
                        },
                    },
                    rules = Array.Empty<object>(),
                },
            },
            rules = Array.Empty<object>(),
        };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"Очікувалось {status}, а {response.StatusCode}: {body}");
        Assert.Equal(errorCode, JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }
}
