// tests/Ecr.Api.Tests/EntityFieldMapLifecycleTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Дії над мапінгом поля джерела: пауза / відновлення, приймання зміни одиниці,
/// видалення (директива №15, <c>BE-27</c>).
/// </summary>
/// <remarks>
/// ⛔ Доказ саме на HTTP. Конфлікт стану кидається ДВОМА шляхами — доменним
/// <c>DomainException</c> (пауза, одиниця) і <c>BusinessRuleException</c>
/// обробника видалення, — і в <c>409</c> їх перетворюють РІЗНІ гілки
/// <c>ExceptionHandlingMiddleware.Map</c>: правило суфікса <c>-0409</c> для
/// першого й окремий арм для другого. Обробниковий тест лишався б зеленим,
/// поки клієнт бачить <c>422</c> «дані невірні» там, де вводити нічого.
/// </remarks>
[Collection("SqlServer")]
public sealed class EntityFieldMapLifecycleTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime FromUtc = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Право з обробника, а не літералом: розійтися нема з чим.</summary>
    private static string Manage => Ecr.Application.Sources.SetEntityFieldMapPausedHandler.Permission;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-27")]
    public async Task Пауза_і_відновлення_перемикають_мапінг_а_повторні_дають_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);

        var stand = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);

        var paused = await PostAsync(client, stand.FieldMapId, "pause").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.False((await JsonAsync(paused).ConfigureAwait(true)).GetProperty("isActive").GetBoolean());

        // Стан у базі, а не лише у відповіді: збір читає саме його.
        Assert.False(await IsActiveAsync(stand.FieldMapId).ConfigureAwait(true));

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ. Прибрати `if (!IsActive)` у `EntityFieldMap.Pause`
        // — і повторна пауза відповідає `200`, а в журналі безпеки з'являється
        // другий запис про подію, якої не було.
        var again = await PostAsync(client, stand.FieldMapId, "pause").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("ECR-INT-0409", await ErrorCodeAsync(again).ConfigureAwait(true));

        var resumed = await PostAsync(client, stand.FieldMapId, "resume").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.True((await JsonAsync(resumed).ConfigureAwait(true)).GetProperty("isActive").GetBoolean());

        // Відновлювати непризупинене нема чого — інакше пауза була б дверима в
        // один бік лише на вигляд.
        var resumedTwice = await PostAsync(client, stand.FieldMapId, "resume").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, resumedTwice.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.9")]
    [Trait("Finding", "BE-27")]
    public async Task Приймання_зміни_одиниці_лишає_в_журналі_обидві_одиниці()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);

        var stand = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);

        var accepted = await client.PostAsJsonAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}/accept-unit-change", UriKind.Relative),
            new { sourceUnitId = stand.NewUnitId });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(
            stand.NewUnitId,
            (await JsonAsync(accepted).ConfigureAwait(true)).GetProperty("sourceUnitId").GetInt32());

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ. Прибрати виклик `audit.WriteSecurityEventAsync` в
        // `AcceptSourceUnitChangeHandler` — і червоним стає рівно це твердження:
        // одиниця змінилася, а відповіді на «хто вирішив, що нова правильна, і
        // якою була стара» не лишилося ніде.
        //
        // ⚠ Перевіряються ОБИДВА коди, не самі ідентифікатори: довідник одиниць
        // живий, і через рік рядка з `fromUnitId` може вже не бути — тоді
        // журнал із самим числом не відповідає ні на що.
        var details = await SecurityEventAsync(
            Ecr.Application.Sources.AcceptSourceUnitChangeHandler.AcceptedEventType,
            stand.FieldMapId).ConfigureAwait(true);

        Assert.NotNull(details);
        Assert.Contains($"\"fromUnitCode\":\"{stand.OldUnitCode}\"", details, StringComparison.Ordinal);
        Assert.Contains($"\"toUnitCode\":\"{stand.NewUnitCode}\"", details, StringComparison.Ordinal);
        Assert.Contains($"\"fromUnitId\":{stand.OldUnitId}", details, StringComparison.Ordinal);

        // Та сама одиниця вдруге — конфлікт: приймати нема чого, а другий запис
        // у журналі стверджував би зміну, якої не було.
        var same = await client.PostAsJsonAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}/accept-unit-change", UriKind.Relative),
            new { sourceUnitId = stand.NewUnitId });

        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal("ECR-INT-0409", await ErrorCodeAsync(same).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.9")]
    public async Task Прийняття_поміченої_збором_одиниці_без_id_відновлює_збір_а_повторне_дає_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);

        var stand = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);
        await MarkPendingAsync(stand.FieldMapId, stand.NewUnitCode, stand.NewUnitId).ConfigureAwait(true);

        // Тіло без id: одиницю сервер бере з позначки збору.
        var accepted = await client.PostAsJsonAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}/accept-unit-change", UriKind.Relative),
            new { });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var dto = await JsonAsync(accepted).ConfigureAwait(true);
        Assert.Equal(stand.NewUnitId, dto.GetProperty("sourceUnitId").GetInt32());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("pendingSourceUnitChange").ValueKind);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати `IsActive = true` в
        // `EntityFieldMap.AcceptSourceUnitChange` — макет обіцяє «Collection
        // resumed», а мапінг лишається на паузі.
        Assert.True(dto.GetProperty("isActive").GetBoolean());
        Assert.True(await IsActiveAsync(stand.FieldMapId).ConfigureAwait(true));

        var again = await client.PostAsJsonAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}/accept-unit-change", UriKind.Relative),
            new { });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(
            "err.ECR-INT-0409.mappingUnitChangeNotPending",
            (await JsonAsync(again).ConfigureAwait(true)).GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.9")]
    [Trait("Finding", "BE-27")]
    public async Task Ручне_відновлення_мапінгу_що_чекає_рішення_про_одиницю_дає_409_і_пауза_лишається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);

        var stand = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);
        await MarkPendingAsync(stand.FieldMapId, stand.NewUnitCode, stand.NewUnitId).ConfigureAwait(true);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати `if (HasPendingSourceUnitChange)` в
        // `EntityFieldMap.Resume` — відповідь 200, а позначка лишається, і
        // наступний прогін знову ставить мапінг на паузу.
        var refused = await PostAsync(client, stand.FieldMapId, "resume").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await JsonAsync(refused).ConfigureAwait(true);
        Assert.Equal("ECR-INT-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-INT-0409.mappingUnitChangePending", problem.GetProperty("messageKey").GetString());
        Assert.False(await IsActiveAsync(stand.FieldMapId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.9")]
    public async Task Одиницю_якої_немає_в_довіднику_не_приймають_422_і_мапінг_лишається_на_паузі()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);

        var stand = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);
        await MarkPendingAsync(stand.FieldMapId, "m3-unknown", unitId: null).ConfigureAwait(true);

        var refused = await client.PostAsJsonAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}/accept-unit-change", UriKind.Relative),
            new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await JsonAsync(refused).ConfigureAwait(true);
        Assert.Equal("ECR-INT-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-INT-0422.pendingUnitNotInCatalog", problem.GetProperty("messageKey").GetString());

        Assert.False(await IsActiveAsync(stand.FieldMapId).ConfigureAwait(true));
        Assert.Equal(stand.OldUnitId, await SourceUnitIdAsync(stand.FieldMapId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-27")]
    public async Task Мапінг_зі_зібраними_даними_не_видаляється_а_порожній_видаляється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);

        var withData = await ArrangeAsync(collectPoints: 3).ConfigureAwait(true);

        var refused = await client.DeleteAsync(
            new Uri($"/api/v1/entity-field-maps/{withData.FieldMapId}", UriKind.Relative));

        // ⛔ Рішення цього PR: видалення НЕ мовчазне і НЕ дозволене. Точки в
        // `ext.RawDataPoint` пояснює саме мапінг — одиниця, рядок-адресат,
        // згортка, — і стерти його означало б лишити дані без пояснення,
        // причому дані, що вже могли потрапити в поданий звіт. Вихід — пауза.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("ECR-INT-0409", await ErrorCodeAsync(refused).ConfigureAwait(true));

        // ⛔ Наслідок ВИДНО У ВІДПОВІДІ. «Щось уже зібрано» без «скільки» — це
        // відмова без підстави: рішення «пауза замість видалення» ухвалюють
        // саме за цим числом.
        var problem = await JsonAsync(refused).ConfigureAwait(true);
        Assert.Equal(3, problem.GetProperty("collectedPoints").GetInt32());
        Assert.Equal(
            "err.ECR-INT-0409.mappingHasCollectedData",
            problem.GetProperty("messageKey").GetString());

        // Запис на місці: відмова не має лишати мапінг напіввидаленим.
        Assert.True(await ExistsAsync(withData.FieldMapId).ConfigureAwait(true));

        // ⚠ Другий мапінг — БЕЗ зібраного: інакше тест доводив би лише те, що
        // маршрут завжди відмовляє.
        var empty = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);

        var deleted = await client.DeleteAsync(
            new Uri($"/api/v1/entity-field-maps/{empty.FieldMapId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await ExistsAsync(empty.FieldMapId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.12")]
    [Trait("Finding", "BE-27")]
    public async Task Без_права_Integration_Manage_дії_дають_403_і_нічого_не_міняють()
    {
        // ⚠ Користувач автентифікований і зі СТОРОННІМ правом, а не безправний:
        // інакше тест доводив би лише те, що маршрут закритий для анонімів.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.View").ConfigureAwait(true);

        var stand = await ArrangeAsync(collectPoints: 0).ConfigureAwait(true);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PostAsync(client, stand.FieldMapId, "pause").ConfigureAwait(true)).StatusCode);

        var accept = await client.PostAsJsonAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}/accept-unit-change", UriKind.Relative),
            new { sourceUnitId = stand.NewUnitId });
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);

        var delete = await client.DeleteAsync(
            new Uri($"/api/v1/entity-field-maps/{stand.FieldMapId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        // ⛔ Стан не змінився ЖОДНОЮ з трьох дій. Без цих тверджень тест лишався
        // б зеленим і на системі, яка спершу робить, а потім згадує перевірити
        // право.
        Assert.True(await ExistsAsync(stand.FieldMapId).ConfigureAwait(true));
        Assert.True(await IsActiveAsync(stand.FieldMapId).ConfigureAwait(true));
        Assert.Equal(stand.OldUnitId, await SourceUnitIdAsync(stand.FieldMapId).ConfigureAwait(true));
    }

    /// <summary>Що заведено для одного тесту.</summary>
    private sealed record Stand(
        int FieldMapId, int OldUnitId, string OldUnitCode, int NewUnitId, string NewUnitCode);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, int fieldMapId, string action)
        => client.PostAsync(
            new Uri($"/api/v1/entity-field-maps/{fieldMapId}/{action}", UriKind.Relative), content: null);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false))
            .RootElement;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
        => (await JsonAsync(response).ConfigureAwait(false)).GetProperty("errorCode").GetString();

    private async Task<bool> ExistsAsync(int fieldMapId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.EntityFieldMaps.AsNoTracking().AnyAsync(m => m.Id == fieldMapId)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsActiveAsync(int fieldMapId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.EntityFieldMaps.AsNoTracking()
            .Where(m => m.Id == fieldMapId).Select(m => m.IsActive).SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Позначка «джерело змінило одиницю» — тим самим шляхом, що й збір.</summary>
    private async Task MarkPendingAsync(int fieldMapId, string unitCode, int? unitId)
    {
        await using var db = new EcrDbContext(Options());

        await new CollectionStore(db, new TestClock(Now))
            .PauseForSourceUnitChangeAsync(fieldMapId, unitCode, unitId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<int?> SourceUnitIdAsync(int fieldMapId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.EntityFieldMaps.AsNoTracking()
            .Where(m => m.Id == fieldMapId).Select(m => m.SourceUnitId).SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary><c>DetailsJson</c> події журналу безпеки; <c>null</c> — події немає.</summary>
    private async Task<string?> SecurityEventAsync(string eventType, int fieldMapId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DetailsJson FROM aud.SecurityEvent WHERE EventType = @e AND DetailsJson LIKE @like;";
        command.Parameters.AddWithValue("@e", eventType);
        command.Parameters.AddWithValue(
            "@like", $"%\"fieldMapId\":{fieldMapId.ToString(CultureInfo.InvariantCulture)},%");

        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
    }

    /// <summary>
    /// Джерело, сутність, дві одиниці й мапінг на колонку; за потреби — зібрані
    /// точки за його полем.
    /// </summary>
    /// <param name="collectPoints">Скільки точок покласти в <c>ext.RawDataPoint</c>.</param>
    private async Task<Stand> ArrangeAsync(int collectPoints)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None).ConfigureAwait(false);

        await using var db = new EcrDbContext(Options());
        var tag = $"{Guid.NewGuid():N}"[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("BE-27"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);

        // ⛔ Неактивне НАВМИСНО — та сама пастка, що в `CollectionScheduleStateSaveTests`.
        // База тестів спільна на весь прогін: активне джерело без завершеного
        // збору для `SourcesHealthCheck` — прогалина, тобто `Degraded`, і
        // `HealthTests.Health_ready_зелений…` падає в КОЖНОМУ повному прогоні,
        // проходячи поодинці. Дії над мапінгом активності джерела не питають:
        // вони адресують сам мапінг за ідентифікатором.
        entity.Deactivate();
        db.SourceEntities.Add(entity);

        var dimensionId = await db.Dimensions.OrderBy(d => d.Id).Select(d => d.Id).FirstAsync()
            .ConfigureAwait(false);

        var oldUnit = new Unit(
            EcrCode.Create($"UO{tag}"), Name("old"), Name("old"), dimensionId,
            isBase: false, factorToBase: 1m, offsetToBase: 0m);
        var newUnit = new Unit(
            EcrCode.Create($"UN{tag}"), Name("new"), Name("new"), dimensionId,
            isBase: false, factorToBase: 1000m, offsetToBase: 0m);

        db.Units.AddRange(oldUnit, newUnit);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var field = $"Flare_{tag}_CO";
        var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
        map.SetUnits(oldUnit.Id, newUnit.Id);
        map.SetMaterialization($"R1_{tag}", AggregationKind.Sum);

        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (collectPoints > 0)
        {
            // ⚠ Через справжній `CollectionStore`, а не сирим INSERT: лічильник
            // наслідків рахує саме те, що туди кладе збір.
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(
                entity.Id, FromUtc, FromUtc.AddDays(1), isCatchUp: false,
                triggeredByUserId: null, CancellationToken.None).ConfigureAwait(false);

            var points = Enumerable.Range(0, collectPoints)
                .Select(i => new Ecr.Application.Ports.SourceDataPoint(
                    field, FromUtc.AddHours(i), 10m, null, null, "Good"))
                .ToList();

            await store.UpsertRawPointsAsync(runId, entity.Id, points, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return new Stand(map.Id, oldUnit.Id, oldUnit.Code, newUnit.Id, newUnit.Code);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і названими правами.</summary>
    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
