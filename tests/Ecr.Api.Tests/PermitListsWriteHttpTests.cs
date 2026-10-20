// tests/Ecr.Api.Tests/PermitListsWriteHttpTests.cs
using System.Net;
using System.Net.Http.Json;
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
