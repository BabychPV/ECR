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

        var id = (await BodyAsync(created, null).ConfigureAwait(true)).GetProperty("id").GetInt32();

        int entityId;
        await using (var arrange = NewDb())
        {
            var entity = new SourceEntity(id, $"ENT-{code}", RegistrySourceKind.External);
            arrange.SourceEntities.Add(entity);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
            entityId = entity.Id;
        }

        var refused = await client.DeleteAsync(At(id.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(true);

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

        var removed = await client.DeleteAsync(At(id.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(true);
        Assert.True(removed.StatusCode == HttpStatusCode.NoContent, $"{removed.StatusCode}: {app.ErrorsText}");

        await using var db = NewDb();
        Assert.False(await db.DataSources.AnyAsync(s => s.Id == id).ConfigureAwait(true));
    }

    private static object Body(string code) => new
    {
        code,
        nameL10n = new Dictionary<string, string> { ["en"] = "PI AF primary" },
        transport = "PiWebApi",
        endpoint = Endpoint,
        catalog = "EcrDb",
        maxParallel = 4,
        isActive = true,
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
