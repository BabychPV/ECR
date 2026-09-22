// tests/Ecr.Api.Tests/CampaignSummaryTests.cs
using System.Net;
using System.Text.Json;
using Ecr.Application.Reporting;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/campaign/summary?periodKey=</c> — огляд кампанії звітності
/// (директива №15 <c>BE-22</c>, рішення людини <c>Q15-07</c>).
/// </summary>
/// <remarks>
/// ⛔ Головне питання пункту — ПРАВА ВИДИМОСТІ, і відповідь людини на
/// <c>Q15-07</c> перевертає звичну тут відповідь: «окреме право
/// <c>Report.ViewCampaign</c>, видається явно; лише лічильники станів, без
/// значень». Тобто огляд віддає ЧУЖІ проєкти — ті, на які в користувача немає
/// жодного гранту, — і це не діра, а сенс пункту: керівник кампанії мусить
/// бачити, хто саме її затримує.
///
/// ⚠ Тому тест наскрізний і саме тут: межа доступу перевіряється справжнім
/// конвеєром автентифікації на справжній базі. Мок сховища довів би лише, що
/// обробник кличе порт, — тобто нічого про те, що видно на екрані керівника.
/// </remarks>
[Collection("SqlServer")]
public sealed class CampaignSummaryTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Без_права_Report_ViewCampaign_огляд_дає_403()
    {
        // ⚠ Користувач із правом ЧИТАТИ регламентну звітність і власні
        // документи: інакше тест доводив би лише те, що маршрут закритий для
        // безправного, і не розрізняв би родину `Report.%` та окреме право,
        // яке рішення `Q15-07` і завело.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Report.ViewRegulatory", "Document.View")
            .ConfigureAwait(true);

        var response = await GetAsync(client, 202601).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // ⛔ У відмові — КОД ПРАВА: адміністратор має знати, що саме видати.
        Assert.Contains(
            GetCampaignSummaryHandler.Permission,
            await response.Content.ReadAsStringAsync().ConfigureAwait(true),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Огляд_показує_проєкт_на_який_у_користувача_немає_жодного_гранту()
    {
        // ⛔ КЛЮЧОВИЙ ТЕСТ пункту і МУТАЦІЙНИЙ ДОКАЗ до нього: додати в
        // `GetCampaignSummaryHandler` межу проєктів (той самий
        // `ListDocumentsHandler.ReadableProjects(profile)`, що в зведенні
        // переліку документів) — і червоним стає рівно це твердження: проєкт
        // зникне з відповіді, бо гранту на нього немає ні в кого.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None).ConfigureAwait(true);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));

            var state = new ApprovalState(chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value);
            state.Submit(1, Now);
            db.ApprovalStates.Add(state);

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);

        // Право — і БІЛЬШЕ НІЧОГО: жодного `sec.ResourceGrant` цьому
        // користувачеві не видавали.
        using var client = await SignedInAsync(app, GetCampaignSummaryHandler.Permission)
            .ConfigureAwait(true);

        var response = await GetAsync(client, chain.PeriodKey.Value).ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        Assert.Equal(chain.PeriodKey.Value, body.GetProperty("periodKey").GetInt32());

        var row = body.GetProperty("projects")
            .EnumerateArray()
            .Single(p => p.GetProperty("projectId").GetInt32() == chain.ProjectId);

        Assert.Equal(1, row.GetProperty("documents").GetInt32());
        Assert.Equal(1, row.GetProperty("submitted").GetInt32());
        Assert.Equal(0, row.GetProperty("draft").GetInt32());

        // Форма проводу для клієнта: класифікація — рядком, строк у будівника
        // ланцюга не пораховано (`null`), підсумки — окремим об'єктом.
        Assert.Equal("InProgress", row.GetProperty("progress").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("submissionDeadline").ValueKind);
        Assert.True(body.GetProperty("totals").GetProperty("projects").GetInt32() >= 1);

        // ⛔ Лічильники — і нічого крім них (рішення `Q15-07`: «без значень»).
        // Поля зі значеннями комірок чи сумами зрізу тут бути не може: воно
        // перетворило б право на огляд на обхід грантів.
        Assert.False(row.TryGetProperty("values", out _));
        Assert.False(row.TryGetProperty("cells", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-22")]
    public async Task Огляд_за_періодом_нуль_дає_422_а_не_порожню_кампанію()
    {
        // ⚠ Незв'язаний параметр запиту дає 0 (`A7-28`), і порожня кампанія у
        // відповідь виглядала б як «усі все подали».
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, GetCampaignSummaryHandler.Permission)
            .ConfigureAwait(true);

        var response = await GetAsync(client, 0).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, int periodKey)
        => client.GetAsync(new Uri(
            $"/api/v1/campaign/summary?periodKey={periodKey.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            UriKind.Relative));

    /// <summary>Клієнт із чинним сеансом і названими правами (помічник спільний).</summary>
    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
