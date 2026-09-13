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

    /// <summary>Клієнт із чинним сеансом локального користувача.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
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
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
