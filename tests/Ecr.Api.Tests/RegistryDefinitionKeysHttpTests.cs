// tests/Ecr.Api.Tests/RegistryDefinitionKeysHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Ключі, композиція й режим коду в описі довідника — наскрізь через HTTP і SQL Server (RT-11,
/// <c>D-151…D-155</c>, <c>D-157</c>, FEATURE-REGISTRY-TABLES §4.5, §4.8, §7.1).
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item><c>SaveRegistryDefinitionHandler.PublishKeysAsync</c> без перевірки дублікатів →
/// <see cref="Ключ_на_даних_з_дублікатами_409"/> отримує 200 (ключ опубліковано на дублікатах, а
/// гонка з індексом дає 409 <c>keyTakenConcurrently</c> — не <c>existingDuplicates</c>);</item>
/// <item><c>GetRegistryEntriesHandler</c> без фільтра батька композиції →
/// <see cref="Пікер_не_показує_частин_невидимого_батька"/> бачить частину закритого батька.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class RegistryDefinitionKeysHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Keys-2026!";

    private static readonly string[] GroupFields = ["GROUP"];
    private static readonly string[] PrimaryFields = ["PARENT", "PART"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Ключ_на_даних_з_дублікатами_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var registry = await GroupedRegistryAsync(client);

        var a = await CreateAsync(client, registry, "A", new() { ["NAME"] = "a", ["GROUP"] = "North" });
        await CreateAsync(client, registry, "B", new() { ["NAME"] = "b", ["GROUP"] = " north " });
        await CreateAsync(client, registry, "C", new() { ["NAME"] = "c", ["GROUP"] = "South" });
        await CreateAsync(client, registry, "D", new() { ["NAME"] = "d" });

        // Жива перевірка до збереження: той самий алгоритм, що й публікація (регістр і крайні
        // пробіли не розрізняють значень, запис без значення в перевірку не входить).
        var check = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/keys/check", UriKind.Relative),
            new { fieldCodes = GroupFields, ignoreCase = true });
        var checkBody = await check.Content.ReadAsStringAsync();
        Assert.True(check.StatusCode == HttpStatusCode.OK, $"{check.StatusCode}: {checkBody}\n{app.ErrorsText}");
        var report = JsonDocument.Parse(checkBody).RootElement;
        Assert.Equal(4, report.GetProperty("checked").GetInt32());
        Assert.Equal(1, report.GetProperty("groups").GetInt32());
        var group = Assert.Single(report.GetProperty("sample").EnumerateArray());
        Assert.Equal("North", group.GetProperty("keyText").GetString());
        Assert.Equal(
            [$"A{registry.Tag}", $"B{registry.Tag}"],
            group.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray());

        var response = await SaveDefinitionAsync(client, registry, keys: [GroupKey()]);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-4092", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-4092.existingDuplicates", problem.GetProperty("messageKey").GetString());
        Assert.Equal("1", problem.GetProperty("groups").GetString());
        var sample = Assert.Single(problem.GetProperty("sample").EnumerateArray());
        Assert.Contains(sample.GetProperty("entries").EnumerateArray(), e => e.GetProperty("id").GetInt64() == a);

        // Відмова — уся транзакція: ні ключа, ні нової версії опису.
        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryKeyDefs.AnyAsync(k => k.RegistryDefId == registry.Id));
        Assert.Equal(2, await db.RegistryDefs.Where(d => d.Id == registry.Id).Select(d => d.DefinitionVersion).SingleAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Первинний_ключ_на_записах_з_порожньою_частиною_422_primaryKeyEmptyParts()
    {
        // ⛔ Мутація (L4-06 / L5-03): прибрати перевірку порожніх частин у `PublishKeysAsync` →
        // перший assert отримує 200: первинний ключ опублікований, а записи без значення тихо
        // лишилися поза його унікальністю (рядка ключа в них немає, хеш = null).
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var registry = await GroupedRegistryAsync(client);

        await CreateAsync(client, registry, "A", new() { ["NAME"] = "a", ["GROUP"] = "North" });
        await CreateAsync(client, registry, "B", new() { ["NAME"] = "b" });
        await CreateAsync(client, registry, "C", new() { ["NAME"] = "c", ["GROUP"] = "   " });

        // GROUP стає обов'язковим разом із первинним ключем на ньому — але B і C значення не мають.
        var refused = await SaveDefinitionAsync(client, registry, keys: [GroupPrimaryKey()], groupRequired: true);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{refused.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-0422.primaryKeyEmptyParts", problem.GetProperty("messageKey").GetString());
        Assert.Equal("BY_GROUP", problem.GetProperty("key").GetString());
        Assert.Equal("2", problem.GetProperty("entries").GetString());
        Assert.Equal(
            [$"B{registry.Tag}", $"C{registry.Tag}"],
            problem.GetProperty("sample").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).Order(StringComparer.Ordinal).ToArray());

        // Відмова — уся транзакція: ні ключа, ні нової версії опису, ні обов'язковості поля.
        await using (var db = new EcrDbContext(Options()))
        {
            Assert.False(await db.RegistryKeyDefs.AnyAsync(k => k.RegistryDefId == registry.Id));
            Assert.Equal(2, await db.RegistryDefs.Where(d => d.Id == registry.Id).Select(d => d.DefinitionVersion).SingleAsync());
            Assert.False(await db.RegistryFieldDefs.Where(f => f.RegistryDefId == registry.Id && f.Code == "GROUP").Select(f => f.IsRequired).SingleAsync());
        }

        // Контроль: коли в усіх живих записів частина ключа є, той самий опис проходить.
        foreach (var code in new[] { "B", "C" })
        {
            var id = await EntryIdAsync(registry, $"{code}{registry.Tag}");
            var deleted = await client.DeleteAsync(new Uri($"/api/v1/registries/{registry.Code}/entries/{id}", UriKind.Relative));
            Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}");
        }

        var ok = await SaveDefinitionAsync(client, registry, keys: [GroupPrimaryKey()], groupRequired: true);
        Assert.True(ok.IsSuccessStatusCode, $"{ok.StatusCode}: {await ok.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        await using var after = new EcrDbContext(Options());
        var key = await after.RegistryKeyDefs.AsNoTracking().SingleAsync(k => k.RegistryDefId == registry.Id);
        Assert.True(key.IsPrimary);
        Assert.Equal(1, await after.RegistryEntryKeys.CountAsync(k => k.RegistryKeyDefId == key.Id && k.IsLive));
    }

    private async Task<long> EntryIdAsync(Registry registry, string code)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == registry.Id && e.Code == code)
            .Select(e => e.Id)
            .SingleAsync();
    }

    private static object GroupPrimaryKey()
        => new
        {
            id = (int?)null,
            code = "BY_GROUP",
            nameL10n = new { values = new Dictionary<string, string> { ["en"] = "By group" } },
            fieldCodes = GroupFields,
            isPrimary = true,
            ignoreCase = true,
            isActive = true,
        };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Ключ_без_дублікатів_заповнює_рядки_в_тій_самій_транзакції()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var registry = await GroupedRegistryAsync(client);

        var a = await CreateAsync(client, registry, "A", new() { ["NAME"] = "a", ["GROUP"] = "North" });
        var c = await CreateAsync(client, registry, "C", new() { ["NAME"] = "c", ["GROUP"] = "South" });
        await CreateAsync(client, registry, "D", new() { ["NAME"] = "d" });

        var response = await SaveDefinitionAsync(client, registry, keys: [GroupKey()]);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        await using (var db = new EcrDbContext(Options()))
        {
            var key = await db.RegistryKeyDefs.AsNoTracking().SingleAsync(k => k.RegistryDefId == registry.Id);
            var rows = await db.RegistryEntryKeys.AsNoTracking()
                .Where(k => k.RegistryKeyDefId == key.Id)
                .Select(k => new { k.RegistryEntryId, k.IsLive, k.KeyText })
                .ToListAsync();

            // Запис без значення (D) рядка не має: частина порожня (D-153).
            Assert.Equal([a, c], rows.Select(r => r.RegistryEntryId).Order().ToArray());
            Assert.All(rows, r => Assert.True(r.IsLive));
            Assert.Contains(rows, r => r.KeyText == "North");
        }

        // Ключ діє з першого ж запису: той самий GROUP іншим регістром — 409.
        var duplicate = await PostEntryAsync(client, registry, "E", new() { ["NAME"] = "e", ["GROUP"] = "NORTH" });
        var duplicateBody = await duplicate.Content.ReadAsStringAsync();
        Assert.True(duplicate.StatusCode == HttpStatusCode.Conflict, $"{duplicate.StatusCode}: {duplicateBody}");
        Assert.Equal("err.ECR-REG-4092.keyTaken", JsonDocument.Parse(duplicateBody).RootElement.GetProperty("messageKey").GetString());

        // Опис читається з ключем.
        var definition = await DefinitionAsync(client, registry.Code);
        var read = Assert.Single(definition.GetProperty("keys").EnumerateArray());
        Assert.Equal("BY_GROUP", read.GetProperty("code").GetString());
        Assert.Equal(GroupFields, read.GetProperty("fieldCodes").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.True(read.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Композиція_й_автокод_через_HTTP_до_каскаду()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var (parent, child) = await CompositionAsync(client, parentTemporal: false);

        var stream = await CreateAsync(client, parent, "S", new() { ["NAME"] = "1D-2" });
        var part = await CreateAsync(client, child, string.Empty, new() { ["PARENT"] = stream, ["PART"] = "CH4" });

        // Первинний ключ частини діє: той самий батько й та сама назва — 409.
        var twin = await PostEntryAsync(client, child, string.Empty, new() { ["PARENT"] = stream, ["PART"] = "ch4" });
        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);

        // Опис читається з композицією, ключем і режимом коду.
        var definition = await DefinitionAsync(client, child.Code);
        Assert.Equal("Auto", definition.GetProperty("codeMode").GetString());
        var relation = Assert.Single(definition.GetProperty("relations").EnumerateArray());
        Assert.Equal("Composition", relation.GetProperty("kind").GetString());
        Assert.Equal("Cascade", relation.GetProperty("onParentDelete").GetString());
        Assert.True(Assert.Single(definition.GetProperty("keys").EnumerateArray()).GetProperty("isPrimary").GetBoolean());

        var deleted = await client.DeleteAsync(
            new Uri($"/api/v1/registries/{parent.Code}/entries/{stream}", UriKind.Relative));
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        await using var db = new EcrDbContext(Options());
        var stored = await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == part);
        Assert.Matches(AutoCode(), stored.Code);
        Assert.True(stored.IsDeleted, "частина не видалена каскадом разом із батьком");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Пікер_не_показує_частин_невидимого_батька()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var (parent, child) = await CompositionAsync(client, parentTemporal: true);

        var stream = await CreateAsync(client, parent, "S", new() { ["NAME"] = "1D-2" });
        var part = await CreateAsync(client, child, string.Empty, new() { ["PARENT"] = stream, ["PART"] = "CH4" });

        Assert.Contains(part, await PickerAsync(client, child, "2026-07-01"));

        // Батька закрито 1 червня: на липень він невидимий — і його склад теж. Той самий запит,
        // що вже лежить у кеші, мусить побачити зміну (ревізія батька в ключі кешу).
        var closed = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{parent.Code}/entries/{stream}/validity", UriKind.Relative),
            new { from = (DateOnly?)null, to = new DateOnly(2026, 6, 1) });
        Assert.True(closed.IsSuccessStatusCode, $"{closed.StatusCode}: {await closed.Content.ReadAsStringAsync()}");

        Assert.DoesNotContain(part, await PickerAsync(client, child, "2026-07-01"));
        Assert.Contains(part, await PickerAsync(client, child, "2026-05-01"));

        // Дата обов'язкова, бо темпоральний батько: без неї перелік збрехав би «порожньо».
        var withoutDate = await client.GetAsync(new Uri($"/api/v1/registries/{child.Code}/entries", UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutDate.StatusCode);
    }

    [GeneratedRegex("^E[0-9]{9}$")]
    private static partial Regex AutoCode();

    private static object GroupKey()
        => new
        {
            id = (int?)null,
            code = "BY_GROUP",
            nameL10n = new { values = new Dictionary<string, string> { ["en"] = "By group" } },
            fieldCodes = GroupFields,
            isPrimary = false,
            ignoreCase = true,
            isActive = true,
        };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Опис_без_If_Match_422_зі_старою_версією_409_з_актуальною_200()
    {
        // ⛔ Мутація: `RequireCurrentVersion` не вимагає заголовка (`return` при `expected is null`)
        // або ігнорує його — перший або другий assert червоніє.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var registry = await GroupedRegistryAsync(client);
        var url = new Uri($"/api/v1/registries/{registry.Code}/definition", UriKind.Relative);
        var body = new
        {
            fields = new object[]
            {
                Field(registry.FieldIds["NAME"], "NAME", "String", 1, isKey: true),
                Field(registry.FieldIds["GROUP"], "GROUP", "String", 2),
            },
            rules = Array.Empty<object>(),
            reason = "ФВ-8.12 If-Match",
        };
        var current = await RegistryDefinitionHttpExtensions.ReadVersionAsync(client, url);

        var missing = await client.PutDefinitionAsync(url, body, ifMatch: null);
        var missingBody = await missing.Content.ReadAsStringAsync();
        Assert.True(missing.StatusCode == HttpStatusCode.UnprocessableEntity, $"{missing.StatusCode}: {missingBody}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-REQ-0422.definitionVersionRequired", JsonDocument.Parse(missingBody).RootElement.GetProperty("messageKey").GetString());

        var stale = await client.PutDefinitionAsync(url, body, ifMatch: "\"999\"");
        var staleBody = await stale.Content.ReadAsStringAsync();
        Assert.True(stale.StatusCode == HttpStatusCode.Conflict, $"{stale.StatusCode}: {staleBody}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-REG-0409.definitionChanged", JsonDocument.Parse(staleBody).RootElement.GetProperty("messageKey").GetString());

        // Відмови нічого не записали: версія та сама.
        Assert.Equal(current, await RegistryDefinitionHttpExtensions.ReadVersionAsync(client, url));

        var ok = await client.PutDefinitionAsync(url, body, ifMatch: current);
        Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{ok.StatusCode}: {await ok.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        Assert.NotEqual(current, await RegistryDefinitionHttpExtensions.ReadVersionAsync(client, url));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збереження_опису_ставить_оновлено_й_автора_а_перелік_віддає_їх_на_дріт()
    {
        // ⛔ Мутація: прибрати `definition.MarkDefinitionUpdated(...)` у застосуванні опису —
        // `updatedAt` лишається null, обидва assert червоніють.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var before = DateTime.UtcNow.AddMinutes(-1);
        var registry = await GroupedRegistryAsync(client);

        var list = await client.GetAsync(new Uri("/api/v1/registries", UriKind.Relative));
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.True(list.StatusCode == HttpStatusCode.OK, $"{list.StatusCode}: {listBody}\n{app.ErrorsText}");

        var item = JsonDocument.Parse(listBody).RootElement.EnumerateArray()
            .Single(d => d.GetProperty("id").GetInt32() == registry.Id);
        Assert.True(item.GetProperty("updatedAt").GetDateTime().ToUniversalTime() > before, listBody);
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("updatedByDisplayName").GetString()), listBody);

        await using var db = new EcrDbContext(Options());
        var row = await db.RegistryDefs.AsNoTracking().SingleAsync(d => d.Id == registry.Id);
        Assert.NotNull(row.DefinitionUpdatedAt);
        Assert.NotNull(row.DefinitionUpdatedByUserId);
    }

    /// <summary>Довідник із ключовим полем NAME і необов'язковим GROUP — через POST і PUT опису.</summary>
    private static async Task<Registry> GroupedRegistryAsync(HttpClient client)
    {
        var registry = await CreateRegistryAsync(client, "KG", isTemporal: false);
        var saved = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{registry.Code}/definition", UriKind.Relative),
            new
            {
                fields = new object[] { Field(null, "NAME", "String", 1, isKey: true), Field(null, "GROUP", "String", 2) },
                rules = Array.Empty<object>(),
                reason = "RT-11",
            });
        Assert.True(saved.IsSuccessStatusCode, $"{saved.StatusCode}: {await saved.Content.ReadAsStringAsync()}");
        return registry with { FieldIds = await FieldIdsAsync(client, registry.Code) };
    }

    private static async Task<HttpResponseMessage> SaveDefinitionAsync(
        HttpClient client, Registry registry, object[] keys, bool groupRequired = false)
        => await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{registry.Code}/definition", UriKind.Relative),
            new
            {
                fields = new object[]
                {
                    Field(registry.FieldIds["NAME"], "NAME", "String", 1, isKey: true),
                    Field(registry.FieldIds["GROUP"], "GROUP", "String", 2, isRequired: groupRequired),
                },
                rules = Array.Empty<object>(),
                reason = "RT-11 key",
                keys,
            });

    /// <summary>
    /// Батько (NAME) і частина композиції — заведені лише через HTTP: <c>relationKind</c>,
    /// <c>onParentDelete</c>, первинний ключ (PARENT, PART) і <c>codeMode = Auto</c>.
    /// </summary>
    private static async Task<(Registry Parent, Registry Child)> CompositionAsync(HttpClient client, bool parentTemporal)
    {
        var parent = await CreateRegistryAsync(client, "CP", parentTemporal);
        var parentSaved = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{parent.Code}/definition", UriKind.Relative),
            new { fields = new object[] { Field(null, "NAME", "String", 1, isKey: true) }, rules = Array.Empty<object>(), reason = "RT-11" });
        Assert.True(parentSaved.IsSuccessStatusCode, $"{parentSaved.StatusCode}: {await parentSaved.Content.ReadAsStringAsync()}");

        var child = await CreateRegistryAsync(client, "CC", isTemporal: false);
        var childSaved = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{child.Code}/definition", UriKind.Relative),
            new
            {
                fields = new object[]
                {
                    new
                    {
                        id = (int?)null, code = "PARENT", nameL10n = Name("PARENT"), dataType = "Lookup", ordinal = 1,
                        isRequired = true, isKey = false, lookupRegistryDefId = (int?)parent.Id, unitId = (int?)null,
                        relationKind = "Composition", onParentDelete = "Cascade",
                    },
                    new
                    {
                        id = (int?)null, code = "PART", nameL10n = Name("PART"), dataType = "String", ordinal = 2,
                        isRequired = true, isKey = false, lookupRegistryDefId = (int?)null, unitId = (int?)null,
                    },
                },
                rules = Array.Empty<object>(),
                reason = "RT-11",
                keys = new object[]
                {
                    new
                    {
                        id = (int?)null, code = "PK", nameL10n = Name("PK"), fieldCodes = PrimaryFields,
                        isPrimary = true, ignoreCase = true, isActive = true,
                    },
                },
                codeMode = "Auto",
            });
        Assert.True(childSaved.IsSuccessStatusCode, $"{childSaved.StatusCode}: {await childSaved.Content.ReadAsStringAsync()}");

        return (parent, child);
    }

    private static object Field(int? id, string code, string dataType, int ordinal, bool isKey = false, bool isRequired = false)
        => new
        {
            id,
            code,
            nameL10n = Name(code),
            dataType,
            ordinal,
            isRequired,
            isKey,
            lookupRegistryDefId = (int?)null,
            unitId = (int?)null,
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
        return new Registry(created.GetProperty("id").GetInt32(), created.GetProperty("code").GetString()!, tag, []);
    }

    private static async Task<Dictionary<string, int>> FieldIdsAsync(HttpClient client, string code)
        => (await DefinitionAsync(client, code)).GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("code").GetString()!, f => f.GetProperty("id").GetInt32());

    private static async Task<JsonElement> DefinitionAsync(HttpClient client, string code)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<long[]> PickerAsync(HttpClient client, Registry registry, string asOf)
    {
        var response = await client.GetAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries?asOf={asOf}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64())];
    }

    private static async Task<long> CreateAsync(
        HttpClient client, Registry registry, string code, Dictionary<string, object?> values)
    {
        var response = await PostEntryAsync(client, registry, code, values);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> PostEntryAsync(
        HttpClient client, Registry registry, string code, Dictionary<string, object?> values)
    {
        var full = code.Length == 0 ? string.Empty : $"{code}{registry.Tag}";
        return client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = registry.Id,
                code = full,
                display = new { values = new Dictionary<string, string> { ["en"] = full.Length == 0 ? "part" : full } },
                parentEntryId = (long?)null,
                values,
            });
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    /// <summary>Клієнт із правами на дані, опис і публікацію довідників.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regk_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry keys test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in new[] { "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Registry(int Id, string Code, string Tag, Dictionary<string, int> FieldIds);
}
