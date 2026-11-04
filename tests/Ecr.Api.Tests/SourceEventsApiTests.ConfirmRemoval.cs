// tests/Ecr.Api.Tests/SourceEventsApiTests.ConfirmRemoval.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-464: підтвердження масового видалення зниклих у PI подій доступне через «Отримати з PI зараз»
/// (<c>?confirmRemoval=true</c>): те саме право <c>Integration.Manage</c>, постановка — і слід у журналі структурних змін.
/// </summary>
/// <remarks>Мутація: контролер не передає <c>confirmRemoval</c> в обробник — запису журналу немає, тест червоний.</remarks>
public sealed partial class SourceEventsApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task D464_підтвердження_видалення_ставить_синк_і_пишеться_в_журнал_а_без_права_403()
    {
        await using var stand = await ArrangeAsync();
        await AddMapAsync(stand);
        using var app = new EcrApiFactory(sql);
        // ✎ Аудит 31.10 (G3): підтвердження — тілом, прив'язане до показаного блоку (мапінг і кандидати).
        var confirmed = new Uri($"/api/v1/sources/{stand.EntityId}/source-events/sync", UriKind.Relative);
        var shown = new { confirmRemovals = new[] { new { mapId = 1, candidates = 11 } } };
        var journal =
            "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'SourceEventRemovalConfirmed' "
            + $"AND EntityType = N'ext.SourceEntity' AND EntityId = {stand.EntityId}";

        using (var viewer = await SignedInAsync(app, ["Integration.View"], null, null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(confirmed, shown)).StatusCode);
        }

        Assert.Equal(0, await ScalarAsync(journal));

        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        // Звичайна синхронізація сліду не лишає.
        var plain = await manager.PostAsJsonAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events/sync", UriKind.Relative), new { });
        Assert.Equal(HttpStatusCode.Accepted, plain.StatusCode);
        Assert.Equal(0, await ScalarAsync(journal));

        // Без тіла взагалі (так шле клієнт звичайний синк) — теж звичайний синк.
        var bodiless = await manager.PostAsync(new Uri($"/api/v1/sources/{stand.EntityId}/source-events/sync", UriKind.Relative), null);
        Assert.True(bodiless.StatusCode == HttpStatusCode.Accepted, $"{bodiless.StatusCode}: {app.ErrorsText}");
        Assert.Equal(0, await ScalarAsync(journal));

        var accepted = await manager.PostAsJsonAsync(confirmed, shown);
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, $"{accepted.StatusCode}: {app.ErrorsText}");
        var jobId = (await JsonAsync(accepted)).GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));

        Assert.Equal(1, await ScalarAsync(journal));
        Assert.Equal(
            1,
            await ScalarAsync(
                "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'SourceEventRemovalConfirmed' "
                + $"AND EntityId = {stand.EntityId} AND NewJson LIKE N'%{jobId}%'"));
    }
}
