// tests/Ecr.Api.Tests/CollectionSchedulesControllerTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.External;
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
/// Розклад збору крізь справжній HTTP і справжню базу (<c>BE-21b</c>): право,
/// перевірка cron справжнім планувальником, <c>If-Match</c>, видалення.
/// </summary>
/// <remarks>
/// ⚠ Cron увімкнених розкладів навмисно веде в 2099 рік: планувальник у
/// тестовому хості СПРАВЖНІЙ і запущений, а задача збору, яка спрацювала б під
/// час прогону, пішла б у неіснуюче джерело і лишила б помилки в чужих тестах.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionSchedulesControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Schedule-Probe-2026!";
    private const string FarFuture = "0 0 3 1 1 ? 2099";
    private const string FarFutureLater = "0 30 4 1 1 ? 2099";

    /// <summary>П'ятипольний unix-cron: Quartz його не приймає.</summary>
    private const string Unsupported = "30 4 * * *";

    private static readonly Uri Schedules = new("/api/v1/collection-schedules", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21b")]
    public async Task Без_права_403_на_кожному_маршруті()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage").ConfigureAwait(true);

        HttpResponseMessage[] responses =
        [
            await client.GetAsync(Schedules).ConfigureAwait(true),
            await client.PostAsJsonAsync(
                Schedules, new { sourceEntityId = 1, cron = FarFuture, isEnabled = true }).ConfigureAwait(true),
            await client.PutAsJsonAsync(At(1), new { cron = FarFuture, isEnabled = true }).ConfigureAwait(true),
            await client.DeleteAsync(At(1)).ConfigureAwait(true),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21b")]
    public async Task Перелік_несе_код_сутності_і_версію_рядка_а_зміна_доїжджає_до_бази()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var (id, code) = await AddScheduleAsync(FarFuture).ConfigureAwait(true);

        var row = await RowAsync(client, id).ConfigureAwait(true);
        Assert.Equal(code, row.GetProperty("sourceEntityCode").GetString());
        Assert.Equal(FarFuture, row.GetProperty("cron").GetString());
        Assert.True(row.GetProperty("isEnabled").GetBoolean());

        var rowVersion = row.GetProperty("rowVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rowVersion));

        var saved = await PutAsync(client, id, FarFutureLater, isEnabled: false, rowVersion).ConfigureAwait(true);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, $"{saved.StatusCode}: {app.ErrorsText}");

        var body = await BodyAsync(saved).ConfigureAwait(true);
        Assert.Equal(FarFutureLater, body.GetProperty("cron").GetString());
        Assert.False(body.GetProperty("isEnabled").GetBoolean());
        Assert.NotEqual(rowVersion, body.GetProperty("rowVersion").GetString());

        await using var db = NewDb();
        var stored = await db.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
        Assert.Equal((FarFutureLater, false, (string?)null), (stored.CronExpression, stored.IsEnabled, stored.LastError));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21c")]
    public async Task Створення_заводить_розклад_сутності_без_нього_а_другий_на_ту_саму_сутність_дає_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var (entityId, code) = await AddSourceEntityAsync().ConfigureAwait(true);

        var created = await PostAsync(client, entityId, FarFuture).ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");

        var body = await BodyAsync(created).ConfigureAwait(true);
        Assert.Equal(code, body.GetProperty("sourceEntityCode").GetString());
        Assert.Equal(FarFuture, body.GetProperty("cron").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("rowVersion").GetString()));

        var id = body.GetProperty("id").GetInt32();

        await using (var db = NewDb())
        {
            var stored = await db.CollectionSchedules.AsNoTracking()
                .SingleAsync(s => s.Id == id).ConfigureAwait(true);

            Assert.Equal(
                (entityId, FarFuture, true, (string?)null),
                (stored.SourceEntityId, stored.CronExpression, stored.IsEnabled, stored.LastError));
        }

        // ⛔ Другий розклад на ту саму сутність — це другий тригер планувальника
        // з тим самим завданням, тобто подвійний збір.
        var duplicate = await PostAsync(client, entityId, FarFutureLater).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var conflict = await BodyAsync(duplicate).ConfigureAwait(true);
        Assert.Equal("ECR-JOB-0409", conflict.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleExists", conflict.GetProperty("messageKey").GetString());

        await using var check = NewDb();
        Assert.Equal(
            1, await check.CollectionSchedules.CountAsync(s => s.SourceEntityId == entityId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21c")]
    public async Task Створення_з_невалідним_cron_дає_422_а_для_неіснуючої_сутності_404_і_рядка_не_зʼявляється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var (entityId, _) = await AddSourceEntityAsync().ConfigureAwait(true);

        var refused = await PostAsync(client, entityId, Unsupported).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "err.ECR-REQ-0422.collectionScheduleCron",
            (await BodyAsync(refused).ConfigureAwait(true)).GetProperty("messageKey").GetString());

        var missing = await PostAsync(client, sourceEntityId: 0, FarFuture).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            "err.ECR-INT-0404.sourceEntity",
            (await BodyAsync(missing).ConfigureAwait(true)).GetProperty("messageKey").GetString());

        // ⛔ Відмова ДО бази: рядка не зʼявилося ні від першої спроби, ні від другої.
        await using var db = NewDb();
        Assert.False(
            await db.CollectionSchedules.AnyAsync(s => s.SourceEntityId == entityId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21b")]
    public async Task Невалідний_cron_дає_422_і_розклад_у_базі_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var (id, _) = await AddScheduleAsync(FarFuture).ConfigureAwait(true);
        var rowVersion = (await RowAsync(client, id).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        var refused = await PutAsync(client, id, Unsupported, isEnabled: true, rowVersion).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await BodyAsync(refused).ConfigureAwait(true);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REQ-0422.collectionScheduleCron", problem.GetProperty("messageKey").GetString());

        // ⛔ Перевірка стоїть ДО бази: cron у рядку лишився попереднім.
        await using var db = NewDb();
        Assert.Equal(
            FarFuture,
            (await db.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true)).CronExpression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21b")]
    public async Task Стара_версія_рядка_дає_409_а_неіснуючий_розклад_404()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var (id, _) = await AddScheduleAsync(FarFuture).ConfigureAwait(true);
        var stale = (await RowAsync(client, id).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        // Хтось інший зберіг правку раніше — версія рядка вже не та.
        var first = await PutAsync(client, id, FarFutureLater, isEnabled: true, stale).ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {app.ErrorsText}");

        var second = await PutAsync(client, id, FarFuture, isEnabled: true, stale).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await BodyAsync(second).ConfigureAwait(true);
        Assert.Equal("ECR-JOB-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-JOB-0409.collectionScheduleChanged", problem.GetProperty("messageKey").GetString());

        await using var db = NewDb();
        Assert.Equal(
            FarFutureLater,
            (await db.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true)).CronExpression);

        var missing = await PutAsync(client, id: 0, FarFuture, isEnabled: true, stale).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21b")]
    public async Task Видалення_прибирає_розклад_а_повторне_дає_404()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var (id, _) = await AddScheduleAsync(FarFuture).ConfigureAwait(true);
        var rowVersion = (await RowAsync(client, id).ConfigureAwait(true)).GetProperty("rowVersion").GetString();

        var removed = await SendAsync(client, HttpMethod.Delete, At(id), rowVersion, body: null).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");

        await using var db = NewDb();
        Assert.False(await db.CollectionSchedules.AnyAsync(s => s.Id == id).ConfigureAwait(true));

        var again = await SendAsync(client, HttpMethod.Delete, At(id), rowVersion, body: null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "UI-09")]
    public async Task Фільтр_dataSource_віддає_розклади_лише_цього_зʼєднання_а_невідомий_код_порожній_перелік()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var mine = await AddDataSourceAsync().ConfigureAwait(true);
        var other = await AddDataSourceAsync().ConfigureAwait(true);
        var (first, _) = await AddScheduleAsync(FarFuture, mine.Id).ConfigureAwait(true);
        var (second, _) = await AddScheduleAsync(FarFuture, mine.Id).ConfigureAwait(true);
        await AddScheduleAsync(FarFuture, other.Id).ConfigureAwait(true);

        var filtered = await client.GetAsync(Filtered(mine.Code)).ConfigureAwait(true);
        Assert.True(filtered.StatusCode == HttpStatusCode.OK, $"{filtered.StatusCode}: {app.ErrorsText}");

        var ids = (await BodyAsync(filtered).ConfigureAwait(true))
            .EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).Order().ToArray();
        Assert.Equal(new[] { first, second }.Order().ToArray(), ids);

        // ⚠ Фільтр, а не адресація: невідоме з'єднання — порожній перелік, не 404.
        var unknown = await client.GetAsync(Filtered($"Nope{Guid.NewGuid():N}")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(0, (await BodyAsync(unknown).ConfigureAwait(true)).GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "UI-09")]
    public async Task Розклад_і_сутність_джерела_несуть_зʼєднання_якому_належать()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.EditSchedule").ConfigureAwait(true);

        var source = await AddDataSourceAsync().ConfigureAwait(true);
        var (id, _) = await AddScheduleAsync(FarFuture, source.Id).ConfigureAwait(true);
        var (entityId, _) = await AddSourceEntityAsync(source.Id).ConfigureAwait(true);

        var listed = await RowAsync(client, id).ConfigureAwait(true);
        var created = await BodyAsync(await PostAsync(client, entityId, FarFuture).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.All(
            new[] { listed, created },
            row => Assert.Equal(
                (source.Id, source.Code),
                (row.GetProperty("dataSourceId").GetInt32(), row.GetProperty("dataSourceCode").GetString())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "UI-09")]
    public async Task Перелік_сутностей_збору_несе_код_зʼєднання()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage").ConfigureAwait(true);

        var source = await AddDataSourceAsync().ConfigureAwait(true);
        var (entityId, _) = await AddSourceEntityAsync(source.Id).ConfigureAwait(true);

        var listed = await client.GetAsync(new Uri("/api/v1/sources", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(listed.StatusCode == HttpStatusCode.OK, $"{listed.StatusCode}: {app.ErrorsText}");

        var row = (await BodyAsync(listed).ConfigureAwait(true))
            .EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == entityId);
        Assert.Equal(
            (source.Id, source.Code),
            (row.GetProperty("dataSourceId").GetInt32(), row.GetProperty("dataSourceCode").GetString()));
    }

    private static Uri Filtered(string dataSource)
        => new($"{Schedules}?dataSource={Uri.EscapeDataString(dataSource)}", UriKind.Relative);

    private static Uri At(int id) => new($"{Schedules}/{id}", UriKind.Relative);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, int sourceEntityId, string cron)
        => client.PostAsJsonAsync(Schedules, new { sourceEntityId, cron, isEnabled = true });

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client, int id, string cron, bool isEnabled, string? ifMatch)
        => SendAsync(client, HttpMethod.Put, At(id), ifMatch, new { cron, isEnabled });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, Uri path, string? ifMatch, object? body)
    {
        using var request = new HttpRequestMessage(method, path);

        if (ifMatch is not null)
        {
            // ⚠ У лапках — як вимагає HTTP від ETag; сервер приймає й голе значення.
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>Рядок переліку з цим ідентифікатором.</summary>
    private static async Task<JsonElement> RowAsync(HttpClient client, int id)
    {
        var listed = await BodyAsync(await client.GetAsync(Schedules).ConfigureAwait(false)).ConfigureAwait(false);

        return listed.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == id);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Джерело, сутність і розклад; повертає ключ розкладу і код сутності.</summary>
    /// <remarks>
    /// ⛔ Сутність НЕАКТИВНА навмисно: база тестів спільна, і активне джерело,
    /// яке жодного разу не збиралося, робить <c>/health/ready</c> жовтим для
    /// кожного наступного тесту прогону. Розкладу активність не потрібна —
    /// перевіряється його редагування, а не збір.
    /// </remarks>
    private async Task<(int Id, string Code)> AddScheduleAsync(string cron, int? dataSourceId = null)
    {
        var (entityId, code) = await AddSourceEntityAsync(dataSourceId).ConfigureAwait(false);

        await using var db = NewDb();
        var schedule = new CollectionSchedule(entityId, cron);
        db.CollectionSchedules.Add(schedule);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (schedule.Id, code);
    }

    /// <summary>З'єднання без сутностей; повертає його ключ і код.</summary>
    private async Task<(int Id, string Code)> AddDataSourceAsync()
    {
        await using var db = NewDb();

        var dataSource = new DataSource(
            EcrCode.Create($"Sch{Guid.NewGuid().ToString("N")[..10]}"),
            new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Source" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (dataSource.Id, dataSource.Code);
    }

    /// <summary>
    /// Сутність БЕЗ розкладу (у новому з'єднанні або в названому); повертає ключ
    /// і код сутності.
    /// </summary>
    private async Task<(int Id, string Code)> AddSourceEntityAsync(int? dataSourceId = null)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var sourceId = dataSourceId ?? (await AddDataSourceAsync().ConfigureAwait(false)).Id;
        await using var db = NewDb();

        var entity = new SourceEntity(sourceId, $"Ent{tag}", RegistrySourceKind.External);
        entity.Describe($"Entity {tag}", null);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (entity.Id, entity.Code);
    }

    /// <summary>Клієнт із сеансом локального користувача з одним правом.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string permission)
    {
        var name = $"sch_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Schedule test" }));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, permission));
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
}
