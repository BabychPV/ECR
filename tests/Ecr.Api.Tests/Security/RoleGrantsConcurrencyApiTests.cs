// tests/Ecr.Api.Tests/Security/RoleGrantsConcurrencyApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Втрачена правка грантів ролі: два адміністратори, одна роль.
/// </summary>
/// <remarks>
/// ⛔ До цієї правки <c>PUT /roles/{id}/grants</c> не звіряв нічого: другий
/// адміністратор, який відкрив гранти до збереження першого, мовчки затирав
/// його набір. Тепер <c>GET</c> віддає версію набору в <c>ETag</c>, а
/// <c>PUT</c> із застарілим <c>If-Match</c> отримує <c>409 ECR-SEC-0409</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RoleGrantsConcurrencyApiTests(SqlServerFixture sql)
{
    private const string Password = "Karachaganak-2026-Spring!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Друге_збереження_зі_старою_версією_дає_409_а_не_перезапис()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var a = await SignedInAsync(app, $"ga_{_tag}").ConfigureAwait(true);
        using var b = await SignedInAsync(app, $"gb_{_tag}").ConfigureAwait(true);
        var grants = Grants(ids.Target);

        var versionA = await ReadVersionAsync(a, grants).ConfigureAwait(true);
        var versionB = await ReadVersionAsync(b, grants).ConfigureAwait(true);
        Assert.Equal(versionA, versionB);

        var putA = await PutAsync(a, grants, versionA, Project(900_001, "Read")).ConfigureAwait(true);
        Assert.True(putA.StatusCode == HttpStatusCode.NoContent, $"{putA.StatusCode}: {app.ErrorsText}");
        Assert.NotEqual(versionA, putA.Headers.ETag?.Tag);

        var putB = await PutAsync(b, grants, versionB, Project(900_002, "Write")).ConfigureAwait(true);
        var body = await putB.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(putB.StatusCode == HttpStatusCode.Conflict, $"{putB.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("ECR-SEC-0409", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());

        // Набір — рівно як у A: правку A ніхто не затер.
        var afterConflict = await ListAsync(a, grants).ConfigureAwait(true);
        Assert.Equal(900_001, Assert.Single(afterConflict).ResourceId);
        Assert.Equal(GrantLevel.Read, afterConflict[0].Level);

        // B перечитує — і тоді зберігає.
        var fresh = await ReadVersionAsync(b, grants).ConfigureAwait(true);
        Assert.Equal(putA.Headers.ETag?.Tag, fresh);
        var retry = await PutAsync(b, grants, fresh, Project(900_002, "Write")).ConfigureAwait(true);
        Assert.True(retry.StatusCode == HttpStatusCode.NoContent, $"{retry.StatusCode}: {app.ErrorsText}");

        var final = await ListAsync(a, grants).ConfigureAwait(true);
        Assert.Equal(900_002, Assert.Single(final).ResourceId);

        // Відхилена спроба не лишила події в журналі: рівно дві заміни.
        Assert.Equal(2, await GrantEventsAsync(ids.Target).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_одночасні_заміни_з_однієї_версії_успішна_рівно_одна()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        var gate = new ReadGate();
        using var baseApp = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(sp => GatedUserStore.Create(
                new UserStore(sp.GetRequiredService<EcrDbContext>()), gate))));
        using var a = await SignedInAsync(app, $"ga_{_tag}").ConfigureAwait(true);
        using var b = await SignedInAsync(app, $"gb_{_tag}").ConfigureAwait(true);
        var grants = Grants(ids.Target);

        var version = await ReadVersionAsync(a, grants).ConfigureAwait(true);

        // ⚠ Шлюз тримає перше прочитане ВСЕРЕДИНІ транзакції (див. ReadGate):
        // без блокування обидві заміни прочитали б стару версію й пройшли
        // звірку обидві. Під UPDLOCK друга до читання не доходить, перша
        // відпускається за тайм-аутом.
        gate.Arm();
        var responses = await Task.WhenAll(
            PutAsync(a, grants, version, Project(900_011, "Read")),
            PutAsync(b, grants, version, Project(900_012, "Write"))).ConfigureAwait(true);

        var codes = responses.Select(r => r.StatusCode).OrderBy(c => (int)c).ToList();
        Assert.True(
            codes.SequenceEqual([HttpStatusCode.NoContent, HttpStatusCode.Conflict]),
            $"{string.Join(", ", codes)}\n{baseApp.ErrorsText}");

        var winner = responses[0].StatusCode == HttpStatusCode.NoContent ? 900_011 : 900_012;
        var final = await ListAsync(a, grants).ConfigureAwait(true);
        Assert.Equal(winner, Assert.Single(final).ResourceId);
        Assert.Equal(1, await GrantEventsAsync(ids.Target).ConfigureAwait(true));
    }

    /// <remarks>
    /// ⛔ Створення проєкту видає грант власності КОЖНІЙ ролі творця з
    /// <c>Project.Manage</c> — читанням набору ролі й заміною. Доти без
    /// блокування і без транзакції: паралельна правка грантів тієї самої ролі
    /// або затиралася грантом власності, або затирала його.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_проєкту_і_правка_грантів_тієї_самої_ролі_нічого_не_губить()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        var (templateVersionId, creatorName) = await ArrangeCreatorAsync(ids.Target).ConfigureAwait(true);
        var gate = new ReadGate();
        using var baseApp = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(sp => GatedUserStore.Create(
                new UserStore(sp.GetRequiredService<EcrDbContext>()), gate))));
        using var admin = await SignedInAsync(app, $"ga_{_tag}").ConfigureAwait(true);
        using var creator = await SignedInAsync(app, creatorName).ConfigureAwait(true);
        var grants = Grants(ids.Target);

        var version = await ReadVersionAsync(admin, grants).ConfigureAwait(true);

        gate.Arm();
        var create = creator.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code = $"GCP{_tag}",
                nameL10n = new Dictionary<string, string> { ["en"] = "Grant race" },
                timeZoneId = "Asia/Atyrau",
                periodKind = "Monthly",
                year = 2026,
                templateVersionId,
                periodPolicyId = 1,
            });
        var put = PutAsync(admin, grants, version, Project(900_031, "Read"));
        await Task.WhenAll(create, put).ConfigureAwait(true);

        var created = await create.ConfigureAwait(true);
        var createdBody = await created.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {createdBody}\n{baseApp.ErrorsText}");
        var projectId = JsonDocument.Parse(createdBody).RootElement.GetProperty("projectId").GetInt32();

        var putStatus = (await put.ConfigureAwait(true)).StatusCode;
        Assert.True(
            putStatus is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
            $"{putStatus}\n{baseApp.ErrorsText}");

        var final = await ListAsync(admin, grants).ConfigureAwait(true);
        var ids2 = string.Join(", ", final.Select(g => $"{g.ResourceKind}:{g.ResourceId}:{g.Level}"));

        // Грант власності не загубився ніколи; правка адміністратора — або
        // лягла поруч (204), або відмовлена чесно (409), а не мовчки затерта.
        Assert.True(
            final.Any(g => g.ResourceKind == ResourceKind.Project && g.ResourceId == projectId && g.Level == GrantLevel.Manage),
            $"PUT {putStatus}; немає гранта власності на {projectId}: [{ids2}]");
        Assert.True(
            putStatus == HttpStatusCode.Conflict || final.Any(g => g.ResourceId == 900_031),
            $"PUT 204, але його гранта немає: [{ids2}]");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_If_Match_422_з_ключем_і_набір_не_змінюється()
    {
        // ⛔ Перехідний режим знято разом з оновленням екрана (U6a): запит без
        // заголовка — запит того, хто набору не читав, і «останній перемагає»
        // для нього більше не діє.
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var a = await SignedInAsync(app, $"ga_{_tag}").ConfigureAwait(true);
        var grants = Grants(ids.Target);

        var put = await PutAsync(a, grants, ifMatch: null, Project(900_021, "Read")).ConfigureAwait(true);
        var body = await put.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{put.StatusCode}: {body}\n{app.ErrorsText}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REQ-0422", root.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REQ-0422.roleGrantsIfMatch", root.GetProperty("messageKey").GetString());
        Assert.Empty(await ListAsync(a, grants).ConfigureAwait(true));
        Assert.Equal(0, await GrantEventsAsync(ids.Target).ConfigureAwait(true));
    }

    private static Uri Grants(int roleId) => new($"/api/v1/roles/{roleId}/grants", UriKind.Relative);

    private static object Project(int id, string level)
        => new { resourceKind = "Project", resourceId = id, level, isDeny = false };

    private static async Task<string> ReadVersionAsync(HttpClient client, Uri grants)
    {
        using var response = await client.GetAsync(grants).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tag = response.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(tag), "GET грантів ролі не віддав ETag.");
        return tag!;
    }

    private static async Task<List<ResourceGrantDto>> ListAsync(HttpClient client, Uri grants)
    {
        using var response = await client.GetAsync(grants).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ResourceGrantDto>>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }).ConfigureAwait(false))!;
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, Uri grants, string? ifMatch, params object[] set)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, grants)
        {
            Content = JsonContent.Create(new { grants = set }),
        };

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private async Task<int> GrantEventsAsync(int roleId)
    {
        await using var db = CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = 'ResourceGrantsReplaced' AND TargetRoleId = {roleId}")
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<(int Manager, int Target)> ArrangeAsync()
    {
        await using var db = CreateContext();

        Role NewRole(string code)
        {
            var role = new Role(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
            db.Roles.Add(role);
            return role;
        }

        var manager = NewRole($"GCM_{_tag}");
        var target = NewRole($"GCT_{_tag}");
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(manager.Id, ListResourceGrantsHandler.Permission));

        var hash = new PasswordHasher().Hash(Password);
        User Local(string prefix)
        {
            var user = new User($"{prefix}_{_tag}", prefix, AuthProvider.Local);
            user.SetPassword(hash);
            db.Users.Add(user);
            return user;
        }

        var first = Local("ga");
        var second = Local("gb");
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(manager.Id, first.Id, principalSid: null));
        db.RoleAssignments.Add(new RoleAssignment(manager.Id, second.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (manager.Id, target.Id);
    }

    /// <summary>
    /// Творець проєктів: носій цільової ролі, якій додається <c>Project.Manage</c>;
    /// плюс чернеткова версія шаблону для створення.
    /// </summary>
    private async Task<(int TemplateVersionId, string CreatorName)> ArrangeCreatorAsync(int targetRoleId)
    {
        await using var db = CreateContext();
        var now = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var template = new Template(
            EcrCode.Create($"GCT{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Grant race" }), createdByUserId: 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, now);
        db.TemplateVersions.Add(version);

        db.RolePermissions.Add(new RolePermission(targetRoleId, "Project.Manage"));

        var creator = new User($"gc_{_tag}", "gc", AuthProvider.Local);
        creator.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(creator);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(targetRoleId, creator.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (version.Id, creator.UserName);
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> app, string userName)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{userName}: {response.StatusCode}");
        return client;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>
    /// Тримає ПЕРШЕ прочитане значення набору, доки не прийде друге читання
    /// (або 3 с), і ще 1 с після того; друге проходить одразу.
    /// </summary>
    /// <remarks>
    /// ⚠ Шлюз стоїть ПІСЛЯ читання: перший учасник тримає вже прочитаний
    /// (можливо, застарілий) набір, а другий за цю секунду встигає
    /// зафіксуватися. Без блокування це рівно той інтерлівінг, у якому перший
    /// затирає другого; під UPDLOCK другий до читання не доходить, доки
    /// перший не закомітиться.
    /// </remarks>
    private sealed class ReadGate : IReadGate
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _arrived;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public async Task PassAsync()
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            if (Interlocked.Increment(ref _arrived) >= 2)
            {
                _both.TrySetResult();
                return;
            }

            await Task.WhenAny(_both.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

}

/// <summary>Шлюз для <see cref="GatedUserStore"/>.</summary>
public interface IReadGate
{
    /// <summary>Пропускає читання — одразу або після очікування.</summary>
    public Task PassAsync();
}

/// <summary>
/// Справжнє сховище, у якого кожне <c>ListGrantsAsync</c> спершу проходить шлюз.
/// </summary>
/// <remarks>
/// ⚠ <see cref="DispatchProxy"/>, а не ручний декоратор: <see cref="IUserStore"/>
/// має кілька десятків членів, і ручне переадресування застаріло б від першого ж
/// нового методу порту.
/// </remarks>
public class GatedUserStore : DispatchProxy
{
    private IUserStore _inner = null!;
    private IReadGate _gate = null!;

    /// <summary>Будує проксі над <paramref name="inner"/>.</summary>
    /// <param name="inner">Справжнє сховище.</param>
    /// <param name="gate">Шлюз.</param>
    public static IUserStore Create(IUserStore inner, IReadGate gate)
    {
        var proxy = Create<IUserStore, GatedUserStore>();
        var self = (GatedUserStore)(object)proxy;
        self._inner = inner;
        self._gate = gate;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name == nameof(IUserStore.ListGrantsAsync))
        {
            return GatedAsync((int)args![0]!, (CancellationToken)args[1]!);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private async Task<IReadOnlyList<ResourceGrantDto>> GatedAsync(int roleId, CancellationToken ct)
    {
        var grants = await _inner.ListGrantsAsync(roleId, ct).ConfigureAwait(false);
        await _gate.PassAsync().ConfigureAwait(false);
        return grants;
    }
}
