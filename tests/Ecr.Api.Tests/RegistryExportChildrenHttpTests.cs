// tests/Ecr.Api.Tests/RegistryExportChildrenHttpTests.cs
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Експорт довідника разом із частинами композиції (RT-16 <c>includeChildren</c>, ФВ-8.16) наскрізь
/// через HTTP і SQL Server: три рівні (батько → частина → частина частини), CSV-архів, що
/// імпортується назад без змін, книга з аркушем на довідник, право на кожну частину, стеля на всіх.
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item>без рекурсії в <c>ExportRegistryHandler.ChildrenAsync</c> (лише прямі частини) →
/// <see cref="Експорт_з_частинами_CSV_архів_імпортується_назад_без_змін"/> червоний (немає <c>03-…</c>);</item>
/// <item>без перевірки читання дочірнього довідника → <see cref="Експорт_з_частинами_XLSX_право_на_кожну_частину_й_стеля"/>
/// червоний (грант лише на батька дає 200 замість 403);</item>
/// <item>стеля лише на батька (<c>root.Ordered.Count</c> замість суми) → той самий тест червоний (200 замість 422).</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryExportChildrenHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Child-2026!";

    private const string Precise = "0.1234567890123456";

    private static readonly string[] Editor = ["Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Експорт_з_частинами_CSV_архів_імпортується_назад_без_змін()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, [], Editor);
        var f = await FixtureAsync(client);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/registries/{f.Parent.Code}/export?format=csv&asOf=2026-07-01&includeChildren=true", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {Encoding.UTF8.GetString(bytes)}\n{app.ErrorsText}");
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            $"registry-{f.Parent.Code}-20260701.zip",
            response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var files = Unzip(bytes);

        // Номер — порядок імпорту: батько раніше за частину, частина раніше за свою частину.
        Assert.Equal([$"01-{f.Parent.Code}.csv", $"02-{f.Child.Code}.csv", $"03-{f.Grand.Code}.csv"], [.. files.Keys]);
        Assert.Equal(["code,NAME", $"S1{f.Parent.Tag},1D-1"], Lines(files[$"01-{f.Parent.Code}.csv"]));

        // Частина закритого батька на asOf не видна — як у сітці; поле композиції несе КОД батька.
        Assert.Equal(
            ["code,PARENT,PART,SHARE", $"P1{f.Child.Tag},S1{f.Parent.Tag},CH4,{Precise}"],
            Lines(files[$"02-{f.Child.Code}.csv"]));
        Assert.Equal(["code,OWNER,X", $"G1{f.Grand.Tag},P1{f.Child.Tag},x"], Lines(files[$"03-{f.Grand.Code}.csv"]));

        var before = await ValuesAsync(client, f, "2026-07-01");

        // Кожен файл архіву — рівно те, що приймає імпорт свого довідника; у порядку номерів.
        foreach (var (registry, name) in new[] { (f.Parent, "01"), (f.Child, "02"), (f.Grand, "03") })
        {
            var report = await ImportAsync(client, registry, files[$"{name}-{registry.Code}.csv"]);
            Assert.Empty(report.GetProperty("errors").EnumerateArray());
            Assert.Equal(0, report.GetProperty("added").GetInt32());
        }

        Assert.Equal(before, await ValuesAsync(client, f, "2026-07-01"));
        foreach (var (registry, id) in new[] { (f.Parent, f.S1), (f.Child, f.P1), (f.Grand, f.G1) })
        {
            var history = Items(await GetAsync(client, $"/api/v1/registries/{registry.Code}/entries/{id}/history"));
            Assert.Equal(["created"], [.. history.Select(i => i.GetProperty("kind").GetString()!)]);
        }

        // Без includeChildren — той самий плаский CSV, що й досі (контракт RT-16 не зрушив).
        var flat = await client.GetAsync(new Uri($"/api/v1/registries/{f.Parent.Code}/export?format=csv&asOf=2026-07-01", UriKind.Relative));
        Assert.Equal("text/csv", flat.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Експорт_з_частинами_XLSX_право_на_кожну_частину_й_стеля()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, [], Editor);
        var f = await FixtureAsync(client);

        var before = (await ExportEventsAsync(f.Parent.Code)).Count;
        var response = await client.GetAsync(new Uri(
            $"/api/v1/registries/{f.Parent.Code}/export?format=xlsx&asOf=2026-05-01&includeChildren=true", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {Encoding.UTF8.GetString(bytes)}\n{app.ErrorsText}");

        using (var book = new XLWorkbook(new MemoryStream(bytes)))
        {
            Assert.Equal([f.Parent.Code, f.Child.Code, f.Grand.Code], [.. book.Worksheets.Select(s => s.Name)]);

            // На 2026-05-01 чинні обидва батьки — видно обидві частини.
            var child = book.Worksheet(f.Child.Code);
            Assert.Equal(
                ["code", "@name", "@validFrom", "@validTo", "PARENT", "PART", "SHARE"],
                [.. Enumerable.Range(1, 7).Select(c => child.Cell(1, c).GetString())]);
            Assert.Equal([$"S1{f.Parent.Tag}", $"S2{f.Parent.Tag}"], [child.Cell(2, 5).GetString(), child.Cell(3, 5).GetString()]);
            Assert.Equal($"P1{f.Child.Tag}", book.Worksheet(f.Grand.Code).Cell(2, 5).GetString());
        }

        var events = await ExportEventsAsync(f.Parent.Code);
        Assert.Equal(before + 1, events.Count);
        using (var details = JsonDocument.Parse(events[^1]))
        {
            Assert.True(details.RootElement.GetProperty("includeChildren").GetBoolean());
            Assert.Equal(5, details.RootElement.GetProperty("rows").GetInt32());
        }

        // Стеля — на всі довідники файлу: 2 батьки вміщаються в 4, а 2 + 2 + 1 — ні.
        using (var capped = app.WithWebHostBuilder(b => b.UseSetting("Registries:ExportMaxRows", "4")))
        {
            using var cappedClient = await SignedInAsync(capped, [], Editor);
            var alone = await cappedClient.GetAsync(new Uri($"/api/v1/registries/{f.Parent.Code}/export?asOf=2026-05-01", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
            var tooLarge = await cappedClient.GetAsync(new Uri(
                $"/api/v1/registries/{f.Parent.Code}/export?asOf=2026-05-01&includeChildren=true", UriKind.Relative));
            var text = await tooLarge.Content.ReadAsStringAsync();
            Assert.True(tooLarge.StatusCode == HttpStatusCode.UnprocessableEntity, $"{tooLarge.StatusCode}: {text}");
            Assert.Contains("err.ECR-REQ-0422.registryExportTooLarge", text, StringComparison.Ordinal);
        }

        // Грант на батька не відкриває його частин: без includeChildren — файл, з ним — відмова.
        using var parentOnly = await SignedInAsync(app, [f.Parent.Id]);
        var own = await parentOnly.GetAsync(new Uri($"/api/v1/registries/{f.Parent.Code}/export?asOf=2026-05-01", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var withParts = await parentOnly.GetAsync(new Uri(
            $"/api/v1/registries/{f.Parent.Code}/export?asOf=2026-05-01&includeChildren=true", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, withParts.StatusCode);

        // ⛔ S18: глобальне право із забороною на частину — та сама відмова, що й лише з грантом на батька:
        // 403 permission без ідентифікатора частини. Доти — 404 registryId з registryDefId частини,
        // про яку людина не питала й якої не бачить.
        using var deniedChild = await SignedInDeniedAsync(app, [], [f.Child.Id], "Registry.View");
        var hiddenPart = await deniedChild.GetAsync(new Uri(
            $"/api/v1/registries/{f.Parent.Code}/export?asOf=2026-05-01&includeChildren=true", UriKind.Relative));
        var hiddenText = await hiddenPart.Content.ReadAsStringAsync();
        Assert.True(hiddenPart.StatusCode == HttpStatusCode.Forbidden, $"{hiddenPart.StatusCode}: {hiddenText}");
        Assert.Contains("err.ECR-AUTH-0403.permission", hiddenText, StringComparison.Ordinal);
        Assert.DoesNotContain("registryDefId", hiddenText, StringComparison.Ordinal);
        Assert.Contains("err.ECR-AUTH-0403.permission", await withParts.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var all = await SignedInAsync(app, [f.Parent.Id, f.Child.Id, f.Grand.Id]);
        var granted = await all.GetAsync(new Uri(
            $"/api/v1/registries/{f.Parent.Code}/export?asOf=2026-05-01&includeChildren=true", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
    }

    /// <summary>
    /// Темпоральний батько (NAME) → частина (PARENT, PART, SHARE) → частина частини (OWNER, X).
    /// S1 чинний завжди, S2 закритий 2026-06-01; P1 ∈ S1, P2 ∈ S2; G1 ∈ P1.
    /// </summary>
    private static async Task<Fixture> FixtureAsync(HttpClient client)
    {
        var parent = await CreateRegistryAsync(client, "EP", isTemporal: true);
        await SaveFieldsAsync(client, parent, [Field("NAME", "String", 1, isKey: true)]);

        var child = await CreateRegistryAsync(client, "EC", isTemporal: false);
        await SaveFieldsAsync(client, child, [
            Composition("PARENT", parent.Id), Field("PART", "String", 2, isKey: true), Field("SHARE", "Decimal", 3)]);

        var grand = await CreateRegistryAsync(client, "EG", isTemporal: false);
        await SaveFieldsAsync(client, grand, [Composition("OWNER", child.Id), Field("X", "String", 2, isKey: true)]);

        var s1 = await CreateAsync(client, parent, "S1", new() { ["NAME"] = "1D-1" });
        var s2 = await CreateAsync(client, parent, "S2", new() { ["NAME"] = "1D-2" });
        var p1 = await CreateAsync(client, child, "P1", new() { ["PARENT"] = s1, ["PART"] = "CH4", ["SHARE"] = Precise });
        await CreateAsync(client, child, "P2", new() { ["PARENT"] = s2, ["PART"] = "CO2", ["SHARE"] = "1" });
        var g1 = await CreateAsync(client, grand, "G1", new() { ["OWNER"] = p1, ["X"] = "x" });

        var window = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{parent.Code}/entries/{s2}/validity", UriKind.Relative),
            new { from = (DateOnly?)null, to = new DateOnly(2026, 6, 1) });
        Assert.True(window.IsSuccessStatusCode, $"{window.StatusCode}: {await window.Content.ReadAsStringAsync()}");

        return new Fixture(parent, child, grand, s1, p1, g1);
    }

    /// <summary>Значення рядків усіх трьох довідників — доказ «імпорт нічого не змінив».</summary>
    private static async Task<string> ValuesAsync(HttpClient client, Fixture f, string asOf)
    {
        var text = new StringBuilder();
        foreach (var registry in new[] { f.Parent, f.Child, f.Grand })
        {
            foreach (var row in Items(await GetAsync(client, $"/api/v1/registries/{registry.Code}/rows?asOf={asOf}")))
            {
                text.Append(row.GetProperty("code").GetString()).Append(row.GetProperty("values")).Append('\n');
            }
        }

        return text.ToString();
    }

    private static async Task<JsonElement> ImportAsync(HttpClient client, Registry registry, byte[] csv)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(csv);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "export.csv");
        var imported = await client.PostAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries/import?dryRun=false", UriKind.Relative), content);
        var report = JsonDocument.Parse(await imported.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.True(imported.StatusCode == HttpStatusCode.OK, $"{imported.StatusCode}: {report}");
        return report;
    }

    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            result.Add(entry.FullName, buffer.ToArray());
        }

        return result;
    }

    /// <summary>Рядки CSV; BOM на початку обов'язковий (інакше Excel читає кирилицю як cp1251).</summary>
    private static string[] Lines(byte[] csv)
    {
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]);
        return Encoding.UTF8.GetString(csv[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<List<string>> ExportEventsAsync(string registryCode)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DetailsJson FROM aud.SecurityEvent
             WHERE EventType = N'RegistryExported' AND DetailsJson LIKE N'%' + @code + N'%'
             ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@code", registryCode);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static JsonElement[] Items(JsonElement page) => [.. page.GetProperty("items").EnumerateArray()];

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task SaveFieldsAsync(HttpClient client, Registry registry, object[] fields)
    {
        var saved = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{registry.Code}/definition", UriKind.Relative),
            new { fields, rules = Array.Empty<object>(), reason = "RT-16 includeChildren" });
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
    }

    private static object Field(string code, string dataType, int ordinal, bool isKey = false)
        => new
        {
            id = (int?)null, code, nameL10n = Name(code), dataType, ordinal, isRequired = isKey, isKey,
            lookupRegistryDefId = (int?)null, unitId = (int?)null,
        };

    private static object Composition(string code, int parentId)
        => new
        {
            id = (int?)null, code, nameL10n = Name(code), dataType = "Lookup", ordinal = 1,
            isRequired = true, isKey = false, lookupRegistryDefId = (int?)parentId, unitId = (int?)null,
            relationKind = "Composition", onParentDelete = "Cascade",
        };

    private static object Name(string value) => new { values = new Dictionary<string, string> { ["en"] = value } };

    private static async Task<Registry> CreateRegistryAsync(HttpClient client, string prefix, bool isTemporal)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/registries", UriKind.Relative),
            new { code = $"{prefix}{tag}", nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} {tag}" }, isTemporal });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        var created = JsonDocument.Parse(body).RootElement;
        return new Registry(created.GetProperty("id").GetInt32(), created.GetProperty("code").GetString()!, tag);
    }

    private static async Task<long> CreateAsync(HttpClient client, Registry registry, string code, Dictionary<string, object?> values)
    {
        var full = $"{code}{registry.Tag}";
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = registry.Id,
                code = full,
                display = new { values = new Dictionary<string, string> { ["en"] = full } },
                parentEntryId = (long?)null,
                values,
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    /// <summary>Користувач із глобальними правами й/або грантами <c>Read</c> на довідники.</summary>
    private Task<HttpClient> SignedInAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, int[] readGrants, params string[] permissions)
        => SignedInDeniedAsync(app, readGrants, [], permissions);

    /// <summary>Те саме, плюс явні заборони (<c>IsDeny</c>) на довідники.</summary>
    private async Task<HttpClient> SignedInDeniedAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, int[] readGrants, int[] denies, params string[] permissions)
    {
        var name = $"rege_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            if (permissions.Length > 0 || readGrants.Length > 0 || denies.Length > 0)
            {
                var role = new Role(
                    EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry export children test" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync();

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                foreach (var registryId in readGrants)
                {
                    db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registryId, GrantLevel.Read));
                }

                foreach (var registryId in denies)
                {
                    db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, registryId, GrantLevel.Read, isDeny: true));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync();
            }
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}");
        return client;
    }

    private sealed record Registry(int Id, string Code, string Tag);

    private sealed record Fixture(Registry Parent, Registry Child, Registry Grand, long S1, long P1, long G1);
}
