// tests/Ecr.Api.Tests/SourceEventsApiTests.RowVersion.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// AN-40 / L9-06: мапінг подій має версію рядка, і повна заміна зі застарілою версією — <c>409</c>, а не мовчазний
/// відкат чужої правки.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише локально, у гілку не пушились):
/// <list type="bullet">
/// <item>в <c>UpdateSourceEventMapHandler</c> прибрати звірку <c>command.RowVersion</c> — застаріла пауза дає 200 і
/// повертає старі поля, червоніє цей тест;</item>
/// <item>прибрати <c>store.MarkChanged(map)</c> — заміна лише полів не піднімає версію, і друга правка з тією самою
/// версією проходить; червоніє цей тест.</item>
/// </list>
/// </remarks>
public sealed partial class SourceEventsApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Audit", "L9-06")]
    public async Task L9_06_застаріла_версія_мапінгу_подій_дає_409_а_правка_лише_полів_піднімає_версію()
    {
        await using var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        var created = await manager.PostAsJsonAsync(new Uri("/api/v1/source-event-maps", UriKind.Relative), Create(stand, stand.EntityId));
        Assert.True(created.StatusCode == HttpStatusCode.OK, $"{created.StatusCode}: {app.ErrorsText}");
        var body = await JsonAsync(created);
        var id = body.GetProperty("id").GetInt32();
        var first = body.GetProperty("rowVersion").GetString();
        Assert.False(string.IsNullOrEmpty(first));

        var one = new Uri($"/api/v1/source-event-maps/{id}", UriKind.Relative);

        // Читання віддає ту саму версію, що й створення.
        Assert.Equal(first, (await JsonAsync(await manager.GetAsync(one))).GetProperty("rowVersion").GetString());

        // Правка з чинною версією проходить і дає нову.
        var updated = await manager.PutAsJsonAsync(one, WithVersion(Update(stand, stand.V8EntryId), first));
        Assert.True(updated.StatusCode == HttpStatusCode.OK, $"{updated.StatusCode}: {app.ErrorsText}");
        var second = (await JsonAsync(updated)).GetProperty("rowVersion").GetString();
        Assert.NotEqual(first, second);

        // Те саме тіло ще раз: рядок ext.SourceEventMap по суті не змінюється, міняються лише поля (дочірні таблиці).
        // Версія однаково має піднятися — інакше наступна правка зі «старою» версією пройшла б.
        var fieldsOnly = await manager.PutAsJsonAsync(one, WithVersion(Update(stand, stand.V8EntryId), second));
        Assert.True(fieldsOnly.StatusCode == HttpStatusCode.OK, $"{fieldsOnly.StatusCode}: {app.ErrorsText}");
        var third = (await JsonAsync(fieldsOnly)).GetProperty("rowVersion").GetString();
        Assert.NotEqual(second, third);

        // «Пауза» з кешованого рядка (перша версія) — 409 з ключем, і поля лишаються тими, що зберегла остання правка.
        var stale = await manager.PutAsJsonAsync(
            one,
            WithVersion(
                new
                {
                    volumeMode = "EventAttribute",
                    isActive = false,
                    fields = new[] { Field(stand.StartColumn, "$start"), Field(stand.EndColumn, "$end") },
                },
                first));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await JsonAsync(stale);
        Assert.Equal("ECR-INT-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-INT-0409.eventMapConcurrency", problem.GetProperty("messageKey").GetString());
        Assert.Equal(4, await ScalarAsync($"SELECT COUNT(*) FROM ext.SourceEventFieldMap WHERE SourceEventMapId = {id}"));
        Assert.Equal(third, (await JsonAsync(await manager.GetAsync(one))).GetProperty("rowVersion").GetString());

        // Без версії — як і раніше, без звірки: наявні споживачі контракту не ламаються.
        var unversioned = await manager.PutAsJsonAsync(one, Update(stand, stand.V8EntryId));
        Assert.True(unversioned.StatusCode == HttpStatusCode.OK, $"{unversioned.StatusCode}: {app.ErrorsText}");

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(one)).StatusCode);
    }

    private static JsonObject WithVersion(object body, string? rowVersion)
    {
        var node = JsonSerializer.SerializeToNode(body)!.AsObject();
        node["rowVersion"] = rowVersion;
        return node;
    }
}
