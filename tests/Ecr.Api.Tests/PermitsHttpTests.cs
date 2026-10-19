// tests/Ecr.Api.Tests/PermitsHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HTTP-контракт <c>PermitsController</c> (Permits, PM-04): статуси 200/201/204, 403, 404, 409, 422 і необов'язкове тіло
/// поновлення.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути в <c>CreatePermitHandler.Permission</c> право, якого немає в читача (напр. <c>Registry.View</c>) -
/// «Читач…» дістає 201 замість 403.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitsHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повний_цикл_дозволу_через_HTTP_дає_201_200_204_і_409_422_404()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        var type = await PublishTypeAsync(client, app);

        // 201 + Location; повторення (той самий номер і початок) — 409 ECR-PRM-4095; погане вікно — 422 ECR-PRM-0422.
        var created = await client.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-1", "2026-01-01", "2027-01-01"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var permit = await Json(created);
        var id = permit.GetProperty("id").GetInt64();
        Assert.EndsWith($"/permits/{id}", created.Headers.Location!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Draft", permit.GetProperty("status").GetString());

        await AssertErrorAsync(
            await client.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-1", "2026-01-01", "2027-01-01")),
            HttpStatusCode.Conflict, "ECR-PRM-4095");
        await AssertErrorAsync(
            await client.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-2", "2027-01-01", "2026-01-01")),
            HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");

        // 200 читання і перелік; 404 ECR-PRM-0404.
        var read = await client.GetAsync(new Uri($"/api/v1/permits/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var rowVersion = (await Json(read)).GetProperty("rowVersion").GetString()!;
        var list = await Json(await client.GetAsync(new Uri($"/api/v1/permits?type={type}&status=Draft", UriKind.Relative)));
        Assert.Equal(id, list.GetProperty("items")[0].GetProperty("id").GetInt64());
        await AssertErrorAsync(await client.GetAsync(new Uri("/api/v1/permits/999999999", UriKind.Relative)), HttpStatusCode.NotFound, "ECR-PRM-0404");

        // 200 зміна; чужий токен — 409 ECR-PRM-4096.
        var changed = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permits/{id}", UriKind.Relative),
            new { rowVersion, number = "HT-1", location = "Edited", issueDate = (string?)null, validFrom = "2026-01-01", validTo = "2027-01-01", actualEnd = (string?)null, reportingFlags = (object?)null });
        Assert.True(changed.StatusCode == HttpStatusCode.OK, $"{changed.StatusCode}: {app.ErrorsText}");
        var stale = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permits/{id}", UriKind.Relative),
            new { rowVersion, number = "HT-1", location = "Again", issueDate = (string?)null, validFrom = "2026-01-01", validTo = "2027-01-01", actualEnd = (string?)null, reportingFlags = (object?)null });
        await AssertErrorAsync(stale, HttpStatusCode.Conflict, "ECR-PRM-4096");

        // 200 активація; повторна — 409 ECR-PRM-4096 (не чернетка); чинний не видаляється.
        var activated = await client.PostAsync(new Uri($"/api/v1/permits/{id}/activate", UriKind.Relative), null);
        Assert.True(activated.StatusCode == HttpStatusCode.OK, $"{activated.StatusCode}: {app.ErrorsText}");
        Assert.Equal("Active", (await Json(activated)).GetProperty("status").GetString());
        await AssertErrorAsync(await client.PostAsync(new Uri($"/api/v1/permits/{id}/activate", UriKind.Relative), null), HttpStatusCode.Conflict, "ECR-PRM-4096");
        await AssertErrorAsync(await client.DeleteAsync(new Uri($"/api/v1/permits/{id}", UriKind.Relative)), HttpStatusCode.Conflict, "ECR-PRM-4096");

        // 201 поновлення без тіла — нова чернетка; друге — 409 ECR-PRM-4095; чернетка видаляється 204.
        var renewed = await client.PostAsync(new Uri($"/api/v1/permits/{id}/renew", UriKind.Relative), null);
        Assert.True(renewed.StatusCode == HttpStatusCode.Created, $"{renewed.StatusCode}: {app.ErrorsText}");
        var next = await Json(renewed);
        Assert.Equal(("Draft", "2027-01-01", "2028-01-01"), (next.GetProperty("status").GetString(), next.GetProperty("validFrom").GetString(), next.GetProperty("validTo").GetString()));
        await AssertErrorAsync(await client.PostAsync(new Uri($"/api/v1/permits/{id}/renew", UriKind.Relative), null), HttpStatusCode.Conflict, "ECR-PRM-4095");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(new Uri($"/api/v1/permits/{next.GetProperty("id").GetInt64()}", UriKind.Relative))).StatusCode);

        // 422 коротка причина; 200 завершення.
        await AssertErrorAsync(
            await client.PostAsJsonAsync(new Uri($"/api/v1/permits/{id}/end", UriKind.Relative), new { reason = "коротко", endDate = "2026-12-01" }),
            HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");
        var ended = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permits/{id}/end", UriKind.Relative), new { reason = "об'єкт виведено з експлуатації", endDate = "2026-12-01" });
        Assert.True(ended.StatusCode == HttpStatusCode.OK, $"{ended.StatusCode}: {app.ErrorsText}");
        Assert.Equal("2026-12-01", (await Json(ended)).GetProperty("actualEnd").GetString());
    }

    /// <summary>
    /// Клієнт пише рядки таблиці дозволу через <c>entries/batch</c> з <c>code = null</c> (e2e 39279368294): реєстр таблиці
    /// створюється з <c>CodeMode = Auto</c>, тож рядок записується (<c>applied</c>), а код видає послідовність.
    /// Мутаційний доказ: прибрати <c>UseCodeMode(Auto)</c> у <c>PublishPermitTypeHandler</c> - червоніє
    /// (<c>applied = false</c>, <c>ECR-CFG-0422 invalidCode</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_таблиці_дозволу_без_коду_записується_бо_реєстр_таблиці_в_режимі_Auto()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        var type = await PublishTypeAsync(client, app);
        var created = await client.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-AUTO", "2026-01-01", "2027-01-01"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var permitId = (await Json(created)).GetProperty("id").GetInt64();
        var registry = $"PRM_{type}_Rows";

        var definition = await client.GetAsync(new Uri($"/api/v1/registries/{registry}/definition", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        Assert.Equal("Auto", (await Json(definition)).GetProperty("codeMode").GetString());

        var batch = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry}/entries/batch?dryRun=false", UriKind.Relative),
            new
            {
                items = new[]
                {
                    new
                    {
                        clientRowId = "save", op = "upsert", id = (long?)null, code = (string?)null, baseVersion = (string?)null,
                        values = new Dictionary<string, string?> { ["PERMIT"] = permitId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Name"] = "Outfall 1" },
                    },
                },
            });
        var body = await Json(batch);
        Assert.True(batch.StatusCode == HttpStatusCode.OK && body.GetProperty("applied").GetBoolean(), $"{batch.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(1, body.GetProperty("added").GetInt32());
        Assert.Equal(0, body.GetProperty("rows")[0].GetProperty("errors").GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_читає_але_не_змінює_дозволи_а_без_права_перегляду_403()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        var type = await PublishTypeAsync(editor, app);
        var created = await editor.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-R", "2026-01-01", "2027-01-01"));
        var id = (await Json(created)).GetProperty("id").GetInt64();

        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(new Uri($"/api/v1/permits/{id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-X", "2026-01-01", "2027-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(new Uri($"/api/v1/permits/{id}/activate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(new Uri($"/api/v1/permits/{id}", UriKind.Relative))).StatusCode);

        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app, "Document.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(new Uri("/api/v1/permits", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_не_змінює_завершує_і_не_поновлює_дозвіл_403()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        var type = await PublishTypeAsync(editor, app);
        var created = await Json(await editor.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-F", "2026-01-01", "2027-01-01")));
        var id = created.GetProperty("id").GetInt64();
        var rowVersion = created.GetProperty("rowVersion").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync(new Uri($"/api/v1/permits/{id}/activate", UriKind.Relative), null)).StatusCode);

        // Читач без Registry.EditData: PUT, end і renew - 403 (чинний дозвіл існує, тож це саме право, а не 404/409).
        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        var put = await reader.PutAsJsonAsync(
            new Uri($"/api/v1/permits/{id}", UriKind.Relative),
            new { rowVersion, number = "HT-F", location = "X", issueDate = (string?)null, validFrom = "2026-01-01", validTo = "2027-01-01", actualEnd = (string?)null, reportingFlags = (object?)null });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var end = await reader.PostAsJsonAsync(
            new Uri($"/api/v1/permits/{id}/end", UriKind.Relative), new { reason = "об'єкт виведено з експлуатації", endDate = "2026-12-01" });
        Assert.Equal(HttpStatusCode.Forbidden, end.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(new Uri($"/api/v1/permits/{id}/renew", UriKind.Relative), null)).StatusCode);

        // Нічого не змінилось: дозвіл і далі чинний, без завершення.
        var after = await Json(await editor.GetAsync(new Uri($"/api/v1/permits/{id}", UriKind.Relative)));
        Assert.Equal("Active", after.GetProperty("status").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дії_над_неіснуючим_дозволом_дають_404_ECR_PRM_0404()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        const string missing = "/api/v1/permits/999999999";

        await AssertErrorAsync(await editor.PostAsync(new Uri($"{missing}/activate", UriKind.Relative), null), HttpStatusCode.NotFound, "ECR-PRM-0404");
        await AssertErrorAsync(
            await editor.PostAsJsonAsync(new Uri($"{missing}/end", UriKind.Relative), new { reason = "об'єкт виведено з експлуатації", endDate = "2026-12-01" }),
            HttpStatusCode.NotFound, "ECR-PRM-0404");
        await AssertErrorAsync(await editor.PostAsync(new Uri($"{missing}/renew", UriKind.Relative), null), HttpStatusCode.NotFound, "ECR-PRM-0404");
        await AssertErrorAsync(
            await editor.PutAsJsonAsync(
                new Uri(missing, UriKind.Relative),
                new { rowVersion = "AAAAAAAAB9E=", number = "HT-0", location = (string?)null, issueDate = (string?)null, validFrom = "2026-01-01", validTo = "2027-01-01", actualEnd = (string?)null, reportingFlags = (object?)null }),
            HttpStatusCode.NotFound, "ECR-PRM-0404");
        await AssertErrorAsync(await editor.DeleteAsync(new Uri(missing, UriKind.Relative)), HttpStatusCode.NotFound, "ECR-PRM-0404");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Завершення_нечинного_дозволу_409_notActive_а_активація_з_перетином_409_ECR_PRM_4092()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType");
        var type = await PublishTypeAsync(editor, app);

        // Чернетка не чинна: завершення - 409 ECR-PRM-4096 notActive.
        var draft = await Json(await editor.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-N", "2026-01-01", "2027-01-01")));
        var end = await editor.PostAsJsonAsync(
            new Uri($"/api/v1/permits/{draft.GetProperty("id").GetInt64()}/end", UriKind.Relative),
            new { reason = "об'єкт виведено з експлуатації", endDate = "2026-12-01" });
        await AssertErrorAsync(end, HttpStatusCode.Conflict, "ECR-PRM-4096");
        Assert.Contains("notActive", await end.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Два дозволи одного типу й номера з вікнами, що перетинаються: другий не активується - 409 ECR-PRM-4092.
        var first = await Json(await editor.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-O", "2026-01-01", "2027-01-01")));
        var second = await Json(await editor.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-O", "2026-06-01", "2027-06-01")));
        Assert.Equal(
            HttpStatusCode.OK,
            (await editor.PostAsync(new Uri($"/api/v1/permits/{first.GetProperty("id").GetInt64()}/activate", UriKind.Relative), null)).StatusCode);
        await AssertErrorAsync(
            await editor.PostAsync(new Uri($"/api/v1/permits/{second.GetProperty("id").GetInt64()}/activate", UriKind.Relative), null),
            HttpStatusCode.Conflict, "ECR-PRM-4092");
    }

    private async Task<string> PublishTypeAsync(HttpClient client, EcrApiFactory app)
    {
        var code = $"HP{_tag}";
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
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "публікація для тесту дозволів", rowVersion = fresh });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {app.ErrorsText}");
        return code;
    }

    private static object Create(string typeCode, string number, string from, string to)
        => new { typeCode, number, location = (string?)null, issueDate = (string?)null, validFrom = from, validTo = to, reportingFlags = (object?)null };

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"Очікувалось {status}, а {response.StatusCode}: {body}");
        Assert.Equal(errorCode, JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }
}
