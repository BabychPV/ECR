// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.HeaderUndo.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// C-9 (аудит 2026-11-10, L5): скасування дозволу ШАПКИ, місяць якого вже поділено (частини з цим дозволом), відмовляє
/// так само, як скасування дозволу рядка (<c>422 permitHistoryNothingToUndo</c>); частини й сегмент лишаються.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати перевірку частин з <c>UndoHeaderPermitHandler</c> — перший тест отримує 200, сегмента немає, а
/// частини скасованого дозволу лишаються в <c>doc.CellPermitPart</c>.
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C-9")]
    public async Task Скасування_дозволу_шапки_з_поділеним_місяцем_відмовлено()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddHeaderAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);
        var split = await SplitAsync(client, s, 5m, 7m).ConfigureAwait(true);
        Assert.True(split.StatusCode == HttpStatusCode.OK, $"{split.StatusCode}: {await split.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        Assert.Equal(2, await PartCountAsync(s).ConfigureAwait(true));

        var undo = await UndoHeaderAsync(client, s).ConfigureAwait(true);
        var body = await undo.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(undo.StatusCode == HttpStatusCode.UnprocessableEntity, $"{undo.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-REQ-0422.permitHistoryNothingToUndo", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));
        Assert.Equal(2, await PartCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Без поділу скасування дозволу шапки й далі проходить (сторож не блокує зайвого).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C-9")]
    public async Task Скасування_дозволу_шапки_без_поділу_проходить()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddHeaderAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        var undo = await UndoHeaderAsync(client, s).ConfigureAwait(true);
        var body = await undo.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(undo.StatusCode == HttpStatusCode.OK, $"{undo.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    private static Task<HttpResponseMessage> UndoHeaderAsync(HttpClient client, Scenario s)
        => client.PostAsJsonAsync(new Uri($"{Base(s)}/header/undo", UriKind.Relative), new { fieldCode = HeaderCode });
}
