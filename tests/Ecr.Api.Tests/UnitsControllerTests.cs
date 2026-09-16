// tests/Ecr.Api.Tests/UnitsControllerTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/units</c> має віддавати розмірність у формі, придатній для
/// показу людині, а не лише внутрішній числовий <c>DimensionId</c>.
/// </summary>
/// <remarks>
/// ⛔ Q-297: `UnitDto` (`Ecr.Application.Units.Dto`) документував
/// <c>DimensionCode</c> як обов'язкове поле відповіді, але жоден
/// обробник/контролер його не повертав — відповідь несла лише голий
/// <c>dimensionId: number</c>, і клієнт (`UnitsPage.tsx`) показував це число
/// напряму користувачу. Тест ловить саме це: <c>dimensionCode</c> у JSON
/// відповіді має бути РЯДКОМ із довідника <c>uom.Dimension.Code</c>
/// (`kg` → `Mass`), а не відсутнім чи порожнім.
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitsControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Units-Probe-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_одиниць_несе_код_розмірності_а_не_лише_її_ідентифікатор()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var response = await client.GetAsync(new Uri("/api/v1/units", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var units = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        // Насінна одиниця `kg` — базова одиниця розмірності `Mass` (`09-seed.sql`).
        var kilogram = units.EnumerateArray()
            .Single(u => string.Equals(u.GetProperty("code").GetString(), "kg", StringComparison.Ordinal));

        // ⛔ Головне твердження. До фіксу поля `dimensionCode` в JSON не було
        // взагалі — клієнт бачив лише `dimensionId: 1` і не міг показати,
        // ЩО це за розмірність.
        Assert.True(
            kilogram.TryGetProperty("dimensionCode", out var dimensionCode),
            "Відповідь /api/v1/units не містить dimensionCode: клієнт і далі бачить лише голий dimensionId.");

        Assert.Equal("Mass", dimensionCode.GetString());

        // Друга одиниця тієї самої розмірності (`t`) має ТОЙ САМИЙ код —
        // це саме код розмірності, а не якийсь текст, унікальний для одиниці.
        var tonne = units.EnumerateArray()
            .Single(u => string.Equals(u.GetProperty("code").GetString(), "t", StringComparison.Ordinal));

        Assert.Equal("Mass", tonne.GetProperty("dimensionCode").GetString());

        // Інша розмірність (`m3`, Volume) має інший код — перевіряє, що
        // значення справді читається з довідника, а не є однією константою.
        var cubicMetre = units.EnumerateArray()
            .Single(u => string.Equals(u.GetProperty("code").GetString(), "m3", StringComparison.Ordinal));

        Assert.Equal("Volume", cubicMetre.GetProperty("dimensionCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_одиниці_віддає_201_Created_а_не_200_OK()
    {
        // ⚠ Аудит 2026-09-16, §9. Усі решта створювальних маршрутів API
        // (`RegistriesController`, `ProjectsController`, `SecurityController`,
        // `DocumentsController`) віддають `201`; один маршрут, що відповідає
        // інакше, змушує клієнта тримати виняток саме на нього. Знімок
        // контракту оновлено тим самим комітом.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);

        var code = $"u{Guid.NewGuid():N}"[..8];

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/units", UriKind.Relative),
            new
            {
                code,
                symbolL10n = new Dictionary<string, string> { ["en"] = code },
                nameL10n = new Dictionary<string, string> { ["en"] = code },
                dimensionId = 1,
                factorToBase = 2.5m,
                offsetToBase = 0m,
            }).ConfigureAwait(true);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        // Тіло відповіді лишається тим самим — це зміна СТАТУСУ, не контракту
        // даних, і клієнт (`features/units/api.ts`) читає його так само.
        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal(code, created.GetProperty("code").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Недодатний_множник_у_запиті_відхиляється_422_а_не_зберігається()
    {
        // ⛔ Аудит §5.2 через справжній HTTP: порожнє числове поле форми
        // приходить нулем, а одиниця з `factorToBase = 0` згортає кожну
        // конверсію до константи.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);

        var code = $"z{Guid.NewGuid():N}"[..8];

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/units", UriKind.Relative),
            new
            {
                code,
                symbolL10n = new Dictionary<string, string> { ["en"] = code },
                nameL10n = new Dictionary<string, string> { ["en"] = code },
                dimensionId = 1,
                factorToBase = 0m,
                offsetToBase = 0m,
            }).ConfigureAwait(true);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("ECR-UOM-0422", body, StringComparison.Ordinal);
    }

    /// <summary>Клієнт із чинним сеансом локального користувача.</summary>
    /// <param name="app">Фабрика застосунку.</param>
    /// <param name="permissions">
    /// Функціональні права, які треба видати. Порожньо — користувач без прав:
    /// перелік одиниць їх не вимагає, заведення — вимагає <c>Uom.EditCatalog</c>.
    /// </param>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"units_{Guid.NewGuid():N}"[..20];

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

        await using (var db = new EcrDbContext(options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (permissions.Length > 0)
            {
                var role = new Role(
                    Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new Ecr.Domain.ValueObjects.LocalizedText(
                        new Dictionary<string, string> { ["en"] = "Units test" }));
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
}
