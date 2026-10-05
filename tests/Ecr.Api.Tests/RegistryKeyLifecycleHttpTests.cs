// tests/Ecr.Api.Tests/RegistryKeyLifecycleHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// Складений ключ довідника поза ручним upsert (RT-10b, ФВ-8.15, FEATURE-REGISTRY-TABLES §4.4, §4.6):
/// імпорт CSV, зміна вікна чинності, видалення запису — через справжній контейнер і HTTP.
/// </summary>
/// <remarks>
/// ⛔ Рівень HTTP навмисно: обробники отримують <c>RegistryKeyService</c> необов'язковим
/// параметром (тести, що будують їх руками), і лише контейнер доводить, що на справжньому шляху
/// сервіс справді підставлено.
///
/// Мутаційні докази (RT-10b, §9.2):
/// <list type="bullet">
/// <item><c>DeleteRegistryEntryHandler</c> без <c>keys.ReleaseAsync</c> →
/// <see cref="Видалення_запису_звільняє_ключ"/> отримує 409 на новому записі;</item>
/// <item><c>SetEntryValidityHandler</c> без <c>keys.ApplyAsync</c> →
/// <see cref="Вікно_що_перетинає_дубль_ключа_409_і_не_змінюється"/> отримує 200, а
/// <see cref="Зміна_вікна_перераховує_рядок_ключа"/> бачить старе вікно в рядку ключа;</item>
/// <item>імпорт без <c>DuplicateKeyRows</c> → <see cref="Дубль_ключа_у_CSV_помилка_обох_рядків_і_файл_не_застосовано"/>
/// отримує 409 (ловить уже транзакція) замість звіту з двома рядками;</item>
/// <item>імпорт без пошуку за первинним ключем → <see cref="CSV_рядок_з_ключем_наявного_запису_оновлює_саме_його"/>
/// отримує 409 <c>keyTaken</c> (рядок став новим записом).</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeyLifecycleHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Keys-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Дубль_ключа_у_CSV_помилка_обох_рядків_і_файл_не_застосовано()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        // Рядки 2 і 4 — той самий ключ після нормалізації (регістр, пробіли); рядок 3 — інший.
        var csv = new StringBuilder("code,STREAM,CASE_NAME,T_C\r\n")
            .Append(CultureInfo.InvariantCulture, $"A{fixture.Tag},1D-2,370 Winter,1\r\n")
            .Append(CultureInfo.InvariantCulture, $"B{fixture.Tag},1D-2,370 Summer,2\r\n")
            .Append(CultureInfo.InvariantCulture, $"C{fixture.Tag}, 1d-2 ,370  WINTER,3\r\n")
            .ToString();

        var response = await ImportAsync(client, fixture.Code, csv);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.False(report.GetProperty("applied").GetBoolean());

        var errors = report.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal([2, 4], errors.Select(e => e.GetProperty("row").GetInt32()));
        Assert.All(errors, e =>
        {
            Assert.Equal("err.ECR-REG-4092.keyDuplicateInFile", e.GetProperty("messageKey").GetString());
            Assert.Equal("STREAM, CASE_NAME", e.GetProperty("field").GetString());
        });
        Assert.Equal([$"A{fixture.Tag}", $"C{fixture.Tag}"], errors.Select(e => e.GetProperty("key").GetString()));

        // Звіт не рахує відхилені рядки доданими: лишився один законний.
        Assert.Equal(1, report.GetProperty("added").GetInt32());

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => e.RegistryDefId == fixture.DefinitionId));
        Assert.False(await db.RegistryEntryKeys.AnyAsync(k => k.RegistryKeyDefId == fixture.KeyDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task CSV_рядок_з_ключем_наявного_запису_оновлює_саме_його()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        var created = await PostAsync(client, fixture, id: null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 10m);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        // Код і ключ називають один запис (ключ — з іншим регістром): оновлюється саме він. Рядок з
        // НОВИМ кодом і ключем наявного — помилка (L5-06, тест нижче), а не мовчазне оновлення.
        var csv = $"code,STREAM,CASE_NAME,T_C\r\nE1{fixture.Tag},1d-2,370 WINTER,42\r\n";
        var response = await ImportAsync(client, fixture.Code, csv);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("applied").GetBoolean(), body);
        Assert.Equal((0, 1), (report.GetProperty("added").GetInt32(), report.GetProperty("updated").GetInt32()));

        await using var db = new EcrDbContext(Options());
        Assert.Equal(
            [id],
            await db.RegistryEntries.Where(e => e.RegistryDefId == fixture.DefinitionId).Select(e => e.Id).ToListAsync());
        var temperature = await db.RegistryValues.AsNoTracking()
            .SingleAsync(v => v.RegistryEntryId == id && v.RegistryFieldDefId == fixture.TemperatureFieldId);
        Assert.Equal(42m, temperature.ValueNumeric);

        var key = await db.RegistryEntryKeys.AsNoTracking().SingleAsync(k => k.RegistryKeyDefId == fixture.KeyDefId);
        Assert.Equal(id, key.RegistryEntryId);
        Assert.True(key.IsLive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Новий_код_і_ключ_наявного_запису_це_помилка_рядка_а_не_мовчазне_оновлення()
    {
        // L5-06: без перевірки `byKey != null && byCode == null` рядок оновлював E1 (код NEW губився).
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        var created = await PostAsync(client, fixture, id: null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 10m);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var csv = $"code,STREAM,CASE_NAME,T_C\r\nNEW{fixture.Tag},1D-2,370 Winter,42\r\n";
        var response = await ImportAsync(client, fixture.Code, csv);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.False(report.GetProperty("applied").GetBoolean(), body);
        var error = Assert.Single(report.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, error.GetProperty("row").GetInt32());
        Assert.Equal("err.ECR-REG-4092.keyCodeMismatch", error.GetProperty("messageKey").GetString());

        await using var db = new EcrDbContext(Options());
        Assert.Equal(
            [id],
            await db.RegistryEntries.Where(e => e.RegistryDefId == fixture.DefinitionId).Select(e => e.Id).ToListAsync());
        var temperature = await db.RegistryValues.AsNoTracking()
            .SingleAsync(v => v.RegistryEntryId == id && v.RegistryFieldDefId == fixture.TemperatureFieldId);
        Assert.Equal(10m, temperature.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task CSV_рядок_чий_ключ_і_код_указують_на_різні_записи_помилка_рядка()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, fixture, null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, fixture, null, $"E2{fixture.Tag}", "1D-2", "370 Summer", 2m)).StatusCode);

        // Код — E2, ключ — E1: жоден із двох записів не змінюється мовчки.
        var csv = $"code,STREAM,CASE_NAME,T_C\r\nE2{fixture.Tag},1D-2,370 Winter,99\r\n";
        var response = await ImportAsync(client, fixture.Code, csv);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.False(report.GetProperty("applied").GetBoolean());
        var error = Assert.Single(report.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, error.GetProperty("row").GetInt32());
        Assert.Equal("err.ECR-REG-4092.keyCodeMismatch", error.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Два_рядки_CSV_до_одного_запису_за_ключем_і_за_кодом_ручний_режим_відхиляє_рядок_з_новим_кодом()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, fixture, null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 1m)).StatusCode);

        // Рядок 2 знаходить E1 за ключем, рядок 3 — за кодом і міняє йому ключ: застосувати обидва
        // означало б мовчки лишити переможцем останній. З L5-06 рядок 2 (новий код + ключ E1) відхиляє
        // раніше сама перевірка коду; обидва рядки до одного запису лишаються можливими лише для
        // довідника з автокодом (порожній код), тож у ручному режимі помилку дає рядок 2.
        var csv = new StringBuilder("code,STREAM,CASE_NAME,T_C\r\n")
            .Append(CultureInfo.InvariantCulture, $"NEW{fixture.Tag},1D-2,370 Winter,5\r\n")
            .Append(CultureInfo.InvariantCulture, $"E1{fixture.Tag},1D-2,370 Summer,6\r\n")
            .ToString();

        var response = await ImportAsync(client, fixture.Code, csv);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.False(report.GetProperty("applied").GetBoolean());
        var errors = report.GetProperty("errors").EnumerateArray().ToList();
        var error = Assert.Single(errors);
        Assert.Equal(2, error.GetProperty("row").GetInt32());
        Assert.Equal("err.ECR-REG-4092.keyCodeMismatch", error.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Вікно_що_перетинає_дубль_ключа_409_і_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: true);

        await SeedHolderAsync(fixture, $"W25{fixture.Tag}", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var next = await KeyedEntryAsync(client, fixture, $"W26{fixture.Tag}", new DateOnly(2026, 1, 1));

        // Розширення назад до червня 2025-го перетинає зиму W25 тим самим ключем.
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries/{next}/validity", UriKind.Relative),
            new { from = new DateOnly(2025, 6, 1), to = (DateOnly?)null });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-4092", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-4092.keyWindowOverlap", problem.GetProperty("messageKey").GetString());
        Assert.Equal($"W25{fixture.Tag}", problem.GetProperty("entryCode").GetString());

        // Відмова відкотила й саме вікно запису, і рядок ключа.
        await using var db = new EcrDbContext(Options());
        var entry = await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == next);
        Assert.Equal(new DateOnly(2026, 1, 1), entry.ValidFrom);
        var key = await db.RegistryEntryKeys.AsNoTracking().SingleAsync(k => k.RegistryEntryId == next);
        Assert.Equal(new DateOnly(2026, 1, 1), key.ValidFromKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Зміна_вікна_перераховує_рядок_ключа()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: true);

        var entryId = await KeyedEntryAsync(client, fixture, $"W26{fixture.Tag}", new DateOnly(2026, 1, 1));

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries/{entryId}/validity", UriKind.Relative),
            new { from = new DateOnly(2026, 2, 1), to = new DateOnly(2026, 4, 1) });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        // Рядок ключа дзеркалить НОВЕ вікно: інакше наступна перевірка перетину дивилася б на старе.
        await using var db = new EcrDbContext(Options());
        var key = await db.RegistryEntryKeys.AsNoTracking().SingleAsync(k => k.RegistryEntryId == entryId);
        Assert.Equal(new DateOnly(2026, 2, 1), key.ValidFromKey);
        Assert.Equal(new DateOnly(2026, 4, 1), key.ValidTo);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Видалення_запису_звільняє_ключ()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var fixture = await SeedAsync(isTemporal: false);

        var created = await PostAsync(client, fixture, id: null, $"E1{fixture.Tag}", "1D-2", "370 Winter", 1m);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var firstId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var deleted = await client.DeleteAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries/{firstId}", UriKind.Relative));
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        await using (var db = new EcrDbContext(Options()))
        {
            var row = await db.RegistryEntryKeys.AsNoTracking().SingleAsync(k => k.RegistryEntryId == firstId);
            Assert.False(row.IsLive);
        }

        var second = await PostAsync(client, fixture, id: null, $"E2{fixture.Tag}", "1D-2", "370 Winter", 2m);
        Assert.True(second.StatusCode == HttpStatusCode.Created, $"{second.StatusCode}: {await second.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, string registryCode, string csv)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "entries.csv");

        return await client.PostAsync(
            new Uri($"/api/v1/registries/{registryCode}/entries/import?dryRun=false", UriKind.Relative), content);
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
    /// Запис із вікном від <paramref name="from"/> і ключем <c>1D-2 · 370 Winter</c>, записаним
    /// справжнім upsert (вікно upsert не змінює, тож запис створюється заздалегідь).
    /// </summary>
    private async Task<long> KeyedEntryAsync(HttpClient client, Fixture fixture, string code, DateOnly from)
    {
        long id;
        await using (var db = new EcrDbContext(Options()))
        {
            var entry = new RegistryEntry(fixture.DefinitionId, EcrCode.Create(code), Name(code));
            entry.SetValidity(from, null);
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            id = entry.Id;
        }

        var response = await PostAsync(client, fixture, id, code, "1D-2", "370 Winter", 1m);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return id;
    }

    /// <summary>Запис, що вже тримає ключ <c>1D-2 · 370 Winter</c> у вікні [from, to).</summary>
    private async Task SeedHolderAsync(Fixture fixture, string code, DateOnly from, DateOnly to)
    {
        await using var db = new EcrDbContext(Options());

        var entry = new RegistryEntry(fixture.DefinitionId, EcrCode.Create(code), Name(code));
        entry.SetValidity(from, to);
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

    /// <summary>Довідник «кейси потоків»: первинний ключ (STREAM, CASE_NAME) і температура.</summary>
    private async Task<Fixture> SeedAsync(bool isTemporal)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"KL{tag}"), Name($"Key lifecycle {tag}"), isTemporal);
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

        return new Fixture(registry.Id, registry.Code, tag, key.Id, stream.Id, caseName.Id, temperature.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і правами <c>Registry.View</c>, <c>Registry.EditData</c>.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regkl_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry key lifecycle test"));
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
        int DefinitionId, string Code, string Tag, int KeyDefId, int StreamFieldId, int CaseFieldId, int TemperatureFieldId);
}
