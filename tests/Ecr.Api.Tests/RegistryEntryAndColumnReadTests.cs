// tests/Ecr.Api.Tests/RegistryEntryAndColumnReadTests.cs
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
/// Читання, з якого відкриваються форми правки (X-02, X-03, R-04, четвертий
/// раунд UX), — наскрізно через HTTP на справжній базі.
/// </summary>
/// <remarks>
/// ⛔ Обробники перевірені модульно; тут — те, чого модульний тест не бачить:
/// маршрут, серіалізація <c>LocalizedText</c> і значень, і головне — що
/// збереження запису лише англійською назвою НЕ стирає переклад у базі.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryAndColumnReadTests(SqlServerFixture sql)
{
    private const string Password = "Api-R4D-Read-2026!";

    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_довідника_читається_цілком_і_правка_англійською_не_стирає_переклад()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData").ConfigureAwait(true);

        var (code, registryId, entryId) = await SeedRegistryAsync().ConfigureAwait(true);

        var detail = await ReadJsonAsync(client, app, $"/api/v1/registries/{code}/entries/{entryId}").ConfigureAwait(true);

        Assert.Equal("Фенол", detail.GetProperty("displayL10n").GetProperty("values").GetProperty("ru").GetString());
        Assert.Equal("12.5", detail.GetProperty("values").GetProperty("LIMIT").GetString());

        // ⛔ Та сама форма тіла, що шле клієнт: назва лише під `en`, значень немає.
        var save = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/entries", UriKind.Relative),
            new
            {
                id = entryId,
                registryDefId = registryId,
                code = "PHENOL",
                display = new { values = new Dictionary<string, string> { ["en"] = "Phenol (C6H5OH)" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?>(),
            }).ConfigureAwait(true);

        Assert.True(save.IsSuccessStatusCode, $"{save.StatusCode}: {app.ErrorsText}");

        var after = await ReadJsonAsync(client, app, $"/api/v1/registries/{code}/entries/{entryId}").ConfigureAwait(true);
        var names = after.GetProperty("displayL10n").GetProperty("values");

        Assert.Equal("Phenol (C6H5OH)", names.GetProperty("en").GetString());
        Assert.Equal("Фенол", names.GetProperty("ru").GetString());
        Assert.Equal("12.5", after.GetProperty("values").GetProperty("LIMIT").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_чужого_довідника_за_адресою_іншого_коду_404()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View").ConfigureAwait(true);

        var (_, _, entryId) = await SeedRegistryAsync().ConfigureAwait(true);
        var (otherCode, _, _) = await SeedRegistryAsync().ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/registries/{otherCode}/entries/{entryId}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_читається_з_одиницею_і_точністю_яких_немає_в_структурі()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.View").ConfigureAwait(true);

        var (versionId, tableId, columnCode, unitId) = await SeedColumnAsync().ConfigureAwait(true);

        var column = await ReadJsonAsync(
            client, app, $"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/{columnCode}").ConfigureAwait(true);

        Assert.Equal(unitId, column.GetProperty("unitId").GetInt32());
        Assert.Equal(18, column.GetProperty("precision").GetInt32());
        Assert.Equal(4, column.GetProperty("scale").GetInt32());
        Assert.Equal("0", column.GetProperty("defaultValue").GetString());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, EcrApiFactory app, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}: {app.ErrorsText}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
    }

    private async Task<(string Code, int RegistryId, long EntryId)> SeedRegistryAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"RR4D{tag}"), Name($"R4D {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("LIMIT"), Name("Limit"), CellDataType.Decimal, 1);
        db.RegistryFieldDefs.Add(field);

        var entry = new RegistryEntry(
            registry.Id,
            EcrCode.Create("PHENOL"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Phenol", ["ru"] = "Фенол" }),
            1,
            Now);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var value = new RegistryValue(entry.Id, field.Id);
        value.Set(CellDataType.Decimal, 12.5m, null);
        db.RegistryValues.Add(value);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (registry.Code, registry.Id, entry.Id);
    }

    private async Task<(int VersionId, int TableId, string ColumnCode, int UnitId)> SeedColumnAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);

        var template = new Template(EcrCode.Create($"TR4D{tag}"), Name($"R4D {tag}"), createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"SH{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"TB{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var column = new ColumnDef(table.Id, EcrCode.Create("LIMIT"), Name("Limit"), 1, CellDataType.Decimal);
        column.SetNumericFormat(18, 4);
        column.SetPresentation("0", null, null);
        column.SetUnit(unitId);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (version.Id, table.Id, column.Code, unitId);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і заданими правами.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"r4d_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("R4D read test"));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

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
}
