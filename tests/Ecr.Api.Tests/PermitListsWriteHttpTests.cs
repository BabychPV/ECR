// tests/Ecr.Api.Tests/PermitListsWriteHttpTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// Запис значень списків вибору дозволів (Permits, PM-08e): <c>PUT /permit-lists/{key}/items</c> - матриця прав 403/404,
/// спільний довідник (через обробники «Довідників») і «лише для типу» (прихований реєстр + чернетка типу), усе або нічого.
/// </summary>
/// <remarks>
/// Мутаційні докази (виконані): (1) у <c>ChangePermitListItemsHandler</c> прибрати <c>RegistryAccess.RequireAsync(...EditSharedPermission...)</c> -
/// червоний «Права…» (читач із порожнім тілом дістає 422 замість 403); (2) прибрати виклик <c>PatchDraftAsync</c> - червоний
/// «Значення_лише_для_типу_лягає_у_відкриту_чернетку…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitListsWriteHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Права_читач_не_змінює_редактор_типів_не_чіпає_спільний_а_невідомий_ключ_дає_404()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLR_{_tag}");
        await PublishTypeAsync(admin, $"PLR{_tag}", shared, ["A", "B"]);
        var sharedKey = $"shared:{shared}";
        var typeKey = $"type:PLR{_tag}:Rows:Kind";
        var add = Changes(new { action = "Add", displayL10n = new { en = "New" } });

        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, sharedKey, add)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, typeKey, add)).StatusCode);

        // Право перевіряється ДО розбору змін: читач із порожнім (недійсним) тілом отримує 403, а не 422.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, sharedKey, Changes())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, typeKey, Changes())).StatusCode);

        // Редактор типів (без Registry.EditData) пише «лише для типу», але не спільний довідник.
        using var typeEditor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View", "Permit.EditType");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(typeEditor, sharedKey, add)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(typeEditor, typeKey, add)).StatusCode);

        // Редактор даних довідників (без Permit.EditType) пише спільний довідник, але не список «лише для типу».
        using var dataEditor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View", "Registry.EditData");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(dataEditor, typeKey, Changes(new { action = "Add", displayL10n = new { en = "Other" } }))).StatusCode);

        await AssertErrorAsync(await PutAsync(admin, "type:NOPE:Rows:Kind", add), HttpStatusCode.NotFound, "ECR-PRM-0404");
        await AssertErrorAsync(await PutAsync(admin, "shared:NOPE" + _tag, add), HttpStatusCode.NotFound, "ECR-PRM-0404");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Спільний_довідник_додати_змінити_видалити_значення_через_ті_самі_обробники_довідників()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLE_{_tag}");
        await PublishTypeAsync(client, $"PLE{_tag}", shared, ["A"]);
        var key = $"shared:{shared}";

        var added = await PutAsync(client, key, Changes(
            new { action = "Add", displayL10n = new { en = "Alpha", ru = "Альфа" } },
            new { action = "Add", displayL10n = new { en = "Beta" } }));
        Assert.True(added.StatusCode == HttpStatusCode.OK, $"{added.StatusCode}: {await added.Content.ReadAsStringAsync()}");
        var detail = await Json(added);
        Assert.Equal(3, detail.GetProperty("list").GetProperty("itemCount").GetInt32());
        var alpha = detail.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("displayL10n").GetProperty("en").GetString() == "Alpha");
        var alphaId = alpha.GetProperty("id").GetInt64();
        Assert.Equal("Альфа", alpha.GetProperty("displayL10n").GetProperty("ru").GetString());

        // Зміна: назва зливається (ru лишається), код не міняється.
        var renamed = await Json(await PutAsync(client, key, Changes(new { action = "Update", id = alphaId, displayL10n = new { en = "Alpha 2" } })));
        var item = renamed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == alphaId);
        Assert.Equal(
            ("Alpha 2", "Альфа", alpha.GetProperty("code").GetString()),
            (item.GetProperty("displayL10n").GetProperty("en").GetString(), item.GetProperty("displayL10n").GetProperty("ru").GetString(), item.GetProperty("code").GetString()));

        // Видалення; запис ІНШОГО довідника за чужим id - 404 і нічого не видаляє.
        var removed = await Json(await PutAsync(client, key, Changes(new { action = "Remove", id = alphaId })));
        Assert.DoesNotContain(removed.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt64() == alphaId);
        var foreign = await ForeignEntryIdAsync();
        await AssertErrorAsync(await PutAsync(client, key, Changes(new { action = "Remove", id = foreign })), HttpStatusCode.NotFound, "ECR-REG-0404");
        await using var db = sql.CreateContext();
        Assert.False((await db.RegistryEntries.AsNoTracking().SingleAsync(e => e.Id == foreign)).IsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Значення_лише_для_типу_додаються_змінюються_вимикаються_а_останнє_й_дубль_відхиляються()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLT_{_tag}");
        await PublishTypeAsync(client, $"PLT{_tag}", shared, ["One", "Two"]);
        var key = $"type:PLT{_tag}:Rows:Kind";

        var added = await Json(await PutAsync(client, key, Changes(new { action = "Add", displayL10n = new { en = "Three" } })));
        var ids = added.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("displayL10n").GetProperty("en").GetString()!, i => i.GetProperty("id").GetInt64());
        Assert.Equal(["One", "Two", "Three"], Labels(added));

        // Дубль (без урахування регістру) і порожнє значення - 422 ECR-PRM-0422, нічого не збережено (усе або нічого).
        var duplicate = await PutAsync(client, key, Changes(
            new { action = "Add", displayL10n = new { en = "Four" } }, new { action = "Add", displayL10n = new { en = "three" } }));
        await AssertErrorAsync(duplicate, HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");
        await AssertErrorAsync(
            await PutAsync(client, key, Changes(new { action = "Add", displayL10n = new { ru = "Нет en" } })), HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");
        Assert.Equal(["One", "Two", "Three"], Labels(await Json(await GetAsync(client, key))));

        // Перейменування і вимкнення; останнє значення не прибирається.
        await PutAsync(client, key, Changes(new { action = "Update", id = ids["One"], displayL10n = new { en = "Uno" } }));
        var afterRemove = await Json(await PutAsync(client, key, Changes(new { action = "Remove", id = ids["Two"] }, new { action = "Remove", id = ids["Three"] })));
        Assert.Equal(["Uno"], Labels(afterRemove));
        await AssertErrorAsync(
            await PutAsync(client, key, Changes(new { action = "Remove", id = ids["One"] })), HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");

        // Невідома дія і порожній перелік змін - 422.
        await AssertErrorAsync(await PutAsync(client, key, Changes(new { action = "Rename", id = ids["One"] })), HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");
        await AssertErrorAsync(await PutAsync(client, key, Changes()), HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Значення_лише_для_типу_лягає_у_відкриту_чернетку_нової_версії_і_переживає_публікацію()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLD_{_tag}");
        var type = $"PLD{_tag}";
        await PublishTypeAsync(client, type, shared, ["A", "B"]);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(new Uri($"/api/v1/permit-types/{type}/new-version", UriKind.Relative), null)).StatusCode);

        var key = $"type:{type}:Rows:Kind";
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, key, Changes(new { action = "Add", displayL10n = new { en = "C" } }))).StatusCode);

        // Чернетка несе нове значення: без цього публікація вимкнула б його (SyncChoices).
        var draft = await Json(await client.GetAsync(new Uri($"/api/v1/permit-types/{type}", UriKind.Relative)));
        var choices = draft.GetProperty("tables")[0].GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "Kind").GetProperty("choices").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(["A", "B", "C"], choices);

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{type}/publish", UriKind.Relative),
            new { reason = "нова версія зі значенням C", rowVersion = draft.GetProperty("rowVersion").GetString() });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {await published.Content.ReadAsStringAsync()}");
        Assert.Equal(["A", "B", "C"], Labels(await Json(await GetAsync(client, key))));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_того_самого_дозволу_не_приймає_значень_422()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLS2_{_tag}");
        await PublishTypeAsync(client, $"PLS{_tag}", shared, ["A"], withSibling: true);

        await AssertErrorAsync(
            await PutAsync(client, $"permit:PLS{_tag}:Rows:Place", Changes(new { action = "Add", displayL10n = new { en = "X" } })),
            HttpStatusCode.UnprocessableEntity, "ECR-PRM-0422");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_перегляду_наявний_і_неіснуючий_ключ_відповідають_однаково_403()
    {
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLO_{_tag}");
        await PublishTypeAsync(admin, $"PLO{_tag}", shared, ["A"]);
        var add = Changes(new { action = "Add", displayL10n = new { en = "New" } });

        // Оракул існування: без Registry.View 403 і для наявного, і для вигаданого ключа (не 403 проти 404).
        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app, "Document.View", "Permit.EditType", "Registry.EditData");
        var existing = await PutAsync(nobody, $"type:PLO{_tag}:Rows:Kind", add);
        var missing = await PutAsync(nobody, $"type:NOPE{_tag}:Rows:Kind", add);
        Assert.Equal(HttpStatusCode.Forbidden, existing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(
            JsonDocument.Parse(await existing.Content.ReadAsStringAsync()).RootElement.GetProperty("errorCode").GetString(),
            JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(nobody, $"shared:{shared}", add)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Англійський_підпис_значення_вже_вибраного_в_дозволі_не_змінюється_409_а_інші_мови_так()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLU_{_tag}");
        var type = $"PLU{_tag}";
        await PublishTypeAsync(client, type, shared, ["Used", "Free"]);
        var key = $"type:{type}:Rows:Kind";
        var ids = (await Json(await GetAsync(client, key))).GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("displayL10n").GetProperty("en").GetString()!, i => i.GetProperty("id").GetInt64());

        // Рядок дозволу вибирає значення «Used».
        var permit = await client.PostAsJsonAsync(
            new Uri("/api/v1/permits", UriKind.Relative),
            new { typeCode = type, number = $"PLU-{_tag}", location = "x", issueDate = "2026-01-05", validFrom = "2026-01-10", validTo = "2027-01-10", reportingFlags = new { form300 = false, form301 = false, form302 = false, tox = false } });
        Assert.Equal(HttpStatusCode.Created, permit.StatusCode);
        var permitId = (await Json(permit)).GetProperty("id").GetInt64();
        var batch = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/PRM_{type}_Rows/entries/batch?dryRun=false", UriKind.Relative),
            new
            {
                items = new[]
                {
                    new
                    {
                        clientRowId = "save", op = "upsert", id = (long?)null, code = (string?)null, baseVersion = (string?)null,
                        values = new Dictionary<string, string?> { ["PERMIT"] = permitId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Name"] = "Row", ["Kind"] = ids["Used"].ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    },
                },
            });
        var body = await Json(batch);
        Assert.True(batch.StatusCode == HttpStatusCode.OK && body.GetProperty("applied").GetBoolean(), body.ToString());

        // en змінити не можна (409 ECR-PRM-4094), нічого не змінилось; те саме en + нова ru - можна; вільне значення - можна.
        await AssertErrorAsync(
            await PutAsync(client, key, Changes(new { action = "Update", id = ids["Used"], displayL10n = new { en = "Renamed" } })),
            HttpStatusCode.Conflict, "ECR-PRM-4094");
        Assert.Contains("Used", Labels(await Json(await GetAsync(client, key))));
        var translated = await PutAsync(client, key, Changes(new { action = "Update", id = ids["Used"], displayL10n = new { en = "Used", ru = "Занято" } }));
        Assert.Equal(HttpStatusCode.OK, translated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(client, key, Changes(new { action = "Update", id = ids["Free"], displayL10n = new { en = "Spare" } }))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельні_додавання_у_список_лише_для_типу_не_губляться_і_не_дублюють_код_20_раундів()
        => await RaceAsync(withDraft: false);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельні_додавання_у_відкриту_чернетку_типу_не_губляться_20_раундів()
        => await RaceAsync(withDraft: true);

    /// <summary>
    /// Два паралельні Add у той самий список: кожна відповідь - 200 або 409 ECR-PRM-4093 (ніколи 500/дубль коду); програвший
    /// повторює й дістає 200; наприкінці всі значення на місці з РІЗНИМИ кодами (а за відкритої чернетки - і в її Choices).
    /// Мутаційний доказ: прибрати <c>type.MarkListChanged</c> - обидва беруть той самий код V### (500) чи губиться значення
    /// чернетки, тест червоніє.
    /// </summary>
    private async Task RaceAsync(bool withDraft)
    {
        using var app = new EcrApiFactory(sql);
        using var first = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        using var second = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLC{(withDraft ? "D" : "P")}_{_tag}");
        var type = $"PLC{(withDraft ? "D" : "P")}{_tag}";
        await PublishTypeAsync(first, type, shared, ["Seed"]);
        if (withDraft)
        {
            Assert.Equal(HttpStatusCode.OK, (await first.PostAsync(new Uri($"/api/v1/permit-types/{type}/new-version", UriKind.Relative), null)).StatusCode);
        }

        var key = $"type:{type}:Rows:Kind";
        var expected = new List<string> { "Seed" };
        for (var round = 0; round < 20; round++)
        {
            var names = new[] { $"A{round}", $"B{round}" };
            var results = await Task.WhenAll(
                PutAsync(first, key, Changes(new { action = "Add", displayL10n = new { en = names[0] } })),
                PutAsync(second, key, Changes(new { action = "Add", displayL10n = new { en = names[1] } })));

            for (var i = 0; i < 2; i++)
            {
                var status = results[i].StatusCode;
                var text = await results[i].Content.ReadAsStringAsync();
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"раунд {round}: {status}: {text}");
                if (status == HttpStatusCode.Conflict)
                {
                    Assert.Equal("ECR-PRM-4093", JsonDocument.Parse(text).RootElement.GetProperty("errorCode").GetString());
                    var retry = await PutAsync(i == 0 ? first : second, key, Changes(new { action = "Add", displayL10n = new { en = names[i] } }));
                    Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
                }
            }

            expected.AddRange(names);
        }

        var items = (await Json(await GetAsync(first, key))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(expected.Order(), items.Select(i => i.GetProperty("displayL10n").GetProperty("en").GetString()!).Order());
        Assert.Equal(items.Count, items.Select(i => i.GetProperty("code").GetString()).Distinct().Count());

        if (withDraft)
        {
            var draft = await Json(await first.GetAsync(new Uri($"/api/v1/permit-types/{type}", UriKind.Relative)));
            var choices = draft.GetProperty("tables")[0].GetProperty("columns").EnumerateArray()
                .Single(c => c.GetProperty("code").GetString() == "Kind").GetProperty("choices").EnumerateArray().Select(c => c.GetString()!).ToList();
            Assert.Equal(expected.Order(), choices.Order());
        }
    }

    private async Task<long> ForeignEntryIdAsync()
    {
        await using var db = sql.CreateContext();
        var other = new Ecr.Domain.Entities.Configuration.RegistryDef(
            Ecr.Domain.ValueObjects.EcrCode.Create($"PLF_{_tag}"), Text("Foreign"), isTemporal: false);
        db.RegistryDefs.Add(other);
        await db.SaveChangesAsync();
        var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(
            other.Id, Ecr.Domain.ValueObjects.EcrCode.Create("F1"), Text("Foreign 1"), 1, DateTime.UnixEpoch);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }
}
