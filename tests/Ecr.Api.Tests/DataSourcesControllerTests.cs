// tests/Ecr.Api.Tests/DataSourcesControllerTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Джерела даних крізь справжній HTTP і справжню базу (<c>BE-21</c>): право,
/// секрет у відповідях відсутній, проба з'єднання, заборона видалення.
/// </summary>
/// <remarks>
/// ⛔ Мережі тут немає: транспорт PI Web API підмінено на весь стенд
/// (<see cref="EcrApiFactory.SourceCalls"/>).
/// </remarks>
[Collection("SqlServer")]
public sealed class DataSourcesControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Source-Probe-2026!";

    /// <summary>Значення секрету, яке середовище дає під джерело.</summary>
    private const string Marker = "SvcAccountSig-41c8";

    private const string Endpoint = "https://pi.corp.example/piwebapi";
    private const string Reason = "Переїхав сервер AF, звіряємо доступ.";
    private static readonly Uri Sources = new("/api/v1/data-sources", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Без_права_403_на_кожному_маршруті()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "System.ViewHealth").ConfigureAwait(true);

        HttpResponseMessage[] responses =
        [
            await client.GetAsync(Sources).ConfigureAwait(true),
            await client.PostAsJsonAsync(Sources, Body("X")).ConfigureAwait(true),
            await client.PutAsJsonAsync(At("1"), Body("X")).ConfigureAwait(true),
            await client.DeleteAsync(At("1")).ConfigureAwait(true),
            await client.PostAsJsonAsync(At("1/test"), new { reason = Reason }).ConfigureAwait(true),
        ];

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Manage_без_View_бачить_перелік_а_View_лише_читає()
    {
        using var app = new EcrApiFactory(sql);

        // `Integration.Manage` включає `Integration.View`: хто заводить з'єднання,
        // бачить їхній перелік (раніше — 403 під кнопкою «New connection»).
        using (var manager = await SignedInAsync(app, "Integration.Manage").ConfigureAwait(true))
        {
            var listed = await manager.GetAsync(Sources).ConfigureAwait(true);
            Assert.True(listed.StatusCode == HttpStatusCode.OK, $"{listed.StatusCode}: {app.ErrorsText}");
        }

        using var viewer = await SignedInAsync(app, "Integration.View").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Sources).ConfigureAwait(true)).StatusCode);

        HttpResponseMessage[] writes =
        [
            await viewer.PostAsJsonAsync(Sources, Body("X")).ConfigureAwait(true),
            await viewer.PutAsJsonAsync(At("1"), Body("X")).ConfigureAwait(true),
            await viewer.DeleteAsync(At("1")).ConfigureAwait(true),
            await viewer.PostAsJsonAsync(At("1/test"), new { reason = Reason }).ConfigureAwait(true),
        ];

        Assert.All(writes, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Секрет_середовища_не_виїжджає_жодною_відповіддю_і_не_лежить_у_базі()
    {
        var code = $"PI{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        // ⛔ Секрет справді є в конфігурації процесу — інакше перевірка
        // доводила б лише те, що його ніде не задано. Ім'я виводиться з коду
        // джерела; префікс `ECR_` і роздільник `__` — той самий шлях, яким
        // застосунок читає решту налаштувань.
        Environment.SetEnvironmentVariable($"ECR_Secrets__DataSource.{code}", Marker);

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

            var seen = new StringBuilder();

            var created = await client.PostAsJsonAsync(Sources, Body(code)).ConfigureAwait(true);
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");

            var body = await BodyAsync(created, seen).ConfigureAwait(true);
            var id = body.GetProperty("id").GetInt32();
            Assert.True(body.GetProperty("hasSecret").GetBoolean());

            var listed = await BodyAsync(await client.GetAsync(Sources).ConfigureAwait(true), seen).ConfigureAwait(true);
            var row = listed.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == id);
            Assert.Equal("PiWebApi", row.GetProperty("transport").GetString());
            Assert.True(row.GetProperty("hasSecret").GetBoolean());

            // Проба йде ТИМ САМИМ адаптером, яким збирають: транспорт підмінено
            // стендом, тож перевіряється саме шлях, а не мережа.
            var probe = await BodyAsync(
                await client.PostAsJsonAsync(At($"{id}/test"), new { reason = Reason }).ConfigureAwait(true), seen)
                .ConfigureAwait(true);

            Assert.True(probe.GetProperty("ok").GetBoolean(), $"{probe}\n{app.ErrorsText}");
            Assert.Equal(1, probe.GetProperty("entities").GetInt32());
            Assert.Contains(app.SourceCalls, uri => uri.AbsoluteUri.StartsWith(Endpoint, StringComparison.Ordinal));

            // ⛔ Головне: значення секрету немає НІДЕ, куди воно могло б просочитися.
            Assert.DoesNotContain(Marker, seen.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(app.ServerLog, line => line.Contains(Marker, StringComparison.Ordinal));

            // ⛔ Побайтно по всьому рядку, а не по одній колонці: рядок у базі
            // читається цілком, і поле, у яке секрет потрапив би «тимчасово»,
            // тут назветься саме себе.
            await using var db = NewDb();
            var stored = await db.DataSources.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
            Assert.DoesNotContain(Marker, JsonSerializer.Serialize(stored), StringComparison.Ordinal);
            Assert.Equal($"DataSource.{code}", stored.SecretName);

            var events = await db.Database
                .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType LIKE N'DataSource%'")
                .ToListAsync().ConfigureAwait(true);
            Assert.Contains(events, e => e.Contains(code, StringComparison.Ordinal));
            Assert.DoesNotContain(events, e => e.Contains(Marker, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable($"ECR_Secrets__DataSource.{code}", null);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Адреса_з_паролем_дає_422_і_джерела_не_створює()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        var code = $"FL{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var refused = await client.PostAsJsonAsync(
            Sources,
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "FLERT" },
                transport = "Sql",
                endpoint = "Server=flert;Database=Vol;User Id=svc;Password=hunter2",
            }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var problem = await BodyAsync(refused, null).ConfigureAwait(true);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(
            "err.ECR-REQ-0422.dataSourceEndpointCarriesSecret", problem.GetProperty("messageKey").GetString());

        await using var db = NewDb();
        Assert.False(await db.DataSources.AnyAsync(s => s.Code == code).ConfigureAwait(true));
        Assert.DoesNotContain(app.ServerLog, line => line.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Джерело_із_сутністю_збору_не_видаляється_а_порожнє_видаляється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        var code = $"PI{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var created = await client.PostAsJsonAsync(Sources, Body(code)).ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");

        var createdBody = await BodyAsync(created, null).ConfigureAwait(true);
        var id = createdBody.GetProperty("id").GetInt32();
        var version = createdBody.GetProperty("rowVersion").GetString();

        int entityId;
        await using (var arrange = NewDb())
        {
            var entity = new SourceEntity(id, $"ENT-{code}", RegistrySourceKind.External);
            arrange.SourceEntities.Add(entity);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
            entityId = entity.Id;
        }

        var refused = await SendAsync(client, HttpMethod.Delete, id, version, body: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await BodyAsync(refused, null).ConfigureAwait(true);
        Assert.Equal("ECR-JOB-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-JOB-0409.dataSourceInUse", problem.GetProperty("messageKey").GetString());

        // Перелік каже те саме числом — саме воно й пояснює відмову.
        var listed = await BodyAsync(await client.GetAsync(Sources).ConfigureAwait(true), null).ConfigureAwait(true);
        var row = listed.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == id);
        Assert.Equal(1, row.GetProperty("sourceEntities").GetInt32());

        await using (var cleanup = NewDb())
        {
            await cleanup.SourceEntities.Where(e => e.Id == entityId).ExecuteDeleteAsync().ConfigureAwait(true);
        }

        var removed = await SendAsync(client, HttpMethod.Delete, id, version, body: null).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");

        await using var db = NewDb();
        Assert.False(await db.DataSources.AnyAsync(s => s.Id == id).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Актуальна_версія_дає_200_з_новою_версією_а_видалення_з_нею_204()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        var (id, code) = await AddInactiveAsync().ConfigureAwait(true);
        var version = await VersionAsync(client, id).ConfigureAwait(true);

        var saved = await SendAsync(client, HttpMethod.Put, id, version, Body(code, isActive: false, catalog: "Moved"))
            .ConfigureAwait(true);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, $"{saved.StatusCode}: {app.ErrorsText}");

        var fresh = (await BodyAsync(saved, null).ConfigureAwait(true)).GetProperty("rowVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fresh));
        Assert.NotEqual(version, fresh);
        Assert.Equal(fresh, await VersionAsync(client, id).ConfigureAwait(true));

        var removed = await SendAsync(client, HttpMethod.Delete, id, fresh, body: null).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Застаріла_версія_дає_409_на_зміні_й_видаленні_і_рядок_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        var (id, code) = await AddInactiveAsync().ConfigureAwait(true);
        var stale = await VersionAsync(client, id).ConfigureAwait(true);

        // Хтось інший зберіг правку раніше — версія рядка вже не та.
        var first = await SendAsync(client, HttpMethod.Put, id, stale, Body(code, isActive: false, catalog: "Moved"))
            .ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {app.ErrorsText}");

        HttpResponseMessage[] conflicts =
        [
            await SendAsync(client, HttpMethod.Put, id, stale, Body(code, isActive: false, catalog: "Lost"))
                .ConfigureAwait(true),
            await SendAsync(client, HttpMethod.Delete, id, stale, body: null).ConfigureAwait(true),
        ];

        foreach (var conflict in conflicts)
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var problem = await BodyAsync(conflict, null).ConfigureAwait(true);
            Assert.Equal("ECR-JOB-0409", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-JOB-0409.dataSourceChanged", problem.GetProperty("messageKey").GetString());
        }

        // Рядок той, що зберіг перший; видалення теж не відбулося.
        await using var db = NewDb();
        var stored = await db.DataSources.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
        Assert.Equal("Moved", stored.Catalog);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Без_If_Match_зміна_й_видалення_дають_422_і_рядок_лишається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        var (id, code) = await AddInactiveAsync().ConfigureAwait(true);

        HttpResponseMessage[] refusals =
        [
            await SendAsync(client, HttpMethod.Put, id, rowVersion: null, Body(code, isActive: false, catalog: "Lost"))
                .ConfigureAwait(true),
            await SendAsync(client, HttpMethod.Delete, id, rowVersion: null, body: null).ConfigureAwait(true),
        ];

        foreach (var refusal in refusals)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refusal.StatusCode);
            var problem = await BodyAsync(refusal, null).ConfigureAwait(true);
            Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-REQ-0422.dataSourceIfMatch", problem.GetProperty("messageKey").GetString());
        }

        await using var db = NewDb();
        var stored = await db.DataSources.AsNoTracking().SingleAsync(s => s.Id == id).ConfigureAwait(true);
        Assert.Equal("EcrDb", stored.Catalog);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-21")]
    public async Task Облікові_дані_в_резервній_адресі_відмова_називає_поле_secondaryEndpoint()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.Manage", "Integration.View").ConfigureAwait(true);

        var code = $"SP{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var refused = await client.PostAsJsonAsync(
            Sources,
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "PI AF" },
                transport = "PiWebApi",
                endpoint = Endpoint,
                secondaryEndpoint = "https://svc:hunter2@pi2.corp.example/piwebapi",
                isActive = false,
            }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await BodyAsync(refused, null).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-REQ-0422.dataSourceEndpointCarriesSecret", problem.GetProperty("messageKey").GetString());
        Assert.Equal("secondaryEndpoint", problem.GetProperty("field").GetString());

        await using var db = NewDb();
        Assert.False(await db.DataSources.AnyAsync(s => s.Code == code).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.13")]
    public async Task Каталог_джерела_відкриває_Manage_читає_адаптером_а_вимкнене_джерело_503()
    {
        using var app = new EcrApiFactory(sql);
        var (id, _) = await AddInactiveAsync().ConfigureAwait(true);

        using var client = await SignedInAsync(app, "Integration.Manage").ConfigureAwait(true);

        // Вимкнене джерело каталогу не віддає: відмова джерела, а не 500.
        var down = await client.GetAsync(At($"{id}/catalog")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);

        var problem = await BodyAsync(down, null).ConfigureAwait(true);
        Assert.Equal("ECR-INT-0503", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-INT-0503.catalogUnavailable", problem.GetProperty("messageKey").GetString());

        // Увімкнене — читається підміненим транспортом PI Web API; потім знову
        // вимикається, щоб не робити `/health/ready` «Degraded» решті прогону.
        await SetActiveAsync(id, true).ConfigureAwait(true);

        try
        {
            var listed = await client.GetAsync(At($"{id}/catalog?limit=10")).ConfigureAwait(true);
            Assert.True(listed.StatusCode == HttpStatusCode.OK, $"{listed.StatusCode}: {app.ErrorsText}");

            var page = await BodyAsync(listed, null).ConfigureAwait(true);
            var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal("Unit-01", item.GetProperty("code").GetString());
            Assert.Equal("Element", item.GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        }
        finally
        {
            await SetActiveAsync(id, false).ConfigureAwait(true);
        }

        using var viewer = await SignedInAsync(app, "Integration.View").ConfigureAwait(true);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await viewer.GetAsync(At($"{id}/catalog")).ConfigureAwait(true)).StatusCode);
    }

    private async Task SetActiveAsync(int id, bool active)
    {
        await using var db = NewDb();
        await db.DataSources.Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, active)).ConfigureAwait(false);
    }

    /// <summary>
    /// Вимкнене джерело прямо в базі: активне без відповіді зробило б
    /// <c>/health/ready</c> «Degraded» для решти прогону.
    /// </summary>
    private async Task<(int Id, string Code)> AddInactiveAsync()
    {
        var code = $"RV{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        await using var db = NewDb();
        var source = new DataSource(
            Ecr.Domain.ValueObjects.EcrCode.Create(code),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
            ExternalTransport.PiWebApi, Endpoint, $"DataSource.{code}");
        source.Configure(null, "EcrDb", 4);
        source.Deactivate();
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (source.Id, code);
    }

    private static async Task<string?> VersionAsync(HttpClient client, int id)
    {
        var listed = await BodyAsync(await client.GetAsync(Sources).ConfigureAwait(false), null).ConfigureAwait(false);

        return listed.EnumerateArray()
            .Single(s => s.GetProperty("id").GetInt32() == id)
            .GetProperty("rowVersion").GetString();
    }

    /// <summary>Запит із <c>If-Match</c> у лапках — так, як його шле клієнт; <c>null</c> — без заголовка.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, int id, string? rowVersion, object? body)
    {
        using var request = new HttpRequestMessage(method, At(id.ToString(CultureInfo.InvariantCulture)));

        if (rowVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static object Body(string code, bool isActive = true, string catalog = "EcrDb") => new
    {
        code,
        nameL10n = new Dictionary<string, string> { ["en"] = "PI AF primary" },
        transport = "PiWebApi",
        endpoint = Endpoint,
        catalog,
        maxParallel = 4,
        isActive,
    };

    private static Uri At(string tail) => new($"{Sources}/{tail}", UriKind.Relative);

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, StringBuilder? seen)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        seen?.Append(text);

        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>Клієнт із сеансом локального користувача з названими правами.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"src_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(
                Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Source test" }));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
