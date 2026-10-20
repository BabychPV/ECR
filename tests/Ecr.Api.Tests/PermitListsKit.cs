// tests/Ecr.Api.Tests/PermitListsKit.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Спільні кроки HTTP-тестів списків вибору дозволів (Permits, PM-08e): заведення довідника і типу, виклики, розбір.</summary>
internal static class PermitListsKit
{
    internal static readonly string[] AllRights = ["Registry.View", "Registry.EditData", "Permit.EditType", "Permit.PublishType"];

    /// <summary>Спільний довідник з одним записом <c>E1</c>.</summary>
    internal static async Task<string> EnsureSharedRegistryAsync(SqlServerFixture sql, string code)
    {
        await using var db = sql.CreateContext();
        var def = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();
        db.RegistryFieldDefs.Add(new RegistryFieldDef(def.Id, EcrCode.Create("Name"), Text("Name"), CellDataType.String, 1));
        db.RegistryEntries.Add(new RegistryEntry(def.Id, EcrCode.Create("E1"), Text("Entry 1"), 1, DateTime.UnixEpoch));
        await db.SaveChangesAsync();
        return code;
    }

    internal static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });

    /// <summary>Заводить, наповнює й публікує тип з таблицею Rows: ключ, «лише для типу» Kind, спільний Site (і сестринський Place).</summary>
    internal static async Task PublishTypeAsync(HttpClient client, string code, string sharedCode, string[] choices, bool withSibling = false)
    {
        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = code }, category = "Air" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var rowVersion = (await Json(create)).GetProperty("rowVersion").GetString()!;

        static object Column(string c, string type, bool key, string? scope = null, string? registry = null, string[]? choices = null, string? sibling = null, int order = 1)
            => new
            {
                code = c, nameL10n = new { en = c }, dataType = type, unitId = (int?)null, required = key, key, group = (string?)null,
                precision = (int?)null, scale = (int?)null, listScope = scope, listRegistryCode = registry, siblingTableCode = sibling,
                choices, order,
            };

        var columns = new List<object>
        {
            Column("Name", "String", key: true),
            Column("Kind", "Lookup", key: false, scope: "TypeOnly", choices: choices, order: 2),
            Column("Site", "Lookup", key: false, scope: "SharedRegistry", registry: sharedCode, order: 3),
        };
        var tables = new List<object>();
        if (withSibling)
        {
            columns.Add(Column("Place", "Lookup", key: false, scope: "SamePermitTable", sibling: "Places", order: 4));
            tables.Add(new { code = "Places", nameL10n = new { en = "Places" }, order = 1, columns = new[] { Column("Name", "String", key: true) }, rules = Array.Empty<object>() });
        }

        tables.Add(new { code = "Rows", nameL10n = new { en = "Rows" }, order = tables.Count + 1, columns, rules = Array.Empty<object>() });
        var replaced = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}", UriKind.Relative),
            new { rowVersion, nameL10n = new { en = code }, category = "Air", tables, rules = Array.Empty<object>() });
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, $"{replaced.StatusCode}: {await replaced.Content.ReadAsStringAsync()}");
        var fresh = (await Json(replaced)).GetProperty("rowVersion").GetString()!;

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "випуск типу для списків", rowVersion = fresh });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {await published.Content.ReadAsStringAsync()}");
    }

    internal static object Changes(params object[] changes) => new { changes };

    internal static Task<HttpResponseMessage> PutAsync(HttpClient client, string key, object body)
        => client.PutAsJsonAsync(new Uri($"/api/v1/permit-lists/{Uri.EscapeDataString(key)}/items", UriKind.Relative), body);

    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, string key)
        => client.GetAsync(new Uri($"/api/v1/permit-lists/{Uri.EscapeDataString(key)}", UriKind.Relative));

    internal static async Task<List<JsonElement>> ReadListAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/api/v1/permit-lists", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await Json(response)).EnumerateArray()];
    }

    internal static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);

    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string errorCode)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"Очікувалось {status}, а {response.StatusCode}: {body}");
        Assert.Equal(errorCode, JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    internal static IEnumerable<string?> Labels(JsonElement detail)
        => detail.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("displayL10n").GetProperty("en").GetString());
}
