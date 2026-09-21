// tests/Ecr.Api.Tests/RegistryDefinitionUsageTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// <c>GET /api/v1/registries/{code}/usage</c> — «де використано» ВИЗНАЧЕННЯ
/// довідника (директива №15, <c>BE-24</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього маршруту система вміла відповісти лише на питання про ОДИН
/// запис (<c>ECR-REG-0409</c> при видаленні). Що спирається на довідник
/// цілком — колонки шаблонів типу <c>Lookup</c>, поля сусідніх довідників —
/// не казало ніщо, тож перевипустити опис можна було наосліп.
///
/// ⚠ База спільна для всього набору, тому кожен тест заводить ВЛАСНИЙ довідник
/// із випадковим кодом: «рівно два посилання» інакше залежало б від того, що
/// насіяли сусіди.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryDefinitionUsageTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Usage-2026!";

    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Колонка_шаблону_і_поле_сусіднього_довідника_рахуються_обидві()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditDefinition")
            .ConfigureAwait(true);

        var fixture = await SeedAsync(withReferences: true).ConfigureAwait(true);

        var usage = await ReadUsageAsync(client, app, fixture.Code).ConfigureAwait(true);

        // ⛔ Число ЛІТЕРАЛОМ, а не «кількість того, що насіяли»: твердження
        // проти власної змінної рухалося б разом із помилкою в підрахунку.
        // Два — це рівно колонка шаблону і поле сусіднього довідника.
        Assert.Equal(2, usage.GetProperty("total").GetInt32());

        var items = usage.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var kinds = items.Select(i => i.GetProperty("kind").GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(["registryField", "templateColumn"], kinds);

        // ⚠ Підпис має називати те, чим об'єкт упізнає людина. Рід без підпису
        // («є одна колонка десь») не відповідає на питання, заради якого цей
        // маршрут і потрібен — ЩО саме доведеться правити.
        var column = items.Single(i => i.GetProperty("kind").GetString() == "templateColumn");
        Assert.Equal($"{fixture.TableCode}.{fixture.ColumnCode}", column.GetProperty("label").GetString());
        Assert.Equal(
            $"/admin/templates/{fixture.TemplateId}/versions/{fixture.TemplateVersionId}",
            column.GetProperty("route").GetString());

        var field = items.Single(i => i.GetProperty("kind").GetString() == "registryField");
        Assert.Equal($"{fixture.OtherCode}.{fixture.FieldCode}", field.GetProperty("label").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Довідник_на_який_ніхто_не_посилається_дає_чесний_нуль()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Registry.EditDefinition")
            .ConfigureAwait(true);

        var fixture = await SeedAsync(withReferences: false).ConfigureAwait(true);

        var usage = await ReadUsageAsync(client, app, fixture.Code).ConfigureAwait(true);

        // ⛔ Нуль — це відповідь, а не порожнеча. Саме на ній ґрунтується
        // рішення «довідник можна чіпати», тож вона перевіряється окремо:
        // підрахунок, який завжди щось знаходить, тут гірший за відсутній.
        Assert.Equal(0, usage.GetProperty("total").GetInt32());
        Assert.Empty(usage.GetProperty("items").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Без_права_Registry_EditDefinition_перелік_не_віддається()
    {
        // ⚠ Користувач має `Registry.View` — право на ЧИТАННЯ довідників.
        // Інакше тест доводив би лише те, що маршрут закритий для безправного,
        // і не розрізняв би читання довідника й підготовку до зміни його опису.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View").ConfigureAwait(true);

        var fixture = await SeedAsync(withReferences: false).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/registries/{fixture.Code}/usage", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-24")]
    public async Task Невідомий_код_дає_404_а_не_нуль_посилань()
    {
        // ⛔ Найдорожча з можливих помилок цього маршруту: друкарська помилка
        // в коді довідника віддала б «нуль посилань», тобто «чіпати безпечно».
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditDefinition").ConfigureAwait(true);

        var unknown = $"NOSUCH{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        var response = await client
            .GetAsync(new Uri($"/api/v1/registries/{unknown}/usage", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal("ECR-REG-0404", problem.GetProperty("errorCode").GetString());
    }

    /// <summary>Читає «де використано» і падає з текстом помилки сервера.</summary>
    private static async Task<JsonElement> ReadUsageAsync(
        HttpClient client, EcrApiFactory app, string code)
    {
        var response = await client
            .GetAsync(new Uri($"/api/v1/registries/{code}/usage", UriKind.Relative))
            .ConfigureAwait(false);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))
            .RootElement;
    }

    /// <summary>
    /// Довідник-предмет і — за потреби — двоє тих, хто на нього посилається.
    /// </summary>
    /// <param name="withReferences">
    /// <c>false</c> — довідник лишається самотнім: це і є сценарій «нуль».
    /// </param>
    private async Task<UsageFixture> SeedAsync(bool withReferences)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        await using var db = new EcrDbContext(Options());

        var subject = new RegistryDef(
            EcrCode.Create($"RUSE{tag}"), Name($"Subject {tag}"), isTemporal: false);

        db.RegistryDefs.Add(subject);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (!withReferences)
        {
            return new UsageFixture(subject.Code, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0);
        }

        // ── Колонка шаблону типу Lookup ──────────────────────────────────
        var template = new Template(
            EcrCode.Create($"TUSE{tag}"), Name($"Usage {tag}"), createdByUserId: 1, Now);
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

        var column = new ColumnDef(
            table.Id, EcrCode.Create($"CL{tag}"), Name("Column"), 1, CellDataType.Lookup);
        column.SetLookup(subject.Id);
        db.ColumnDefs.Add(column);

        // ── Поле СУСІДНЬОГО довідника, що вказує на предмет ──────────────
        var other = new RegistryDef(
            EcrCode.Create($"ROTH{tag}"), Name($"Other {tag}"), isTemporal: false);

        db.RegistryDefs.Add(other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var field = new RegistryFieldDef(
            other.Id, EcrCode.Create($"FL{tag}"), Name("Ref field"), CellDataType.Lookup, 1);
        field.PointTo(subject.Id);
        db.RegistryFieldDefs.Add(field);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new UsageFixture(
            subject.Code, other.Code, table.Code, column.Code, field.Code,
            template.Id, version.Id);
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
        var name = $"regusg_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry usage test"));

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

    /// <summary>Довідник-предмет і те, що на нього посилається.</summary>
    /// <param name="Code">Код довідника, який питаємо.</param>
    /// <param name="OtherCode">Код сусіднього довідника з полем-посиланням.</param>
    /// <param name="TableCode">Таблиця шаблону — перша половина підпису колонки.</param>
    /// <param name="ColumnCode">Колонка типу <c>Lookup</c>.</param>
    /// <param name="FieldCode">Поле сусіднього довідника.</param>
    /// <param name="TemplateId">Шаблон — для перевірки маршруту в переліку.</param>
    /// <param name="TemplateVersionId">Версія шаблону — друга частина маршруту.</param>
    private sealed record UsageFixture(
        string Code,
        string OtherCode,
        string TableCode,
        string ColumnCode,
        string FieldCode,
        int TemplateId,
        int TemplateVersionId);
}
