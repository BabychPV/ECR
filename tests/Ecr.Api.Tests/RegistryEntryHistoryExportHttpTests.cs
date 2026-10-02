// tests/Ecr.Api.Tests/RegistryEntryHistoryExportHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Units;
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
/// Серверні доповнення до екранів довідника (ФВ-8.12, 8.14, 8.16) наскрізь через HTTP і SQL Server:
/// історія запису (RT-15), експорт CSV/XLSX (RT-16), назва й чинність нового запису в пакеті,
/// фільтр <c>GET …/rows?id=</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item>значення без <c>TemporalAll</c> у <c>RegistryRowsQuery.ReadEntryHistoryAsync</c> →
/// <see cref="Історія_запису_хто_що_й_коли"/> червоний (зміни значення немає);</item>
/// <item>межа курсора історії включно (<c>&lt;= 0</c>) →
/// <see cref="Історія_гортається_курсором_і_не_бачить_чужого_запису"/> червоний на повторі;</item>
/// <item><c>Lookup</c> без заміни Id кодом (<c>ExportRegistryHandler.Cell</c>) → <see cref="Експорт_CSV_повторно_імпортується_без_змін"/> червоний
/// і на рядку файлу, і на повторному імпорті;</item>
/// <item>без перевірки стелі → <see cref="Експорт_відмови_стеля_й_журнал"/> червоний;</item>
/// <item>без відбору <c>EntryIds</c> → <see cref="Фільтр_рядків_за_id"/> червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryHistoryExportHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Hist-2026!";

    private static readonly string[] Editor = ["Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.14")]
    public async Task Історія_запису_хто_що_й_коли()
    {
        using var app = new EcrApiFactory(sql);
        var (client, author) = await SignedInWithNameAsync(app, Editor);
        using var _ = client;
        var registry = await CreateRegistryAsync(client, "RH", isTemporal: true);
        await SaveFieldsAsync(client, registry, [Field("NAME", "String", 1, isKey: true), Field("T", "Decimal", 2)]);

        // Новий запис пакетом — одразу з назвою й вікном чинності.
        var (status, body) = await BatchAsync(client, registry,
            new
            {
                clientRowId = "n", op = "upsert", code = $"H{registry.Tag}", name = "Піч №1",
                validFrom = new DateOnly(2026, 1, 1),
                values = new Dictionary<string, object?> { ["NAME"] = "kiln", ["T"] = "1.5" },
            });
        Assert.True(status == HttpStatusCode.OK && body.GetProperty("applied").GetBoolean(), $"{status}: {body}\n{app.ErrorsText}");
        var id = body.GetProperty("rows")[0].GetProperty("entryId").GetInt64();

        var created = Assert.Single(Items(await GetAsync(client, $"/api/v1/registries/{registry.Code}/rows?asOf=2026-03-01")));
        Assert.Equal("Піч №1", created.GetProperty("display").GetString());
        Assert.Equal("2026-01-01", created.GetProperty("validFrom").GetString());
        Assert.Empty(Items(await GetAsync(client, $"/api/v1/registries/{registry.Code}/rows?asOf=2025-12-31")));

        // Кожна наступна зміна — окрема системна версія (PeriodStart — datetime2(3)).
        await Task.Delay(30);
        (status, body) = await BatchAsync(client, registry,
            new { clientRowId = "u", op = "upsert", id, values = new Dictionary<string, object?> { ["T"] = "2.75" } });
        Assert.True(status == HttpStatusCode.OK && body.GetProperty("applied").GetBoolean(), $"{status}: {body}");

        await Task.Delay(30);
        var window = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries/{id}/validity", UriKind.Relative),
            new { from = new DateOnly(2026, 1, 1), to = new DateOnly(2027, 1, 1) });
        Assert.True(window.IsSuccessStatusCode, $"{window.StatusCode}: {await window.Content.ReadAsStringAsync()}");

        await Task.Delay(30);
        var deleted = await client.DeleteAsync(new Uri($"/api/v1/registries/{registry.Code}/entries/{id}", UriKind.Relative));
        Assert.True(deleted.IsSuccessStatusCode, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}");

        // Видалений запис історію має — саме її й шукають після видалення.
        var history = await GetAsync(client, $"/api/v1/registries/{registry.Code}/entries/{id}/history?limit=50");
        var items = Items(history);
        Assert.Equal(["deleted", "validity", "value", "created"], [.. items.Select(i => i.GetProperty("kind").GetString()!)]);
        Assert.All(items, i => Assert.Equal(author, i.GetProperty("byDisplayName").GetString()));

        var value = items[2];
        Assert.Equal("T", value.GetProperty("field").GetString());
        Assert.Equal("1.5", value.GetProperty("oldValue").GetString());
        Assert.Equal("2.75", value.GetProperty("newValue").GetString());

        Assert.Equal("2026-01-01/..", items[1].GetProperty("oldValue").GetString());
        Assert.Equal("2026-01-01/2027-01-01", items[1].GetProperty("newValue").GetString());

        var moments = items.Select(i => i.GetProperty("at").GetDateTime()).ToList();
        Assert.Equal(moments.OrderDescending(), moments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.14")]
    public async Task Історія_гортається_курсором_і_не_бачить_чужого_запису()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Editor);
        var registry = await CreateRegistryAsync(client, "RH", isTemporal: false);
        await SaveFieldsAsync(client, registry, [Field("NAME", "String", 1, isKey: true), Field("T", "Decimal", 2)]);
        var other = await CreateRegistryAsync(client, "RO", isTemporal: false);

        var id = await CreateAsync(client, registry, "C", new() { ["NAME"] = "c", ["T"] = "1" });
        foreach (var t in new[] { "2", "3", "4", "5" })
        {
            await Task.Delay(30);
            var (status, body) = await BatchAsync(client, registry,
                new { clientRowId = t, op = "upsert", id, values = new Dictionary<string, object?> { ["T"] = t } });
            Assert.True(status == HttpStatusCode.OK && body.GetProperty("applied").GetBoolean(), $"{status}: {body}");
        }

        var seen = new List<string?>();
        string? cursor = null;
        var total = 0;
        do
        {
            var query = cursor is null ? "limit=2" : $"limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await GetAsync(client, $"/api/v1/registries/{registry.Code}/entries/{id}/history?{query}");
            total = page.GetProperty("totalCount").GetInt32();
            seen.AddRange(Items(page).Select(i => i.GetProperty("newValue").GetString()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
            Assert.True(seen.Count <= 10, "курсор не закінчується");
        }
        while (cursor is not null);

        Assert.Equal(5, total);
        Assert.Equal(["5", "4", "3", "2", null], seen);

        // Запис ІНШОГО довідника для цього маршруту не існує.
        var foreign = await client.GetAsync(new Uri($"/api/v1/registries/{other.Code}/entries/{id}/history", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Contains("err.ECR-REG-0404.registryEntry", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var stranger = await SignedInAsync(app);
        var denied = await stranger.GetAsync(new Uri($"/api/v1/registries/{registry.Code}/entries/{id}/history", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Експорт_CSV_повторно_імпортується_без_змін()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Editor);
        var fixture = await ExportFixtureAsync(client);

        var response = await client.GetAsync(
            new Uri($"/api/v1/registries/{fixture.Main.Code}/export?format=csv&asOf=2026-07-01", UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {Encoding.UTF8.GetString(bytes)}\n{app.ErrorsText}");
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"registry-{fixture.Main.Code}-20260701.csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // BOM — інакше Excel читає кирилицю як cp1251.
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("code,NAME,T,REF,U", lines[0]);

        // Запис, закритий до asOf, у файл не потрапляє; посилання й одиниця — кодами, число — без втрати знаків.
        Assert.Equal(2, lines.Length);
        Assert.Equal($"P{fixture.Main.Tag},Печь,{Precise},TARGET{fixture.Target.Tag},{fixture.UnitCode}", lines[1]);

        var before = Assert.Single(Items(await GetAsync(client, $"/api/v1/registries/{fixture.Main.Code}/rows?asOf=2026-07-01")));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "export.csv");
        var imported = await client.PostAsync(
            new Uri($"/api/v1/registries/{fixture.Main.Code}/entries/import?dryRun=false", UriKind.Relative), content);
        var report = JsonDocument.Parse(await imported.Content.ReadAsStringAsync()).RootElement;
        Assert.True(imported.StatusCode == HttpStatusCode.OK, $"{imported.StatusCode}: {report}");
        Assert.Empty(report.GetProperty("errors").EnumerateArray());
        Assert.Equal(0, report.GetProperty("added").GetInt32());
        // Імпорт рахує «updated» кожен рядок зі значеннями, навіть тими самими, тож доказ «нічого не
        // змінилося» — рядок сітки й історія запису: жодної версії значень після створення.
        Assert.Equal(1, report.GetProperty("updated").GetInt32());
        var after = Assert.Single(Items(await GetAsync(client, $"/api/v1/registries/{fixture.Main.Code}/rows?asOf=2026-07-01")));
        Assert.Equal(before.GetProperty("values").ToString(), after.GetProperty("values").ToString());
        var history = Items(await GetAsync(client, $"/api/v1/registries/{fixture.Main.Code}/entries/{after.GetProperty("id").GetInt64()}/history"));
        Assert.Equal(["created"], [.. history.Select(i => i.GetProperty("kind").GetString()!)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Експорт_XLSX_за_Accept()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Editor);
        var fixture = await ExportFixtureAsync(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"/api/v1/registries/{fixture.Main.Code}/export?asOf=2026-05-01", UriKind.Relative));
        request.Headers.Accept.ParseAdd("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {Encoding.UTF8.GetString(bytes)}\n{app.ErrorsText}");

        using var book = new XLWorkbook(new MemoryStream(bytes));
        var sheet = book.Worksheet(1);
        Assert.Equal(
            ["code", "@name", "@validFrom", "@validTo", "NAME", "T", "REF", "U"],
            [.. Enumerable.Range(1, 8).Select(c => sheet.Cell(1, c).GetString())]);

        // На 2026-05-01 чинні обидва записи; порядок — за Id.
        Assert.Equal($"P{fixture.Main.Tag}", sheet.Cell(2, 1).GetString());
        Assert.Equal("Печь", sheet.Cell(2, 2).GetString());

        // 26 значущих цифр double не тримає — текстом; 12.5 — числом.
        Assert.Equal(XLDataType.Text, sheet.Cell(2, 6).DataType);
        Assert.Equal(Precise, sheet.Cell(2, 6).GetString());
        Assert.Equal($"TARGET{fixture.Target.Tag}", sheet.Cell(2, 7).GetString());

        Assert.Equal($"S{fixture.Main.Tag}", sheet.Cell(3, 1).GetString());
        Assert.Equal(XLDataType.DateTime, sheet.Cell(3, 4).DataType);
        Assert.Equal(new DateTime(2026, 6, 1), sheet.Cell(3, 4).GetDateTime());
        Assert.Equal(XLDataType.Number, sheet.Cell(3, 6).DataType);
        Assert.Equal(12.5, sheet.Cell(3, 6).GetDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Експорт_відмови_стеля_й_журнал()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Editor);
        var fixture = await ExportFixtureAsync(client);

        var unknown = await client.GetAsync(new Uri($"/api/v1/registries/{fixture.Main.Code}/export?format=pdf", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains("err.ECR-REQ-0422.registryExportFormatUnknown", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using (var capped = app.WithWebHostBuilder(b => b.UseSetting("Registries:ExportMaxRows", "1")))
        {
            using var cappedClient = await SignedInAsync(capped, Editor);
            var tooLarge = await cappedClient.GetAsync(
                new Uri($"/api/v1/registries/{fixture.Main.Code}/export?asOf=2026-05-01", UriKind.Relative));
            var text = await tooLarge.Content.ReadAsStringAsync();
            Assert.True(tooLarge.StatusCode == HttpStatusCode.UnprocessableEntity, $"{tooLarge.StatusCode}: {text}");
            Assert.Contains("err.ECR-REQ-0422.registryExportTooLarge", text, StringComparison.Ordinal);

            // Одного запису на 2026-07-01 стеля пропускає.
            var fits = await cappedClient.GetAsync(
                new Uri($"/api/v1/registries/{fixture.Main.Code}/export?asOf=2026-07-01", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        }

        var before = await ExportEventsAsync(fixture.Main.Code);
        var ok = await client.GetAsync(new Uri($"/api/v1/registries/{fixture.Main.Code}/export?format=xlsx&asOf=2026-05-01", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var events = await ExportEventsAsync(fixture.Main.Code);
        Assert.Equal(before.Count + 1, events.Count);
        using (var details = JsonDocument.Parse(events[^1]))
        {
            Assert.Equal("xlsx", details.RootElement.GetProperty("format").GetString());
            Assert.Equal(2, details.RootElement.GetProperty("rows").GetInt32());
            Assert.Equal("2026-05-01", details.RootElement.GetProperty("asOf").GetString());
        }

        using var stranger = await SignedInAsync(app);
        var denied = await stranger.GetAsync(new Uri($"/api/v1/registries/{fixture.Main.Code}/export", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Фільтр_рядків_за_id()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Editor);
        var registry = await CreateRegistryAsync(client, "RI", isTemporal: false);
        await SaveFieldsAsync(client, registry, [Field("NAME", "String", 1, isKey: true)]);

        var a = await CreateAsync(client, registry, "A", new() { ["NAME"] = "a" });
        await CreateAsync(client, registry, "B", new() { ["NAME"] = "b" });
        var c = await CreateAsync(client, registry, "C", new() { ["NAME"] = "c" });

        var page = await GetAsync(client, $"/api/v1/registries/{registry.Code}/rows?id={c}&id={a}&id=999999999");
        Assert.Equal([a, c], [.. Items(page).Select(r => r.GetProperty("id").GetInt64())]);
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());

        var many = string.Join("&", Enumerable.Range(1, 501).Select(i => $"id={i}"));
        var tooMany = await client.GetAsync(new Uri($"/api/v1/registries/{registry.Code}/rows?{many}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Contains("err.ECR-REQ-0422.registryRowsIdsTooMany", await tooMany.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Назва й чинність у пакеті — лише для нового запису.
        var (status, body) = await BatchAsync(client, registry,
            new { clientRowId = "x", op = "upsert", id = a, name = "rename", values = new Dictionary<string, object?>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("err.ECR-REQ-0422.batchItemNewOnly", body.GetProperty("messageKey").GetString());
    }

    private const string Precise = "1234567890.1234567890123456";

    /// <summary>
    /// Темпоральний довідник NAME/T/REF(→ціль)/U(одиниця): P — чинний завжди, S — закритий 2026-06-01.
    /// </summary>
    private async Task<ExportFixture> ExportFixtureAsync(HttpClient client)
    {
        var target = await CreateRegistryAsync(client, "RT", isTemporal: false);
        await SaveFieldsAsync(client, target, [Field("NAME", "String", 1, isKey: true)]);
        var targetId = await CreateAsync(client, target, "TARGET", new() { ["NAME"] = "t" });

        var (unitId, unitCode) = await UnitAsync();

        var main = await CreateRegistryAsync(client, "RX", isTemporal: true);
        await SaveFieldsAsync(client, main, [
            Field("NAME", "String", 1, isKey: true), Field("T", "Decimal", 2),
            Field("REF", "Lookup", 3, lookup: target.Id), Field("U", "Unit", 4)]);

        await CreateAsync(client, main, "P", new() { ["NAME"] = "Печь", ["T"] = Precise, ["REF"] = targetId, ["U"] = unitId });
        var closed = await CreateAsync(client, main, "S", new() { ["NAME"] = "Second", ["T"] = "12.5" });
        var window = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{main.Code}/entries/{closed}/validity", UriKind.Relative),
            new { from = (DateOnly?)null, to = new DateOnly(2026, 6, 1) });
        Assert.True(window.IsSuccessStatusCode, $"{window.StatusCode}: {await window.Content.ReadAsStringAsync()}");

        return new ExportFixture(main, target, unitCode);
    }

    private async Task<(int Id, string Code)> UnitAsync()
    {
        var code = $"U{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());
        var dimensionId = await db.Dimensions.OrderBy(d => d.Id).Select(d => d.Id).FirstAsync();
        var unit = new Unit(EcrCode.Create(code), Text(code), Text(code), dimensionId, isBase: false, factorToBase: 1m, offsetToBase: 0m);
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        return (unit.Id, code);
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

    private static async Task<(HttpStatusCode Status, JsonElement Body)> BatchAsync(HttpClient client, Registry registry, params object[] items)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries/batch?dryRun=false", UriKind.Relative), new { items });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task SaveFieldsAsync(HttpClient client, Registry registry, object[] fields)
    {
        var saved = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{registry.Code}/definition", UriKind.Relative),
            new { fields, rules = Array.Empty<object>(), reason = "RT-15/16" });
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
    }

    private static object Field(string code, string dataType, int ordinal, bool isKey = false, int? lookup = null)
        => new
        {
            id = (int?)null, code, nameL10n = Name(code), dataType, ordinal, isRequired = isKey, isKey,
            lookupRegistryDefId = lookup, unitId = (int?)null,
        };

    private static object Name(string value) => new { values = new Dictionary<string, string> { ["en"] = value } };

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

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

    /// <summary>Запис із назвою, що дорівнює значенню NAME (так рядок CSV стабільний).</summary>
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
                display = new { values = new Dictionary<string, string> { ["en"] = values.TryGetValue("NAME", out var n) ? $"{n}" : full } },
                parentEntryId = (long?)null,
                values,
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private async Task<HttpClient> SignedInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, params string[] permissions)
        => (await SignedInWithNameAsync(app, permissions)).Client;

    /// <summary>Клієнт і відображуване ім'я користувача (воно, а не логін, іде в історію).</summary>
    private async Task<(HttpClient Client, string DisplayName)> SignedInWithNameAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, params string[] permissions)
    {
        var name = $"regh_{Guid.NewGuid():N}"[..20];
        var display = $"Історик {name[5..]}";

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, display, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            if (permissions.Length > 0)
            {
                var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Text("Registry history test"));
                db.Roles.Add(role);
                await db.SaveChangesAsync();

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
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
        return (client, display);
    }

    private sealed record Registry(int Id, string Code, string Tag);

    private sealed record ExportFixture(Registry Main, Registry Target, string UnitCode);
}
