// tests/Ecr.Api.Tests/RegistryBatchHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>POST /api/v1/registries/{code}/entries/batch</c> — пакетний запис рядків довідника з
/// <c>dryRun</c>, наскрізь через HTTP і SQL Server (RT-14, FEATURE-REGISTRY-TABLES §4.3, §7.1, <c>D-166</c>).
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item>без <c>RetireMovedRowsAsync</c> у службі ключів → <see cref="Обмін_ключами_в_пакеті"/> отримує
/// 409 <c>keyTakenConcurrently</c> від <c>UX_RegistryEntryKey_Live</c>;</item>
/// <item><c>dryRun</c> без відкату → <see cref="DryRun_не_змінює_ревізію"/> червоний (записи й ревізія);
/// <c>PlaceholderAutoCodes = false</c> → той самий тест червоний на послідовності;</item>
/// <item>без звірки <c>baseVersion</c> → <see cref="Застарілий_baseVersion_дає_entryChanged_і_нічого_не_пише"/> червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryBatchHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Batch-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Обмін_ключами_в_пакеті()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData");
        var fixture = await SeedAsync(RegistryCodeMode.Manual);

        var e1 = await CreateAsync(client, fixture, "E1", "10");
        var e2 = await CreateAsync(client, fixture, "E2", "20");
        var versions = await VersionsAsync(client, fixture);

        // E1: 10 → 20, E2: 20 → 10. Кінцевий стан унікальний, але перший же UPDATE одним проходом
        // порушив би UX_RegistryEntryKey_Live — звідси дві фази (§4.3).
        var (status, body) = await BatchAsync(client, fixture, dryRun: false,
            Upsert("r1", e1, versions[e1], ("STREAM", "20")),
            Upsert("r2", e2, versions[e2], ("STREAM", "10")));

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.True(body.GetProperty("applied").GetBoolean(), body.ToString());
        Assert.Equal(["updated", "updated"], Statuses(body));
        Assert.All(Rows(body), r => Assert.False(string.IsNullOrEmpty(r.GetProperty("version").GetString())));

        await using var db = new EcrDbContext(Options());
        var live = await db.RegistryEntryKeys.AsNoTracking()
            .Where(k => k.RegistryKeyDefId == fixture.KeyDefId && k.IsLive).ToListAsync();
        Assert.Equal(2, live.Count);
        Assert.Equal(Hash("20"), live.Single(k => k.RegistryEntryId == e1).KeyHash);
        Assert.Equal(Hash("10"), live.Single(k => k.RegistryEntryId == e2).KeyHash);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Видалення_звільняє_ключ_для_нового_запису_того_самого_пакета()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData");
        var fixture = await SeedAsync(RegistryCodeMode.Manual);

        var old = await CreateAsync(client, fixture, "OLD", "30");
        await CreateAsync(client, fixture, "OTHER", "40");

        var (status, body) = await BatchAsync(client, fixture, dryRun: false,
            new { clientRowId = "d", op = "delete", id = old },
            new { clientRowId = "n", op = "upsert", code = $"NEW{fixture.Tag}", values = new Dictionary<string, object?> { ["STREAM"] = "30" } },

            // Ключ запису поза пакетом — помилка рядка, а не 409 на весь пакет.
            new { clientRowId = "x", op = "upsert", code = $"X{fixture.Tag}", values = new Dictionary<string, object?> { ["STREAM"] = "40" } });

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.False(body.GetProperty("applied").GetBoolean());
        Assert.Equal(["deleted", "added", "error"], Statuses(body));
        var error = Rows(body)[2].GetProperty("errors")[0];
        Assert.Equal("ECR-REG-4092", error.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-4092.keyTaken", error.GetProperty("messageKey").GetString());
        Assert.Equal($"OTHER{fixture.Tag}", error.GetProperty("params").GetProperty("entryCode").GetString());

        // Без рядка-порушника той самий пакет записується: ключ видаленого вільний уже в цій транзакції.
        var (again, applied) = await BatchAsync(client, fixture, dryRun: false,
            new { clientRowId = "d", op = "delete", id = old },
            new { clientRowId = "n", op = "upsert", code = $"NEW{fixture.Tag}", values = new Dictionary<string, object?> { ["STREAM"] = "30" } });

        Assert.True(again == HttpStatusCode.OK && applied.GetProperty("applied").GetBoolean(), $"{again}: {applied}\n{app.ErrorsText}");
        Assert.True(Rows(applied)[1].GetProperty("entryId").GetInt64() > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DryRun_не_змінює_ревізію()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData");
        var fixture = await SeedAsync(RegistryCodeMode.Auto);

        // Перший запис — справжнім пакетом: авто-код і Id нового запису доходять до звіту.
        var (created, first) = await BatchAsync(client, fixture, dryRun: false,
            new { clientRowId = "a", op = "upsert", values = new Dictionary<string, object?> { ["STREAM"] = "1" } });
        Assert.True(created == HttpStatusCode.OK && first.GetProperty("applied").GetBoolean(), $"{created}: {first}\n{app.ErrorsText}");
        var id = Rows(first)[0].GetProperty("entryId").GetInt64();

        var before = await SnapshotAsync(fixture);

        var (status, body) = await BatchAsync(client, fixture, dryRun: true,
            Upsert("u", id, baseVersion: null, ("STREAM", "2"), ("T_C", "1.5")),
            new { clientRowId = "n", op = "upsert", values = new Dictionary<string, object?> { ["STREAM"] = "3" } });

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.False(body.GetProperty("applied").GetBoolean());
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.Equal(["updated", "added"], Statuses(body));

        // Id нового запису після відкату вигаданий — його немає у звіті.
        Assert.Equal(JsonValueKind.Null, Rows(body)[1].GetProperty("entryId").ValueKind);

        Assert.Equal(before, await SnapshotAsync(fixture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_baseVersion_дає_entryChanged_і_нічого_не_пише()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData");
        var fixture = await SeedAsync(RegistryCodeMode.Manual);

        var e1 = await CreateAsync(client, fixture, "E1", "10");
        var e2 = await CreateAsync(client, fixture, "E2", "20");
        var opened = await VersionsAsync(client, fixture);

        // Хтось інший змінив E1 після того, як сітка його прочитала (PeriodStart — datetime2(3)).
        await Task.Delay(20);
        var (changed, _) = await BatchAsync(client, fixture, dryRun: false, Upsert("other", e1, null, ("T_C", "7")));
        Assert.Equal(HttpStatusCode.OK, changed);

        var before = await SnapshotAsync(fixture);
        var (status, body) = await BatchAsync(client, fixture, dryRun: false,
            Upsert("r1", e1, opened[e1], ("T_C", "8")),
            Upsert("r2", e2, opened[e2], ("T_C", "9")));

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.False(body.GetProperty("applied").GetBoolean());
        Assert.Equal(["error", "updated"], Statuses(body));
        var error = Rows(body)[0].GetProperty("errors")[0];
        Assert.Equal("ECR-REG-4093", error.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-4093.entryChanged", error.GetProperty("messageKey").GetString());
        Assert.Equal($"E1{fixture.Tag}", error.GetProperty("params").GetProperty("entryCode").GetString());

        // Помилка одного рядка — не записано й другого.
        Assert.Equal(before, await SnapshotAsync(fixture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Межі_пакета_і_права()
    {
        using var app = new EcrApiFactory(sql);
        var fixture = await SeedAsync(RegistryCodeMode.Manual);

        using (var editor = await SignedInAsync(app, "Registry.View", "Registry.EditData"))
        {
            var tooMany = Enumerable.Range(0, 2001)
                .Select(i => (object)new { clientRowId = $"r{i}", op = "upsert", code = $"C{i}", values = new Dictionary<string, object?>() })
                .ToArray();
            var (large, largeBody) = await BatchAsync(editor, fixture, dryRun: true, tooMany);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, large);
            Assert.Equal("err.ECR-REQ-0422.batchTooLarge", largeBody.GetProperty("messageKey").GetString());

            var (invalid, invalidBody) = await BatchAsync(editor, fixture, dryRun: true, new { clientRowId = "d", op = "delete" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid);
            Assert.Equal("err.ECR-REQ-0422.batchItemInvalid", invalidBody.GetProperty("messageKey").GetString());
        }

        using var reader = await SignedInAsync(app, "Registry.View");
        var (denied, _) = await BatchAsync(reader, fixture, dryRun: true, new { clientRowId = "n", op = "upsert", code = "N", values = new Dictionary<string, object?>() });
        Assert.Equal(HttpStatusCode.Forbidden, denied);
    }

    private static object Upsert(string clientRowId, long id, string? baseVersion, params (string Field, string Value)[] values)
        => new { clientRowId, op = "upsert", id, baseVersion, values = values.ToDictionary(v => v.Field, v => (object?)v.Value) };

    private static JsonElement[] Rows(JsonElement body) => [.. body.GetProperty("rows").EnumerateArray()];

    private static string[] Statuses(JsonElement body) => [.. Rows(body).Select(r => r.GetProperty("status").GetString()!)];

    private static byte[] Hash(string stream)
        => RegistryKeyNormalizer.Hash(RegistryKeyNormalizer.Canonical([new RegistryKeyPart(CellDataType.String, stream)])!);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> BatchAsync(
        HttpClient client, Fixture fixture, bool dryRun, params object[] items)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries/batch?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative),
            new { items });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<long> CreateAsync(HttpClient client, Fixture fixture, string code, string stream)
    {
        var (status, body) = await BatchAsync(client, fixture, dryRun: false,
            new { clientRowId = code, op = "upsert", code = $"{code}{fixture.Tag}", values = new Dictionary<string, object?> { ["STREAM"] = stream } });
        Assert.True(status == HttpStatusCode.OK && body.GetProperty("applied").GetBoolean(), $"{status}: {body}");
        return Rows(body)[0].GetProperty("entryId").GetInt64();
    }

    private static async Task<Dictionary<long, string>> VersionsAsync(HttpClient client, Fixture fixture)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/registries/{fixture.Code}/rows?limit=50", UriKind.Relative));
        return page.GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetInt64(), r => r.GetProperty("version").GetString()!);
    }

    /// <summary>Усе, що пакет міг би змінити: записи, значення, рядки ключів, ревізія, послідовність авто-коду.</summary>
    private async Task<string> SnapshotAsync(Fixture fixture)
    {
        await using var db = new EcrDbContext(Options());
        var entries = await db.RegistryEntries.CountAsync(e => e.RegistryDefId == fixture.DefinitionId);
        var ids = await db.RegistryEntries.Where(e => e.RegistryDefId == fixture.DefinitionId).Select(e => e.Id).ToListAsync();
        var values = (await db.RegistryValues.AsNoTracking().Where(v => ids.Contains(v.RegistryEntryId)).ToListAsync())
            .Select(v => $"{v.RegistryEntryId}:{v.ValueString}|{v.ValueNumeric}").ToList();
        var keys = await db.RegistryEntryKeys.CountAsync(k => k.RegistryKeyDefId == fixture.KeyDefId && k.IsLive);
        var revision = (await db.RegistryDefs.AsNoTracking().SingleAsync(r => r.Id == fixture.DefinitionId)).DataRevision;

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(bigint, current_value) FROM sys.sequences WHERE object_id = OBJECT_ID(N'dic.RegistryEntryCodeSeq');";
        var sequence = (long)(await command.ExecuteScalarAsync())!;

        return $"entries={entries}; values={string.Join(",", values.Order(StringComparer.Ordinal))}; keys={keys}; revision={revision}; seq={sequence}";
    }

    /// <summary>Довідник: STREAM (обов'язковий, первинний ключ) і T_C.</summary>
    private async Task<Fixture> SeedAsync(RegistryCodeMode codeMode)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"RB{tag}"), Name($"Batch {tag}"), isTemporal: false);
        registry.UseCodeMode(codeMode);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var stream = new RegistryFieldDef(registry.Id, EcrCode.Create("STREAM"), Name("Stream"), CellDataType.String, 1);
        var temperature = new RegistryFieldDef(registry.Id, EcrCode.Create("T_C"), Name("T"), CellDataType.Decimal, 2);
        stream.Update(Name("Stream"), 1, isRequired: true);
        db.RegistryFieldDefs.AddRange(stream, temperature);
        await db.SaveChangesAsync();

        var key = new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Name("Primary"), [stream],
            isPrimary: true, ignoreCase: true, createdByUserId: 0, DateTime.UtcNow);
        db.RegistryKeyDefs.Add(key);
        await db.SaveChangesAsync();

        return new Fixture(registry.Id, registry.Code, tag, key.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"regb_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry batch test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in permissions)
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

    private sealed record Fixture(int DefinitionId, string Code, string Tag, int KeyDefId);
}
