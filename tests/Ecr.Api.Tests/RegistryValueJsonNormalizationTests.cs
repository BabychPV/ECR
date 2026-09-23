// tests/Ecr.Api.Tests/RegistryValueJsonNormalizationTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// <c>POST /api/v1/registries/{code}/entries</c> — значення полів приходять
/// із тіла HTTP-запиту через <c>System.Text.Json</c>, тобто фактично
/// <see cref="JsonElement"/>, а не готовий CLR-тип (той самий клас дефекту,
/// що <c>A7-01</c> для комірок документа, — див. <c>CellValueReader.Normalize</c>).
/// </summary>
/// <remarks>
/// ⛔ <c>UpsertRegistryEntryHandler.ApplyValuesAsync</c> передавав значення з
/// тіла запиту в <c>RegistryValue.Set</c> НЕ розгорнутими. <c>RegistryValue.Set</c>
/// приводить значення до типу голими <c>Convert.ToDecimal</c>/<c>ToBoolean</c>/
/// <c>ToInt64</c> (і патерн-матчем для дати) — жоден із них не впізнає
/// <see cref="JsonElement"/>, тож СПРАВЖНІЙ HTTP-запит із коректним числом,
/// булевим чи датою відмовляв би так само, як і зіпсований ввід.
///
/// ⚠ Тестами застосункового рівня це не ловилося: вони конструюють DTO
/// напряму з готовим <c>decimal</c>/<c>bool</c>, минаючи межу «HTTP →
/// обробник», на якій і живе дефект.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryValueJsonNormalizationTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Json-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Коректні_значення_кожного_типу_через_HTTP_зберігаються_і_видно_наступним_читанням()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = fixture.DefinitionId,
                code = $"E{fixture.Tag}",
                display = new { values = new Dictionary<string, string> { ["en"] = "Entry" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?>
                {
                    ["IntF"] = 7,
                    ["DecF"] = 12.5m,
                    ["BoolF"] = true,
                    ["DateF"] = "2026-05-01",
                    ["LookF"] = fixture.RefEntryId,
                },
            }).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        // ⛔ Це і є доказ дефекту/фіксу: коректний JSON number/bool/string для
        // КОЖНОГО типізованого поля має ЗБЕРЕГТИСЯ, а не впасти на приведенні
        // типу — так само, як уже працює `PatchCellsHandler` для комірок
        // документа після `A7-01`.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"POST запису довідника: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        var created = JsonDocument.Parse(body).RootElement;
        var entryId = created.GetProperty("id").GetInt64();

        await using var db = new EcrDbContext(Options());
        var values = await db.RegistryValues
            .AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId)
            .ToListAsync().ConfigureAwait(true);

        var byField = values.ToDictionary(v => v.RegistryFieldDefId);

        Assert.Equal(7m, byField[fixture.FieldIds["IntF"]].ValueNumeric);
        Assert.Equal(12.5m, byField[fixture.FieldIds["DecF"]].ValueNumeric);
        Assert.True(byField[fixture.FieldIds["BoolF"]].ValueBool);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), byField[fixture.FieldIds["DateF"]].ValueDate);
        Assert.Equal(fixture.RefEntryId, byField[fixture.FieldIds["LookF"]].ValueRefEntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Текст_замість_числа_у_Decimal_полі_дає_422_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = fixture.DefinitionId,
                code = $"E{fixture.Tag}",
                display = new { values = new Dictionary<string, string> { ["en"] = "Entry" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["DecF"] = "не число" },
            }).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Текст_замість_булевого_дає_422_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = fixture.DefinitionId,
                code = $"E{fixture.Tag}",
                display = new { values = new Dictionary<string, string> { ["en"] = "Entry" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["BoolF"] = "яскраво" },
            }).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нерозбірна_дата_дає_422_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = fixture.DefinitionId,
                code = $"E{fixture.Tag}",
                display = new { values = new Dictionary<string, string> { ["en"] = "Entry" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["DateF"] = "не дата" },
            }).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ідентифікатор_поза_межами_int_у_Lookup_полі_дає_422_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = fixture.DefinitionId,
                code = $"E{fixture.Tag}",
                display = new { values = new Dictionary<string, string> { ["en"] = "Entry" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["LookF"] = "не ідентифікатор" },
            }).ConfigureAwait(true);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REG-0422", problem.GetProperty("errorCode").GetString());
    }

    /// <summary>Довідник-джерело з одним записом (ціль для поля <c>Lookup</c>) і довідник із п'ятьма типізованими полями.</summary>
    private async Task<Fixture> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = new EcrDbContext(Options());

        var lookupDef = new RegistryDef(EcrCode.Create($"LK{tag}"), Name($"Lookup {tag}"), isTemporal: false);
        db.RegistryDefs.Add(lookupDef);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var refEntry = new RegistryEntry(lookupDef.Id, EcrCode.Create($"R{tag}"), Name($"Ref {tag}"));
        db.RegistryEntries.Add(refEntry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var mainDef = new RegistryDef(EcrCode.Create($"TYP{tag}"), Name($"Types {tag}"), isTemporal: false);
        db.RegistryDefs.Add(mainDef);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var intField = new RegistryFieldDef(mainDef.Id, EcrCode.Create("IntF"), Name("Int"), CellDataType.Int, 1);
        var decField = new RegistryFieldDef(mainDef.Id, EcrCode.Create("DecF"), Name("Decimal"), CellDataType.Decimal, 2);
        var boolField = new RegistryFieldDef(mainDef.Id, EcrCode.Create("BoolF"), Name("Bool"), CellDataType.Bool, 3);
        var dateField = new RegistryFieldDef(mainDef.Id, EcrCode.Create("DateF"), Name("Date"), CellDataType.Date, 4);
        var lookField = new RegistryFieldDef(mainDef.Id, EcrCode.Create("LookF"), Name("Lookup"), CellDataType.Lookup, 5);
        lookField.PointTo(lookupDef.Id);

        db.RegistryFieldDefs.AddRange(intField, decField, boolField, dateField, lookField);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var fieldIds = new Dictionary<string, int>
        {
            ["IntF"] = intField.Id,
            ["DecF"] = decField.Id,
            ["BoolF"] = boolField.Id,
            ["DateF"] = dateField.Id,
            ["LookF"] = lookField.Id,
        };

        return new Fixture(mainDef.Id, mainDef.Code, tag, refEntry.Id, fieldIds);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і правом <c>Registry.EditData</c>.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regjson_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry json test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.EditData"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>Довідник із п'ятьма типізованими полями і ціль для поля <c>Lookup</c>.</summary>
    private sealed record Fixture(
        int DefinitionId,
        string Code,
        string Tag,
        long RefEntryId,
        Dictionary<string, int> FieldIds);
}
