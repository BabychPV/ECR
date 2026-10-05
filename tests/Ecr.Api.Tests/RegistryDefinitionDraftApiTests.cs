// tests/Ecr.Api.Tests/RegistryDefinitionDraftApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Чернетка опису довідника → публікація (<c>BE-24</c> крок 2): чернетка не
/// видна в опублікованому описі, публікація застосовує її під <c>Registry.Publish</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class RegistryDefinitionDraftApiTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Чернетка_не_видна_до_публікації_а_публікація_застосовує_її()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition", "Registry.Publish");
        var (code, fieldId) = await SeedAsync();

        var saved = await PutDraftAsync(client, code, fieldId, "Draft name", rowVersion: null);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var rowVersion = (await Json(saved)).GetProperty("rowVersion").GetString();

        // ⛔ Предмет: опублікований опис і далі старий.
        var before = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition");
        Assert.Equal("Key", FieldName(before));
        Assert.Equal(1, before.GetProperty("definitionVersion").GetInt32());

        var state = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition/draft");
        Assert.Equal(rowVersion, state.GetProperty("draft").GetProperty("rowVersion").GetString());

        // Друге збереження з тією самою (вже застарілою) версією — 409.
        var stale = await PutDraftAsync(client, code, fieldId, "Other", rowVersion: null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("ECR-REG-0409", (await Json(stale)).GetProperty("errorCode").GetString());

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/definition/publish", UriKind.Relative), new { rowVersion });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {app.ErrorsText}");
        Assert.Equal(2, (await Json(published)).GetProperty("definitionVersion").GetInt32());

        var after = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition");
        Assert.Equal("Draft name", FieldName(after));
        var cleared = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition/draft");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("draft").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "L5-02")]
    public async Task Збереження_чернетки_після_прямої_публікації_дає_409()
    {
        // L5-02: форма збудована з опису версії 1, сусід тим часом опублікував опис напряму (версія 2).
        // Без звірки If-Match збереження перебазувало б чернетку на версію 2, і публікація мовчки
        // затерла б його правку.
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition", "Registry.Publish");
        var (code, fieldId) = await SeedAsync();

        var direct = await PutDefinitionAsync(client, code, fieldId, "Neighbour", "\"1\"");
        Assert.True(direct.StatusCode == HttpStatusCode.OK, $"{direct.StatusCode}: {await direct.Content.ReadAsStringAsync()}");

        var stale = await PutDraftAsync(client, code, fieldId, "Mine", rowVersion: null, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var error = await Json(stale);
        Assert.Equal("ECR-REG-0409", error.GetProperty("errorCode").GetString());
        Assert.Contains("definitionChanged", error.ToString(), StringComparison.Ordinal);

        // Чернетку не створено.
        var state = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition/draft");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("draft").ValueKind);

        // Без заголовка — 422, а не мовчазне збереження.
        var missing = await PutDraftAsync(client, code, fieldId, "Mine", rowVersion: null, ifMatch: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        // З актуальною версією (2) — збережено.
        var fresh = await PutDraftAsync(client, code, fieldId, "Mine", rowVersion: null, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Без_Registry_Publish_публікація_дає_403_і_опис_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition");
        var (code, fieldId) = await SeedAsync();

        var saved = await PutDraftAsync(client, code, fieldId, "Draft name", rowVersion: null);
        var rowVersion = (await Json(saved)).GetProperty("rowVersion").GetString();

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/definition/publish", UriKind.Relative), new { rowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, published.StatusCode);
        Assert.Equal("Key", FieldName(await GetJsonAsync(client, $"/api/v1/registries/{code}/definition")));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Скасування_чернетки_204_чужа_версія_409_повторне_404()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition");
        var (code, fieldId) = await SeedAsync();

        var saved = await PutDraftAsync(client, code, fieldId, "Draft name", rowVersion: null);
        var rowVersion = (await Json(saved)).GetProperty("rowVersion").GetString()!;

        var stale = await DeleteDraftAsync(client, code, "AAAAAAAAAAA=");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("ECR-REG-0409", (await Json(stale)).GetProperty("errorCode").GetString());

        var discarded = await DeleteDraftAsync(client, code, rowVersion);
        Assert.True(discarded.StatusCode == HttpStatusCode.NoContent, $"{discarded.StatusCode}: {app.ErrorsText}");

        var state = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition/draft");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("draft").ValueKind);
        var definition = await GetJsonAsync(client, $"/api/v1/registries/{code}/definition");
        Assert.Equal("Key", FieldName(definition));
        Assert.Equal(1, definition.GetProperty("definitionVersion").GetInt32());

        var again = await DeleteDraftAsync(client, code, rowVersion);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("ECR-REG-0404", (await Json(again)).GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Скасування_без_Registry_EditDefinition_дає_403()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Registry.View", "Registry.EditDefinition");
        var (code, fieldId) = await SeedAsync();
        var saved = await PutDraftAsync(editor, code, fieldId, "Draft name", rowVersion: null);
        var rowVersion = (await Json(saved)).GetProperty("rowVersion").GetString()!;

        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        var denied = await DeleteDraftAsync(viewer, code, rowVersion);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var state = await GetJsonAsync(viewer, $"/api/v1/registries/{code}/definition/draft");
        Assert.Equal(rowVersion, state.GetProperty("draft").GetProperty("rowVersion").GetString());
    }

    private static Task<HttpResponseMessage> DeleteDraftAsync(HttpClient client, string code, string rowVersion)
        => client.DeleteAsync(new Uri(
            $"/api/v1/registries/{code}/definition/draft?rowVersion={Uri.EscapeDataString(rowVersion)}",
            UriKind.Relative));

    /// <summary>`PUT …/definition/draft`; <paramref name="ifMatch"/> — версія опису (за замовчуванням 1), <c>null</c> — без заголовка.</summary>
    private static Task<HttpResponseMessage> PutDraftAsync(
        HttpClient client, string code, int fieldId, string name, string? rowVersion, string? ifMatch = "\"1\"")
        => SendAsync(client, "definition/draft", code, fieldId, name, ifMatch, rowVersion);

    /// <summary>Пряме `PUT …/definition` («зберегти й одразу опублікувати»).</summary>
    private static Task<HttpResponseMessage> PutDefinitionAsync(
        HttpClient client, string code, int fieldId, string name, string ifMatch)
        => SendAsync(client, "definition", code, fieldId, name, ifMatch, rowVersion: null);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, string route, string code, int fieldId, string name, string? ifMatch, string? rowVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/registries/{code}/{route}", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                fields = new[]
                {
                    new
                    {
                        id = fieldId, code = "Key", nameL10n = new { values = new { en = name } },
                        dataType = "String", ordinal = 1, isRequired = false, isKey = true,
                        lookupRegistryDefId = (int?)null, unitId = (int?)null,
                    },
                },
                rules = Array.Empty<object>(),
                reason = "rename key",
                rowVersion,
            }),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static string? FieldName(JsonElement definition)
        => definition.GetProperty("fields")[0].GetProperty("nameL10n").GetProperty("values").GetProperty("en").GetString();

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(string Code, int FieldId)> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var registry = new RegistryDef(EcrCode.Create($"RDRF{tag}"), Text("Draft test"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("Key"), Text("Key"), CellDataType.String, 1);
        field.MarkKey(true);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();

        return (registry.Code, field.Id);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
