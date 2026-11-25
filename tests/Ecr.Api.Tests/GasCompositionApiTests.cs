// tests/Ecr.Api.Tests/GasCompositionApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Сторінка «Склад газу» (D-NEW, людина 24.11) через справжній HTTP-шлях: права, повний цикл потік → кейс → версії
/// (вставка обрізає, перенесення тягне сусіда, видалення продовжує попередню), Σ із підтвердженням, журнал, заборона
/// загальних редакторів довідників, імпорт лише в порожню базу.
/// </summary>
/// <remarks>
/// ⚠ База тестів спільна (<c>EcrTest_*</c>): довідники <c>STREAM</c>/<c>STREAM_CASE</c>/<c>GAS_COMPOSITION</c> можуть уже мати
/// записи інших прогонів — тести працюють лише зі своїм потоком (назва з GUID) і не рахують чужих.
/// <para>
/// Мутаційні докази (локально, не в коміті): (1) прибрати <c>PermissionCheck.RequireAsync(… Edit …)</c> у
/// <c>RequireEditAsync</c> → <c>Без_Edit_зміни_403_а_перегляд_200</c> червоний; (2) у <c>CreateVersionAsync</c> не
/// застосовувати <c>plan.Adjustments</c> → <c>Повний_цикл_версій_потоку</c> червоний (409 перетину ключа у вікні);
/// (3) прибрати гілку <c>IsGasCompositionRegistry</c> у <c>ExternalRegistryGuard</c> →
/// <c>Загальний_редактор_довідника_відмовляє_для_складу_газу</c> червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class GasCompositionApiTests(SqlServerFixture sql)
{
    private const string Password = "Gas-Composition-Api-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Без_Edit_зміни_403_а_перегляд_200()
    {
        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(app, ["GasComposition.View"]);

        using var list = await viewer.GetAsync(Url("streams"));
        Assert.True(list.StatusCode == HttpStatusCode.OK, $"{list.StatusCode}: {await list.Content.ReadAsStringAsync()} | {app.ErrorsText}");
        var root = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        Assert.False(root.GetProperty("canEdit").GetBoolean());
        Assert.False(root.GetProperty("canImport").GetBoolean());

        using var components = await viewer.GetAsync(Url("components"));
        Assert.Equal(HttpStatusCode.OK, components.StatusCode);

        using var create = await viewer.PostAsJsonAsync(Url("streams"), Stream("viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        using var import = await viewer.PostAsync(Url("import"), null);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);

        using var none = await SignedInAsync(app, ["Registry.View"]);
        using var denied = await none.GetAsync(Url("streams"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Повний_цикл_версій_потоку()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SignedInAsync(app, ["GasComposition.View", "GasComposition.Edit"]);

        long eventsBefore;
        await using (var db = Context())
        {
            eventsBefore = await db.Database.SqlQuery<long>($"SELECT CAST(ISNULL(MAX(Id), 0) AS bigint) AS [Value] FROM aud.SecurityEvent").SingleAsync();
        }

        // Потік: Id = найбільший + 1.
        var name = $"Test stream {Guid.NewGuid():N}"[..30];
        var created = await OkAsync(await editor.PostAsJsonAsync(Url("streams"), Stream(name)), app);
        var stream = created.GetProperty("stream");
        var streamEntryId = stream.GetProperty("id").GetInt64();
        Assert.True(stream.GetProperty("streamId").GetInt32() > 0);
        Assert.Equal(0, stream.GetProperty("cases").GetArrayLength());

        // Та сама назва й розташування — 409.
        using var duplicate = await editor.PostAsJsonAsync(Url("streams"), Stream(name));
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "err.ECR-REG-0409.gasCompositionStreamNameTaken");

        // Перша версія кейсу з 2026-01.
        var first = await OkAsync(await editor.PostAsJsonAsync(Url($"streams/{streamEntryId}/versions"),
            Version("370 Winter", "2026-01-01", ("Methane", 97m), ("Ethane", 3m))), app);
        var versions = Versions(first, "370 Winter");
        var v1 = Assert.Single(versions);
        Assert.Equal("2026-01-01", v1.GetProperty("validFrom").GetString());
        Assert.Equal(JsonValueKind.Null, v1.GetProperty("validTo").ValueKind);
        Assert.Equal(100m, Dec(v1.GetProperty("sum")));

        // Не з 1-го числа — 422; Σ поза допуском без підтвердження — 422.
        using var midMonth = await editor.PostAsJsonAsync(Url($"streams/{streamEntryId}/versions"),
            Version("370 Winter", "2026-03-15", ("Methane", 100m)));
        await AssertProblemAsync(midMonth, HttpStatusCode.UnprocessableEntity, "err.ECR-REG-0422.gasCompositionMonthStart");
        using var off = await editor.PostAsJsonAsync(Url($"streams/{streamEntryId}/versions"),
            Version("370 Winter", "2026-07-01", ("Methane", 90m)));
        await AssertProblemAsync(off, HttpStatusCode.UnprocessableEntity, "err.ECR-REG-0422.gasCompositionSumDeviation");

        // Друга версія з 2026-07 (Σ = 90 підтверджено): перша обрізана до 2026-07.
        var second = await OkAsync(await editor.PostAsJsonAsync(Url($"streams/{streamEntryId}/versions"),
            Version("370 Winter", "2026-07-01", accept: true, ("Methane", 90m))), app);
        versions = Versions(second, "370 Winter");
        Assert.Equal(2, versions.Count);
        Assert.Equal("2026-07-01", versions[0].GetProperty("validTo").GetString());
        var v2Id = versions[1].GetProperty("id").GetInt64();
        Assert.Equal(90m, Dec(versions[1].GetProperty("sum")));

        // Перенесення другої на 2026-05 зі зміною складу: перша тягнеться до 2026-05; Ethane прибрано (рядок вимкнено).
        var moved = await OkAsync(await editor.PutAsJsonAsync(Url($"versions/{v2Id}"),
            Update("2026-05-01", ("Methane", 99.8m), ("H2S", 0.2m))), app);
        versions = Versions(moved, "370 Winter");
        Assert.Equal("2026-05-01", versions[0].GetProperty("validTo").GetString());
        Assert.Equal("2026-05-01", versions[1].GetProperty("validFrom").GetString());
        var components = versions[1].GetProperty("components").EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => Dec(c.GetProperty("molPct")));
        Assert.Equal(2, components.Count);
        Assert.Equal(0.2m, components["H2S"]);

        // Версія з тією ж датою — 409.
        using var taken = await editor.PostAsJsonAsync(Url($"streams/{streamEntryId}/versions"),
            Version("370 Winter", "2026-05-01", ("Methane", 100m)));
        await AssertProblemAsync(taken, HttpStatusCode.Conflict, "err.ECR-REG-0409.gasCompositionDateTaken");

        // Перейменування кейсу — усіх версій.
        var renamed = await OkAsync(await editor.PutAsJsonAsync(Url($"streams/{streamEntryId}/cases"),
            new { from = "370 Winter", to = "370 Winter (rev 2)" }), app);
        Assert.Equal(2, Versions(renamed, "370 Winter (rev 2)").Count);

        // Видалення другої: перша знову відкрита.
        var deleted = await OkAsync(await editor.DeleteAsync(Url($"versions/{v2Id}")), app);
        var left = Assert.Single(Versions(deleted, "370 Winter (rev 2)"));
        Assert.Equal(JsonValueKind.Null, left.GetProperty("validTo").ValueKind);

        // Журнал: подія на кожну дію людини (потік, 2 версії, перенесення, перейменування, видалення версії = 6).
        // ⚠ Колекція "SqlServer" серіалізована — інших писачів журналу між двома замірами немає.
        await using (var db = Context())
        {
            var events = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = N'GasCompositionChanged' AND Id > {eventsBefore}")
                .SingleAsync();
            Assert.Equal(6, events);
        }

        // Імпорт у непорожню базу — 409.
        using var importer = await SignedInAsync(app, ["GasComposition.View", "GasComposition.Edit", "Integration.Manage"]);
        using var import = await importer.PostAsync(Url("import"), null);
        await AssertProblemAsync(import, HttpStatusCode.Conflict, "err.ECR-REG-0409.gasCompositionAlreadyImported");

        // Видалення потоку — зникає з переліку.
        var gone = await OkAsync(await editor.DeleteAsync(Url($"streams/{streamEntryId}")), app);
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("stream").ValueKind);
        var list = await OkAsync(await editor.GetAsync(Url("streams")), app);
        Assert.DoesNotContain(list.GetProperty("streams").EnumerateArray(), s => s.GetProperty("id").GetInt64() == streamEntryId);
        using var missing = await editor.GetAsync(Url($"streams/{streamEntryId}"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Загальний_редактор_довідника_відмовляє_для_складу_газу()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SignedInAsync(app, ["GasComposition.View", "GasComposition.Edit", "Registry.View", "Registry.EditData"]);
        var name = $"Guard stream {Guid.NewGuid():N}"[..30];
        var created = await OkAsync(await editor.PostAsJsonAsync(Url("streams"), Stream(name)), app);
        var streamEntryId = created.GetProperty("stream").GetProperty("id").GetInt64();

        using var delete = await editor.DeleteAsync(new Uri($"/api/v1/registries/STREAM/entries/{streamEntryId}", UriKind.Relative));
        await AssertProblemAsync(delete, HttpStatusCode.Conflict, "err.ECR-REG-0409.managedByGasComposition");

        await OkAsync(await editor.DeleteAsync(Url($"streams/{streamEntryId}")), app);
    }

    /// <summary>`decimal` на дроті — рядок (`DecimalAsStringJsonConverter`), повної точності.</summary>
    private static decimal Dec(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? decimal.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : value.GetDecimal();

    private static Uri Url(string path) => new($"/api/v1/gas-composition/{path}", UriKind.Relative);

    private static object Stream(string name)
        => new { name, number = "T-1", isOnshore = true, isSour = false, isHmb = false, hmbDoc = (string?)null, group = 1, hidden = false };

    private static object Version(string caseName, string from, params (string Code, decimal Value)[] components)
        => Version(caseName, from, accept: false, components);

    private static object Version(string caseName, string from, bool accept, params (string Code, decimal Value)[] components)
        => new
        {
            caseName,
            validFrom = from,
            temperatureC = 20m,
            densityOp = (decimal?)null,
            mwHysys = (decimal?)null,
            z = (decimal?)null,
            components = components.Select(c => new { code = c.Code, molPct = c.Value }),
            acceptSumDeviation = accept,
        };

    private static object Update(string from, params (string Code, decimal Value)[] components)
        => new
        {
            validFrom = from,
            temperatureC = 21m,
            densityOp = (decimal?)null,
            mwHysys = (decimal?)null,
            z = (decimal?)null,
            components = components.Select(c => new { code = c.Code, molPct = c.Value }),
            acceptSumDeviation = false,
        };

    private static List<JsonElement> Versions(JsonElement result, string caseName)
        => [.. result.GetProperty("stream").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == caseName)
            .GetProperty("versions").EnumerateArray()];

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text} | {app.ErrorsText}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string messageKey)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {text}");
        Assert.Contains(messageKey, text, StringComparison.Ordinal);
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"gasc_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Gas composition test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
