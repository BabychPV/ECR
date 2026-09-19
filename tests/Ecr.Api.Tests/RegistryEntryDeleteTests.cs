// tests/Ecr.Api.Tests/RegistryEntryDeleteTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
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
/// <c>DELETE /api/v1/registries/{code}/entries/{id}</c> — маршрут до наявного
/// обробника (`ФВ-8.6`, директива №15 `BE-01`).
/// </summary>
/// <remarks>
/// ⛔ Обробник <c>DeleteRegistryEntryHandler</c> існував і був зареєстрований у
/// контейнері, але жоден контролер його не кликав: видалити запис довідника з
/// інтерфейсу було неможливо. Тобто код, який перевіряє право, рахує посилання
/// й піднімає ревізію даних, не виконувався НІКОЛИ.
///
/// ⚠ Головний із чотирьох тестів — про чужий довідник. Ідентифікатор запису
/// наскрізний по всіх довідниках, тож без звірки <c>code</c> зі шляху запит
/// <c>DELETE /registries/A/entries/{id запису B}</c> видалив би запис довідника
/// B — мовчки й успішно, «бо id збігся». Саме цю умову прибирає мутація.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryDeleteTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Delete-2026!";

    /// <summary>Дата, на яку читається перелік записів; довідники темпоральні.</summary>
    private const string AsOf = "2026-01-15";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_запису_без_посилань_дає_204_і_прибирає_його_з_переліку()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData")
            .ConfigureAwait(true);

        var fixture = await SeedRegistriesAsync().ConfigureAwait(true);
        var before = await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true);

        var response = await client
            .DeleteAsync(new Uri(
                $"/api/v1/registries/{fixture.Code}/entries/{fixture.EntryId}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // ⚠ Перевіряється саме ПОВТОРНЕ ЧИТАННЯ тим самим маршрутом, яким
        // користується екран, а не рядок у таблиці: «видалено» має означати
        // «зник із того, що бачить людина».
        var listing = await client
            .GetAsync(new Uri(
                $"/api/v1/registries/{fixture.Code}/entries?asOf={AsOf}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(listing.IsSuccessStatusCode, $"{listing.StatusCode}: {app.ErrorsText}");

        var entries = JsonDocument
            .Parse(await listing.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.DoesNotContain(
            entries.EnumerateArray(),
            e => e.GetProperty("id").GetInt64() == fixture.EntryId);

        // ⛔ Ревізія даних — не косметика: за нею кешується перелік записів
        // (`GetRegistryEntriesHandler`). Без інкременту видалений запис
        // повертався б із кеша, і перевірка вище була б зеленою лише тому,
        // що фабрика підіймає свіжий процес.
        var after = await DataRevisionAsync(fixture.DefinitionId).ConfigureAwait(true);
        Assert.True(after > before, $"DataRevision не зросла: було {before}, стало {after}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_на_який_посилається_комірка_не_видаляється_а_дає_409_з_кількістю()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData")
            .ConfigureAwait(true);

        var fixture = await SeedRegistriesAsync().ConfigureAwait(true);
        await ReferenceFromCellAsync(fixture.EntryId).ConfigureAwait(true);

        var response = await client
            .DeleteAsync(new Uri(
                $"/api/v1/registries/{fixture.Code}/entries/{fixture.EntryId}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal("ECR-REG-0409", problem.GetProperty("errorCode").GetString());

        // ⛔ Саме КІЛЬКІСТЬ, а не факт відмови. Клієнт пропонує закрити запис
        // датою замість повтору, і «на запис посилаються N комірок» — єдине,
        // що робить цю пропозицію зрозумілою.
        Assert.True(
            problem.TryGetProperty("references", out var references),
            "У відмові немає `references`: клієнт не має що показати замість «повторити».");

        Assert.True(references.GetInt32() >= 1, $"references = {references.GetInt32()}.");

        // Запис лишився в обігу: відмова не має бути частковою.
        Assert.False(await IsDeletedAsync(fixture.EntryId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_чужого_довідника_не_видаляється_а_дає_404()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ цього набору. Прибрати звірку `definition.Code`
        // в `DeleteRegistryEntryHandler` — і цей тест єдиний із чотирьох стає
        // червоним: сервер віддає `204`, а запис ЧУЖОГО довідника зникає.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditData")
            .ConfigureAwait(true);

        var fixture = await SeedRegistriesAsync().ConfigureAwait(true);

        var response = await client
            .DeleteAsync(new Uri(
                $"/api/v1/registries/{fixture.OtherCode}/entries/{fixture.EntryId}",
                UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal("ECR-REG-0404", problem.GetProperty("errorCode").GetString());

        // ⚠ І головне: запис лишився цілим. Без цього твердження тест був би
        // зеленим навіть тоді, коли обробник спершу видаляє, а потім згадує
        // перевірити довідник.
        Assert.False(await IsDeletedAsync(fixture.EntryId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Registry_EditData_видалення_дає_403()
    {
        // ⚠ Користувач із правом ЧИТАННЯ довідників: інакше тест доводив би
        // лише те, що маршрут закритий для геть безправного, і не розрізняв би
        // `Registry.View` та `Registry.EditData`.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View").ConfigureAwait(true);

        var fixture = await SeedRegistriesAsync().ConfigureAwait(true);

        var response = await client
            .DeleteAsync(new Uri(
                $"/api/v1/registries/{fixture.Code}/entries/{fixture.EntryId}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await IsDeletedAsync(fixture.EntryId).ConfigureAwait(true));
    }

    /// <summary>Два довідники з одним записом у кожному.</summary>
    /// <remarks>
    /// Другий потрібен рівно для одного твердження — «запис чужого довідника».
    /// Без нього перевірку належності довелося б імітувати неіснуючим кодом, а
    /// це інший сценарій: там немає ні довідника, ні запису.
    /// </remarks>
    private async Task<RegistryFixture> SeedRegistriesAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = new EcrDbContext(Options());

        var mine = new RegistryDef(
            EcrCode.Create($"REGD{tag}"), Name($"Registry {tag}"), isTemporal: false);

        var other = new RegistryDef(
            EcrCode.Create($"REGO{tag}"), Name($"Other {tag}"), isTemporal: false);

        db.RegistryDefs.Add(mine);
        db.RegistryDefs.Add(other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entry = new RegistryEntry(mine.Id, EcrCode.Create($"E{tag}"), Name($"Entry {tag}"));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new RegistryFixture(mine.Id, mine.Code, other.Code, entry.Id);
    }

    /// <summary>Робить запис ужитим: комірка документа посилається на нього.</summary>
    private async Task ReferenceFromCellAsync(long registryEntryId)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync()
            .ConfigureAwait(false);

        await using var db = new EcrDbContext(Options());

        db.CellValues.Add(new CellValue(
            new CellAddress(document.PeriodKey, document.RowIds[0], document.ColumnDefIds[0]),
            document.TableDefId,
            new CellValueData { ValueRegistryEntryId = registryEntryId }));

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Чи позначений запис видаленим у базі.</summary>
    private async Task<bool> IsDeletedAsync(long registryEntryId)
    {
        await using var db = new EcrDbContext(Options());

        // ⚠ Читається саме прапорець, а не «чи є рядок»: видалення тут
        // логічне, і зниклий рядок означав би зовсім інший дефект.
        return await db.RegistryEntries
            .AsNoTracking()
            .Where(e => e.Id == registryEntryId)
            .Select(e => e.IsDeleted)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Поточна ревізія даних довідника.</summary>
    private async Task<int> DataRevisionAsync(int registryDefId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.RegistryDefs
            .AsNoTracking()
            .Where(d => d.Id == registryDefId)
            .Select(d => d.DataRevision)
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і заданими правами.</summary>
    /// <param name="app">Фабрика застосунку.</param>
    /// <param name="permissions">Права, які треба видати.</param>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"regdel_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (permissions.Length > 0)
            {
                var role = new Role(
                    EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    Name("Registry delete test"));

                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>Два довідники й один запис у першому з них.</summary>
    /// <param name="DefinitionId">Довідник, якому належить запис.</param>
    /// <param name="Code">Його код — той, що йде у шлях запиту.</param>
    /// <param name="OtherCode">Код СУСІДНЬОГО довідника; записів у ньому немає.</param>
    /// <param name="EntryId">Запис.</param>
    private sealed record RegistryFixture(
        int DefinitionId, string Code, string OtherCode, long EntryId);
}
