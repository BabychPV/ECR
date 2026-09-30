// tests/Ecr.Api.Tests/RegistryKeyConflictHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Складений ключ довідника на шляху <c>POST /api/v1/registries/{code}/entries</c>
/// (RT-10a, ФВ-8.15, FEATURE-REGISTRY-TABLES §4.3–4.4, §7.2).
/// </summary>
/// <remarks>
/// ⛔ Рівень HTTP навмисно: статус задає не обробник, а <c>ExceptionHandlingMiddleware</c>, і
/// код <c>ECR-REG-4092</c> без свого шляху до 409 доїхав би як 422 «дані невірні».
///
/// Мутаційні докази (RT-10a, §9.2):
/// <list type="bullet">
/// <item>у <c>UpsertRegistryEntryHandler</c> замінити <c>keys.SaveAsync</c> на
/// <c>uow.SaveChangesAsync</c> → <see cref="Другий_запис_з_тим_самим_ключем_409"/> отримує
/// <c>201</c>;</item>
/// <item>прибрати арм <c>RegistryKeyConflict</c> у middleware разом із правилом суфікса
/// <c>-409N</c> для <c>BusinessRuleException</c> → той самий тест отримує <c>422</c>. Прибрати
/// лише арм — 409 лишається: суфікс <c>-4092</c> ловить загальне правило.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeyConflictHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Keys-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Другий_запис_з_тим_самим_ключем_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        var first = await PostAsync(client, fixture, id: null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 49.9999977539011000m);
        Assert.True(first.StatusCode == HttpStatusCode.Created, $"{first.StatusCode}: {await first.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        var firstId = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        // Той самий ключ після нормалізації (§4.2): регістр і пробіли не роблять його іншим.
        var second = await PostAsync(client, fixture, id: null, $"E2{fixture.Tag}", " 1d-2 ", "370   WINTER", 1m);
        var body = await second.Content.ReadAsStringAsync();

        Assert.True(second.StatusCode == HttpStatusCode.Conflict, $"{second.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-4092", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-4092.keyTaken", problem.GetProperty("messageKey").GetString());
        Assert.Equal("PK", problem.GetProperty("key").GetString());
        Assert.Equal("1D-2 · 370 Winter", problem.GetProperty("keyText").GetString());
        Assert.Equal($"E1{fixture.Tag}", problem.GetProperty("entryCode").GetString());
        Assert.Equal(firstId.ToString(System.Globalization.CultureInfo.InvariantCulture), problem.GetProperty("entryId").GetString());
        Assert.Equal($"Another entry (E1{fixture.Tag}) already has PK = 1D-2 · 370 Winter.", problem.GetProperty("detail").GetString());
        Assert.Equal("Key already in use", problem.GetProperty("title").GetString());

        // Відмова відкотила ВСЕ: ні запису, ні значень, ні рядка ключа другого не лишилось.
        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => e.Code == $"E2{fixture.Tag}"));
        Assert.Equal(1, await db.RegistryEntryKeys.CountAsync(k => k.RegistryKeyDefId == fixture.KeyDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Оновлення_запису_без_зміни_ключа_не_конфліктує_саме_з_собою()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        var created = await PostAsync(client, fixture, id: null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 10m);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var updated = await PostAsync(client, fixture, id, $"E1{fixture.Tag}", "1D-2", "370 Winter", 11m);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, $"{updated.StatusCode}: {await updated.Content.ReadAsStringAsync()}");

        // Той самий рядок ключа перераховано на місці, а не вставлено другий.
        await using var db = new EcrDbContext(Options());
        var row = await db.RegistryEntryKeys.AsNoTracking().SingleAsync(k => k.RegistryKeyDefId == fixture.KeyDefId);
        Assert.Equal(id, row.RegistryEntryId);
        Assert.True(row.IsLive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Видалений_запис_не_блокує_ключ()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        await SeedHolderAsync(fixture, $"D{fixture.Tag}", from: null, to: null, deleted: true);

        var response = await PostAsync(client, fixture, id: null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 1m);

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Темпоральний_дубль_у_вікні_що_не_перетинається_проходить()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: true);

        await SeedHolderAsync(fixture, $"W25{fixture.Tag}", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), deleted: false);

        // Наступна зима починається рівно там, де попередня закінчилась: напівінтервали не
        // перетинаються (§4.4, ValidityWindow).
        var next = await EntryAsync(fixture, $"W26{fixture.Tag}", new DateOnly(2026, 1, 1), to: null);

        var response = await PostAsync(client, fixture, next, $"W26{fixture.Tag}", "1D-2", "370 winter", 2m);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        await using var db = new EcrDbContext(Options());
        Assert.Equal(2, await db.RegistryEntryKeys.CountAsync(k => k.RegistryKeyDefId == fixture.KeyDefId && k.IsLive));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Темпоральний_дубль_у_перетинному_вікні_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: true);

        await SeedHolderAsync(fixture, $"W25{fixture.Tag}", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), deleted: false);

        // Початок інший (індекс UX_RegistryEntryKey_Live цього не побачив би), вікна
        // перетинаються з червня по грудень 2025-го — ловить служба ключів.
        var overlapping = await EntryAsync(fixture, $"S25{fixture.Tag}", new DateOnly(2025, 6, 1), to: null);

        var response = await PostAsync(client, fixture, overlapping, $"S25{fixture.Tag}", "1D-2", "370 Winter", 3m);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-4092", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-4092.keyWindowOverlap", problem.GetProperty("messageKey").GetString());
        Assert.Equal($"W25{fixture.Tag}", problem.GetProperty("entryCode").GetString());
        Assert.Equal("1D-2 · 370 Winter", problem.GetProperty("keyText").GetString());
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, Fixture fixture, long? id, string code, string stream, string caseName, decimal temperature)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative),
            new
            {
                id,
                registryDefId = fixture.DefinitionId,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?>
                {
                    ["STREAM"] = stream,
                    ["CASE_NAME"] = caseName,
                    ["T_C"] = temperature,
                },
            });

    /// <summary>
    /// Запис, що вже тримає ключ <c>1D-2 · 370 Winter</c>: значення й рядок ключа, як їх лишив
    /// би попередній запис через службу ключів.
    /// </summary>
    private async Task SeedHolderAsync(Fixture fixture, string code, DateOnly? from, DateOnly? to, bool deleted)
    {
        await using var db = new EcrDbContext(Options());

        var entry = new RegistryEntry(fixture.DefinitionId, EcrCode.Create(code), Name(code));
        entry.SetValidity(from, to);
        if (deleted)
        {
            entry.SoftDelete();
        }

        db.RegistryEntries.Add(entry);

        var stream = new RegistryValue(entry, fixture.StreamFieldId);
        stream.Set(CellDataType.String, "1D-2", unitId: null);
        var caseName = new RegistryValue(entry, fixture.CaseFieldId);
        caseName.Set(CellDataType.String, "370 Winter", unitId: null);
        db.RegistryValues.AddRange(stream, caseName);

        var canonical = RegistryKeyNormalizer.Canonical(
            [new RegistryKeyPart(CellDataType.String, "1D-2"), new RegistryKeyPart(CellDataType.String, "370 Winter")])!;
        db.RegistryEntryKeys.Add(new RegistryEntryKey(entry, fixture.KeyDefId, RegistryKeyNormalizer.Hash(canonical), "1D-2 · 370 Winter"));

        await db.SaveChangesAsync();
    }

    /// <summary>Запис без значень — з вікном, яке upsert не змінює.</summary>
    private async Task<long> EntryAsync(Fixture fixture, string code, DateOnly? from, DateOnly? to)
    {
        await using var db = new EcrDbContext(Options());

        var entry = new RegistryEntry(fixture.DefinitionId, EcrCode.Create(code), Name(code));
        entry.SetValidity(from, to);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    /// <summary>Довідник «кейси потоків»: первинний ключ (STREAM, CASE_NAME) і ще одне поле.</summary>
    private async Task<Fixture> SeedAsync(bool isTemporal)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"SC{tag}"), Name($"Stream cases {tag}"), isTemporal);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var stream = new RegistryFieldDef(registry.Id, EcrCode.Create("STREAM"), Name("Stream"), CellDataType.String, 1);
        var caseName = new RegistryFieldDef(registry.Id, EcrCode.Create("CASE_NAME"), Name("Case"), CellDataType.String, 2);
        var temperature = new RegistryFieldDef(registry.Id, EcrCode.Create("T_C"), Name("T"), CellDataType.Decimal, 3);
        stream.Update(Name("Stream"), 1, isRequired: true);
        caseName.Update(Name("Case"), 2, isRequired: true);
        db.RegistryFieldDefs.AddRange(stream, caseName, temperature);
        await db.SaveChangesAsync();

        var key = new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Name("Primary"), [stream, caseName],
            isPrimary: true, ignoreCase: true, createdByUserId: 0, DateTime.UtcNow);
        db.RegistryKeyDefs.Add(key);
        await db.SaveChangesAsync();

        return new Fixture(registry.Id, registry.Code, tag, key.Id, stream.Id, caseName.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і правом <c>Registry.EditData</c>.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regkey_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry keys test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.EditData"));
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

    private sealed record Fixture(
        int DefinitionId, string Code, string Tag, int KeyDefId, int StreamFieldId, int CaseFieldId);
}
