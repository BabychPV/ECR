// tests/Ecr.Api.Tests/RegistryEntriesAsOfHttpTests.cs
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
/// <c>GET /api/v1/registries/{code}/entries</c> через СПРАВЖНІЙ HTTP-конвеєр
/// (<see cref="EcrApiFactory"/>), не виклик <c>GetRegistryEntriesHandler</c>
/// напряму: живий прогін показав, що дефект жив саме на межі маршруту, а не в
/// методі обробника (`ASP.NET Core model binding` резолвить відсутній
/// <c>[FromQuery] DateOnly asOf</c> у <c>default</c> так само, як прямий
/// виклик C#, тож різниця тут не в біндингу — вона в тому, що Lookup-піцкери
/// клієнта НІКОЛИ не посилали <c>asOf</c>, і саме це доводить перший тест).
/// </summary>
/// <remarks>
/// ⛔ До фіксу (`GetRegistryEntriesHandler.cs`) перевірка обов'язковості
/// <c>asOf</c> була БЕЗУМОВНОЮ: запит без параметра для БУДЬ-ЯКОГО довідника —
/// темпорального чи ні — давав <c>422 ECR-REQ-0422</c>. Жоден із трьох
/// клієнтських викликів (`RegistriesPage.tsx`, `DocumentGrid.tsx`,
/// `DocumentHeaderPanel.tsx`) не передавав <c>asOf</c>, тобто Lookup-піцкер
/// був непрацездатним для КОЖНОГО довідника, включно з нетемпоральними
/// (`Substance`).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntriesAsOfHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-AsOf-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нетемпоральний_довідник_віддає_записи_БЕЗ_asOf_у_запиті()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedRegistryAsync(isTemporal: false).ConfigureAwait(true);

        // ⛔ Рівно те, що шле сьогодні кожен із трьох клієнтських Lookup-
        // піцкерів: без `?asOf=`.
        var response = await client
            .GetAsync(new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var entries = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Contains(
            entries.EnumerateArray(),
            e => e.GetProperty("id").GetInt64() == fixture.EntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Темпоральний_довідник_без_asOf_дає_422()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedRegistryAsync(isTemporal: true).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/registries/{fixture.Code}/entries", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REQ-0422.asOfRequired", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Темпоральний_довідник_з_asOf_фільтрує_записи_за_вікном_чинності()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var fixture = await SeedRegistryAsync(isTemporal: true).ConfigureAwait(true);
        await CloseEntryValidityAsync(fixture.EntryId, until: new DateOnly(2025, 1, 1)).ConfigureAwait(true);

        // Запис закрито з 2025-01-01 — на дату ПІСЛЯ закриття його вже нема.
        var after = await client
            .GetAsync(new Uri(
                $"/api/v1/registries/{fixture.Code}/entries?asOf=2026-01-15", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(after.IsSuccessStatusCode, $"{after.StatusCode}: {app.ErrorsText}");

        var afterEntries = JsonDocument
            .Parse(await after.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.DoesNotContain(
            afterEntries.EnumerateArray(),
            e => e.GetProperty("id").GetInt64() == fixture.EntryId);

        // На дату ДО закриття запис ще чинний — та сама дата періоду, інший
        // результат: доводить, що фільтр справді дивиться на asOf, а не просто
        // пропускає перевірку.
        var before = await client
            .GetAsync(new Uri(
                $"/api/v1/registries/{fixture.Code}/entries?asOf=2024-06-01", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(before.IsSuccessStatusCode, $"{before.StatusCode}: {app.ErrorsText}");

        var beforeEntries = JsonDocument
            .Parse(await before.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Contains(
            beforeEntries.EnumerateArray(),
            e => e.GetProperty("id").GetInt64() == fixture.EntryId);
    }

    /// <summary>Один довідник (темпоральний чи ні) з одним записом.</summary>
    private async Task<RegistryFixture> SeedRegistryAsync(bool isTemporal)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = new EcrDbContext(Options());

        var definition = new RegistryDef(
            EcrCode.Create($"AOF{tag}"), Name($"AsOf {tag}"), isTemporal);

        db.RegistryDefs.Add(definition);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entry = new RegistryEntry(definition.Id, EcrCode.Create($"E{tag}"), Name($"Entry {tag}"));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new RegistryFixture(definition.Id, definition.Code, entry.Id);
    }

    /// <summary>Закриває запис датою — той самий шлях, що `SetEntryValidityHandler`.</summary>
    private async Task CloseEntryValidityAsync(long entryId, DateOnly until)
    {
        await using var db = new EcrDbContext(Options());

        var entry = await db.RegistryEntries.SingleAsync(e => e.Id == entryId).ConfigureAwait(false);
        entry.SetValidity(null, until);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і глобальним правом <c>Registry.View</c>.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regaof_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                Name("Registry asOf test"));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
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

    /// <summary>Довідник і один запис у ньому.</summary>
    private sealed record RegistryFixture(int DefinitionId, string Code, long EntryId);
}
