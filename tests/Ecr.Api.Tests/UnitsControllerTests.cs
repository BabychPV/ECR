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
    [Trait("Requirement", "D-30")]
    public async Task Множник_їде_рядком_і_доносить_шістнадцятий_знак_після_коми()
    {
        // ⛔ Справжній HTTP, а не серіалізатор у пам'яті: між `decimal` і
        // клієнтом лежать ДВА незалежні набори опцій (MVC і мінімальні API),
        // і розійтися вони можуть непомітно. Значення з 16 знаками після коми
        // у JSON-ЧИСЛІ клієнт прочитав би як 1.2345678901234568 — саме цього
        // й вимагає уникнути контракт (`docs/build/02-contracts.md` §10).
        //
        // ⚠ `uom.Unit.FactorToBase` — `decimal(38,18)`, тобто всі 16 знаків
        // переживають і запис у базу, і читання з неї.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);

        const string factor = "1.2345678901234567";
        var code = $"p{Guid.NewGuid():N}"[..8];

        // Множник надсилається РЯДКОМ, зсув — ЧИСЛОМ: читання мусить приймати
        // обидві форми, інакше формат відповіді ламає наявних клієнтів запису.
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/units", UriKind.Relative),
            new
            {
                code,
                symbolL10n = new Dictionary<string, string> { ["en"] = code },
                nameL10n = new Dictionary<string, string> { ["en"] = code },
                dimensionId = 1,
                factorToBase = factor,
                offsetToBase = 0,
            }).ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("factorToBase");

        Assert.Equal(JsonValueKind.String, created.ValueKind);
        Assert.Equal(factor, created.GetString());

        // Другий прохід — уже з бази, іншим обробником: значення не зрізалося
        // ні на записі, ні на читанні.
        var listed = (await ReadAsync(client, "/api/v1/units").ConfigureAwait(true))
            .EnumerateArray()
            .Single(u => string.Equals(u.GetProperty("code").GetString(), code, StringComparison.Ordinal))
            .GetProperty("factorToBase");

        Assert.Equal(JsonValueKind.String, listed.ValueKind);

        // ⚠ Хвостові нулі — це МАСШТАБ колонки (`decimal(38,18)`), а не втрата:
        // `decimal` носить масштаб у собі, і `ToString` його друкує. Так само
        // робив і попередній формат — `System.Text.Json` писав JSON-число
        // `1.234567890123456700`; просто `JSON.parse` їх прибирав, а рядок —
        // ні. Нормалізує подання клієнт (`shared/format/number.ts`), сервер
        // не вигадує за нього, скільки знаків значущі.
        Assert.Equal(factor, listed.GetString()!.TrimEnd('0'));
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

        await AssertProblemAsync(response, 422, "ECR-UOM-0422", "err.ECR-UOM-0422.factorMustBePositive")
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Одиниця_з_посиланням_не_видаляється_409_а_без_посилань_204()
    {
        // Директива №15, BE-15 — крізь справжній HTTP і справжню базу: перелік
        // посилань збирає SQL, і лише тут видно, що він перекладається.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);

        var used = await CreateAsync(client).ConfigureAwait(true);
        var other = await CreateAsync(client).ConfigureAwait(true);
        var free = await CreateAsync(client).ConfigureAwait(true);

        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using (var db = new EcrDbContext(options))
        {
            db.UnitConversions.Add(new Ecr.Domain.Entities.Units.UnitConversion(
                used.Id, other.Id, factor: 2m, offset: 0m, kind: 0, note: null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var usage = await ReadAsync(client, $"/api/v1/units/{used.Id}/usage").ConfigureAwait(true);
        Assert.Equal(1, usage.GetProperty("total").GetInt32());

        var item = Assert.Single(usage.GetProperty("items").EnumerateArray());
        Assert.Equal("unitConversion", item.GetProperty("kind").GetString());
        Assert.Equal($"{used.Code} -> {other.Code}", item.GetProperty("label").GetString());

        var refused = await client.DeleteAsync(new Uri($"/api/v1/units/{used.Id}", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);

        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-UOM-0409", problem.GetProperty("errorCode").GetString());

        // ⚠ `details.references` — те, що діалог показує замість «повторити».
        // Деталі винятку їдуть розширеннями problem+json, тобто на верхньому рівні.
        var reference = Assert.Single(problem.GetProperty("references").EnumerateArray());
        Assert.Equal("unitConversion", reference.GetProperty("kind").GetString());

        var empty = await ReadAsync(client, $"/api/v1/units/{free.Id}/usage").ConfigureAwait(true);
        Assert.Equal(0, empty.GetProperty("total").GetInt32());

        var removed = await client.DeleteAsync(new Uri($"/api/v1/units/{free.Id}", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, removed.StatusCode);

        // ⛔ Головне: відхилена одиниця ЛИШИЛАСЬ, видалена — зникла.
        var codes = (await ReadAsync(client, "/api/v1/units").ConfigureAwait(true))
            .EnumerateArray().Select(u => u.GetProperty("code").GetString()).ToList();

        Assert.Contains(used.Code, codes);
        Assert.DoesNotContain(free.Code, codes);

        // Базова одиниця розмірності (`kg`) тримається самою розмірністю.
        var kg = (await ReadAsync(client, "/api/v1/units").ConfigureAwait(true))
            .EnumerateArray().Single(u => u.GetProperty("code").GetString() == "kg").GetProperty("id").GetInt32();
        var kgRefused = await client.DeleteAsync(new Uri($"/api/v1/units/{kg}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, kgRefused.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_одиниці_потребує_чинної_версії_застаріла_дає_409_і_рядок_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);

        var (id, code) = await CreateAsync(client).ConfigureAwait(true);
        var read = await ReadAsync(client, $"/api/v1/units/{id}").ConfigureAwait(true);
        var version = read.GetProperty("rowVersion").GetString()!;

        var noHeader = await PutAsync(client, id, null, "Pound", 0.45m).ConfigureAwait(true);
        await AssertProblemAsync(noHeader, 422, "ECR-REQ-0422", "err.ECR-REQ-0422.unitIfMatch").ConfigureAwait(true);

        // Одиниця без посилань: множник змінюється разом із назвою.
        var saved = await PutAsync(client, id, version, "Pound", 0.45m).ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);
        var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("Pound", body.GetProperty("nameL10n").GetProperty("en").GetString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        var fresh = body.GetProperty("rowVersion").GetString()!;
        Assert.NotEqual(version, fresh);

        // ⛔ Версія з відповіді PUT мусить збігатися з версією, прочитаною з бази
        // (множник там у масштабі колонки), інакше наступна правка — хибний 409.
        var reread = await ReadAsync(client, $"/api/v1/units/{id}").ConfigureAwait(true);
        Assert.Equal(fresh, reread.GetProperty("rowVersion").GetString());

        var stale = await PutAsync(client, id, version, "Stale", 0.45m).ConfigureAwait(true);
        var problem = await AssertProblemAsync(stale, 409, "ECR-UOM-0409", "err.ECR-UOM-0409.unitChanged")
            .ConfigureAwait(true);
        Assert.Equal(fresh, problem.GetProperty("rowVersion").GetString());

        var after = await ReadAsync(client, $"/api/v1/units/{id}").ConfigureAwait(true);
        Assert.Equal("Pound", after.GetProperty("nameL10n").GetProperty("en").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Множник_одиниці_з_посиланнями_не_змінюється_409_а_назва_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);

        var used = await CreateAsync(client).ConfigureAwait(true);
        var other = await CreateAsync(client).ConfigureAwait(true);

        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using (var db = new EcrDbContext(options))
        {
            db.UnitConversions.Add(new Ecr.Domain.Entities.Units.UnitConversion(
                used.Id, other.Id, factor: 2m, offset: 0m, kind: 0, note: null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var version = (await ReadAsync(client, $"/api/v1/units/{used.Id}").ConfigureAwait(true))
            .GetProperty("rowVersion").GetString()!;

        var refused = await PutAsync(client, used.Id, version, "Renamed", 3m).ConfigureAwait(true);
        var problem = await AssertProblemAsync(refused, 409, "ECR-UOM-0409", "err.ECR-UOM-0409.unitFactorInUse")
            .ConfigureAwait(true);
        Assert.Equal("1", problem.GetProperty("total").GetString());

        // Той самий множник (2.5, у базі — 2.500000000000000000) — не зміна.
        var renamed = await PutAsync(client, used.Id, version, "Renamed", 2.5m).ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.OK, renamed.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_одиниці_без_права_403_неіснуючої_404_невалідне_тіло_422()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SignedInAsync(app, "Uom.EditCatalog").ConfigureAwait(true);
        using var reader = await SignedInAsync(app).ConfigureAwait(true);

        var (id, _) = await CreateAsync(editor).ConfigureAwait(true);
        var version = (await ReadAsync(editor, $"/api/v1/units/{id}").ConfigureAwait(true))
            .GetProperty("rowVersion").GetString()!;

        var denied = await PutAsync(reader, id, version, "Nope", 2.5m).ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);

        var missing = await PutAsync(editor, int.MaxValue, version, "Nope", 2.5m).ConfigureAwait(true);
        await AssertProblemAsync(missing, 404, "ECR-UOM-0404", "err.ECR-UOM-0404.unitId").ConfigureAwait(true);

        var blank = await PutAsync(editor, id, version, "  ", 2.5m).ConfigureAwait(true);
        await AssertProblemAsync(blank, 422, "ECR-REQ-0422", "err.ECR-REQ-0422.unitInvalid").ConfigureAwait(true);

        var zero = await PutAsync(editor, id, version, "Zero", 0m).ConfigureAwait(true);
        var zeroProblem = await AssertProblemAsync(zero, 422, "ECR-UOM-0422", "err.ECR-UOM-0422.factorMustBePositive")
            .ConfigureAwait(true);

        // Заголовок коду спільний із «різними розмірностями»: про розмірності
        // над відмовою множника він говорити не може.
        var title = zeroProblem.GetProperty("title").GetString();
        Assert.Equal("Invalid unit conversion", title);
        Assert.DoesNotContain("dimension", title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("greater than zero", zeroProblem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Жодна з відмов не змінила рядка: версія та сама.
        var after = await ReadAsync(editor, $"/api/v1/units/{id}").ConfigureAwait(true);
        Assert.Equal(version, after.GetProperty("rowVersion").GetString());
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, int id, string? ifMatch, string name, decimal factor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/units/{id}", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                symbolL10n = new Dictionary<string, string> { ["en"] = "x" },
                nameL10n = new Dictionary<string, string> { ["en"] = name },
                factorToBase = factor,
                offsetToBase = 0m,
            }),
        };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response, int status, string errorCode, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True((int)response.StatusCode == status, $"{response.StatusCode}: {body}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());

        return problem;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<(int Id, string Code)> CreateAsync(HttpClient client)
    {
        var code = $"d{Guid.NewGuid():N}"[..8];

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
            }).ConfigureAwait(false);

        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

        return (created.GetProperty("id").GetInt32(), code);
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
