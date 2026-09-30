// tests/Ecr.Api.Tests/RegistryExternalLockHttpTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// D-211: записи довідника з <c>SourceKind = External</c> вручну не змінюються (master — AF, D-49) —
/// наскрізь через HTTP і SQL Server: upsert (створення й зміна), вікно чинності, видалення, CSV
/// (прев'ю й застосування), пакет RT-14 (<c>dryRun</c> і запис), каскад композиції в External.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати <c>ExternalRegistryGuard.EnsureManualEditAllowed</c> з будь-якого
/// обробника → відповідний крок <see cref="External_ручні_правки_відхилено_і_нічого_не_записано"/>
/// отримує 2xx замість 409; з перевірки частин у <c>DeleteRegistryEntryHandler</c> →
/// <see cref="Каскад_батька_Local_у_External_довідник_відхилено"/> червоний (204, частину видалено).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryExternalLockHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-External-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-211")]
    public async Task External_ручні_правки_відхилено_і_нічого_не_записано()
    {
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInAsync(app);
        using var _ = client;
        var f = await SeedAsync(RegistrySourceKind.External, userId);
        var before = await SnapshotAsync(f);

        await AssertExternalAsync(await UpsertAsync(client, f, id: null, $"N{f.Tag}", 5m), "створення");
        await AssertExternalAsync(await UpsertAsync(client, f, f.EntryId, f.EntryCode, 6m), "зміна");
        await AssertExternalAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/v1/registries/{f.Code}/entries/{f.EntryId}/validity", UriKind.Relative),
                new { from = "2026-01-01", to = (string?)null }),
            "вікно чинності");
        await AssertExternalAsync(
            await client.DeleteAsync(new Uri($"/api/v1/registries/{f.Code}/entries/{f.EntryId}", UriKind.Relative)),
            "видалення");
        await AssertExternalAsync(await ImportAsync(client, f, dryRun: true), "CSV прев'ю");
        await AssertExternalAsync(await ImportAsync(client, f, dryRun: false), "CSV застосування");
        await AssertExternalAsync(await BatchAsync(client, f, dryRun: true), "пакет dryRun");
        await AssertExternalAsync(await BatchAsync(client, f, dryRun: false), "пакет");

        Assert.Equal(before, await SnapshotAsync(f));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-211")]
    [InlineData(RegistrySourceKind.Hybrid)]
    [InlineData(RegistrySourceKind.Local)]
    public async Task Hybrid_і_Local_ручні_правки_дозволено(RegistrySourceKind kind)
    {
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInAsync(app);
        using var _ = client;
        var f = await SeedAsync(kind, userId);

        await AssertStatusAsync(await UpsertAsync(client, f, id: null, $"N{f.Tag}", 5m), HttpStatusCode.Created, app);
        await AssertStatusAsync(await UpsertAsync(client, f, f.EntryId, f.EntryCode, 6m), HttpStatusCode.OK, app);
        await AssertStatusAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/v1/registries/{f.Code}/entries/{f.EntryId}/validity", UriKind.Relative),
                new { from = "2026-01-01", to = (string?)null }),
            HttpStatusCode.OK,
            app);
        await AssertStatusAsync(await ImportAsync(client, f, dryRun: true), HttpStatusCode.OK, app);
        await AssertStatusAsync(await BatchAsync(client, f, dryRun: true), HttpStatusCode.OK, app);
        await AssertStatusAsync(
            await client.DeleteAsync(new Uri($"/api/v1/registries/{f.Code}/entries/{f.EntryId}", UriKind.Relative)),
            HttpStatusCode.NoContent,
            app);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-211")]
    public async Task Каскад_батька_Local_у_External_довідник_відхилено()
    {
        using var app = new EcrApiFactory(sql);
        var (client, userId) = await SignedInAsync(app);
        using var _ = client;
        var f = await SeedAsync(RegistrySourceKind.Local, userId);
        var (childRegistryId, childEntryId) = await SeedCascadeChildAsync(f, userId, RegistrySourceKind.External);

        await AssertExternalAsync(
            await client.DeleteAsync(new Uri($"/api/v1/registries/{f.Code}/entries/{f.EntryId}", UriKind.Relative)),
            "каскад у External");

        await using (var db = new EcrDbContext(Options()))
        {
            Assert.False(await db.RegistryEntries.AnyAsync(e => (e.Id == f.EntryId || e.Id == childEntryId) && e.IsDeleted));
        }

        // Контроль: та сама композиція, дочірній довідник Local — видалення проходить каскадом.
        await using (var db = new EcrDbContext(Options()))
        {
            var child = await db.RegistryDefs.SingleAsync(r => r.Id == childRegistryId);
            child.SwitchSource(RegistrySourceKind.Local);
            await db.SaveChangesAsync();
        }

        await AssertStatusAsync(
            await client.DeleteAsync(new Uri($"/api/v1/registries/{f.Code}/entries/{f.EntryId}", UriKind.Relative)),
            HttpStatusCode.NoContent,
            app);

        await using (var db = new EcrDbContext(Options()))
        {
            Assert.True(await db.RegistryEntries.AsNoTracking().Where(e => e.Id == childEntryId).Select(e => e.IsDeleted).SingleAsync());
        }
    }

    private static async Task AssertExternalAsync(HttpResponseMessage response, string step)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{step}: {response.StatusCode}: {body}");
            var problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal("ECR-REG-0409", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-REG-0409.externalSource", problem.GetProperty("messageKey").GetString());
        }
    }

    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected, EcrApiFactory app)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        }
    }

    private static Task<HttpResponseMessage> UpsertAsync(HttpClient client, Fixture f, long? id, string code, decimal qty)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{f.Code}/entries", UriKind.Relative),
            new
            {
                id,
                registryDefId = f.Id,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["QTY"] = qty },
            });

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, Fixture f, bool dryRun)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"code,QTY\r\nC{f.Tag},7\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "entries.csv");

        return client.PostAsync(
            new Uri($"/api/v1/registries/{f.Code}/entries/import?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative),
            content);
    }

    private static Task<HttpResponseMessage> BatchAsync(HttpClient client, Fixture f, bool dryRun)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{f.Code}/entries/batch?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative),
            new
            {
                items = new object[]
                {
                    new { clientRowId = "u", op = "upsert", id = f.EntryId, values = new Dictionary<string, object?> { ["QTY"] = 8m } },
                    new { clientRowId = "n", op = "upsert", code = $"B{f.Tag}", values = new Dictionary<string, object?> { ["QTY"] = 9m } },
                },
            });

    /// <summary>Усе, що ручна правка могла б змінити: записи, видалення, вікна, значення, ревізія.</summary>
    private async Task<string> SnapshotAsync(Fixture f)
    {
        await using var db = new EcrDbContext(Options());
        var entries = (await db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == f.Id).OrderBy(e => e.Id).ToListAsync())
            .Select(e => $"{e.Id}:{e.Code}:{e.IsDeleted}:{e.ValidFrom}:{e.ValidTo}").ToList();
        var ids = await db.RegistryEntries.Where(e => e.RegistryDefId == f.Id).Select(e => e.Id).ToListAsync();
        var values = (await db.RegistryValues.AsNoTracking().Where(v => ids.Contains(v.RegistryEntryId)).ToListAsync())
            .Select(v => $"{v.RegistryEntryId}:{v.RegistryFieldDefId}:{v.ValueNumeric}").Order(StringComparer.Ordinal).ToList();
        var revision = (await db.RegistryDefs.AsNoTracking().SingleAsync(r => r.Id == f.Id)).DataRevision;

        return $"entries={string.Join(",", entries)}; values={string.Join(",", values)}; revision={revision}";
    }

    /// <summary>Довідник із полем QTY (Decimal) і одним записом; джерело — <paramref name="kind"/>.</summary>
    private async Task<Fixture> SeedAsync(RegistrySourceKind kind, int userId)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"RX{tag}"), Name($"External lock {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var qty = new RegistryFieldDef(registry.Id, EcrCode.Create("QTY"), Name("Qty"), CellDataType.Decimal, 1);
        db.RegistryFieldDefs.Add(qty);
        await db.SaveChangesAsync();

        var entry = new RegistryEntry(registry.Id, EcrCode.Create($"E{tag}"), Name($"E{tag}"), userId, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();

        var value = new RegistryValue(entry.Id, qty.Id);
        value.Set(CellDataType.Decimal, 1m, null);
        db.RegistryValues.Add(value);

        registry.SwitchSource(kind);
        await db.SaveChangesAsync();

        return new Fixture(registry.Id, registry.Code, tag, entry.Id, entry.Code);
    }

    /// <summary>Дочірній довідник-композиція (<c>Cascade</c>) з одним записом, що посилається на запис фікстури.</summary>
    private async Task<(int RegistryId, long EntryId)> SeedCascadeChildAsync(Fixture f, int userId, RegistrySourceKind kind)
    {
        await using var db = new EcrDbContext(Options());

        var child = new RegistryDef(EcrCode.Create($"RC{f.Tag}"), Name($"Parts {f.Tag}"), isTemporal: false);
        db.RegistryDefs.Add(child);
        await db.SaveChangesAsync();

        var parent = new RegistryFieldDef(child.Id, EcrCode.Create("PARENT"), Name("Parent"), CellDataType.Lookup, 1);
        parent.PointTo(f.Id);
        parent.ComposeInto(ParentDeletePolicy.Cascade);
        db.RegistryFieldDefs.Add(parent);
        await db.SaveChangesAsync();

        var entry = new RegistryEntry(child.Id, EcrCode.Create($"P{f.Tag}"), Name($"P{f.Tag}"), userId, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();

        var value = new RegistryValue(entry.Id, parent.Id);
        value.Set(CellDataType.Lookup, f.EntryId, null);
        db.RegistryValues.Add(value);

        child.SwitchSource(kind);
        await db.SaveChangesAsync();

        return (child.Id, entry.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<(HttpClient Client, int UserId)> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regx_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry external lock test"));
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
        return (client, userId);
    }

    private sealed record Fixture(int Id, string Code, string Tag, long EntryId, string EntryCode);
}
