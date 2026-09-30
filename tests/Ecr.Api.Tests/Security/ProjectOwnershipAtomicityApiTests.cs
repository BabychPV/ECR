// tests/Ecr.Api.Tests/Security/ProjectOwnershipAtomicityApiTests.cs
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
/// Проєкт і грант власності на нього — один коміт (створення і клонування).
/// </summary>
/// <remarks>
/// ⛔ До цієї правки проєкт комітився однією транзакцією, а грант власності
/// (<c>ProjectOwnershipGrant</c>) — другою. Збій на другій лишав закомічений
/// проєкт без жодного гранта: його не бачив ніхто, крім глобальних прав, і
/// його не можна було навіть відкрити, щоб видалити. Збій тут підставляється
/// шовом над справжнім сховищем: <c>ReplaceGrantsAsync</c> відпрацьовує і
/// кидає — тобто ПІСЛЯ запису проєкту (і аудиту клону) в тій самій обробці.
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectOwnershipAtomicityApiTests(SqlServerFixture sql)
{
    private const string Password = "Karachaganak-2026-Spring!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_на_гранті_власності_не_лишає_створеного_проєкту()
    {
        var arranged = await ArrangeAsync().ConfigureAwait(true);
        var fault = new GrantFault();
        using var baseApp = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var app = WithFault(baseApp, fault);
        using var creator = await SignedInAsync(app, arranged.CreatorName).ConfigureAwait(true);
        var code = $"PAC{_tag}";

        fault.Arm();
        using var response = await CreateAsync(creator, code, arranged.TemplateVersionId).ConfigureAwait(true);

        Assert.False(response.IsSuccessStatusCode, $"{response.StatusCode}: збій гранта не дійшов до відповіді");
        Assert.Equal(1, fault.Thrown);

        Assert.Equal(0, await ProjectCountAsync(code).ConfigureAwait(true));
        Assert.Equal(0, await GrantCountAsync(arranged.RoleId).ConfigureAwait(true));
        Assert.Equal(0, await SecurityEventCountAsync(arranged.RoleId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_проєкту_дає_проєкт_грант_власності_і_подію_аудиту()
    {
        var arranged = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var creator = await SignedInAsync(app, arranged.CreatorName).ConfigureAwait(true);
        var code = $"PAN{_tag}";

        using var response = await CreateAsync(creator, code, arranged.TemplateVersionId).ConfigureAwait(true);
        var projectId = await CreatedIdAsync(response, app).ConfigureAwait(true);

        Assert.Equal(1, await ProjectCountAsync(code).ConfigureAwait(true));
        Assert.True(await HasOwnershipGrantAsync(arranged.RoleId, projectId).ConfigureAwait(true));
        Assert.Equal(1, await SecurityEventCountAsync(arranged.RoleId, "CreateProjectOwnership", projectId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_на_гранті_власності_не_лишає_клону_ні_його_аудиту()
    {
        var arranged = await ArrangeAsync().ConfigureAwait(true);
        var fault = new GrantFault();
        using var baseApp = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var app = WithFault(baseApp, fault);
        using var creator = await SignedInAsync(app, arranged.CreatorName).ConfigureAwait(true);

        using var source = await CreateAsync(creator, $"PAS{_tag}", arranged.TemplateVersionId).ConfigureAwait(true);
        var sourceId = await CreatedIdAsync(source, baseApp).ConfigureAwait(true);
        var eventsBefore = await SecurityEventCountAsync(arranged.RoleId).ConfigureAwait(true);
        var cloneCode = $"PAK{_tag}";

        fault.Arm();
        using var response = await CloneAsync(creator, sourceId, cloneCode).ConfigureAwait(true);

        Assert.False(response.IsSuccessStatusCode, $"{response.StatusCode}: збій гранта не дійшов до відповіді");
        Assert.Equal(1, fault.Thrown);

        Assert.Equal(0, await ProjectCountAsync(cloneCode).ConfigureAwait(true));
        Assert.Equal(0, await CloneAuditCountAsync(sourceId).ConfigureAwait(true));
        Assert.Equal(eventsBefore, await SecurityEventCountAsync(arranged.RoleId).ConfigureAwait(true));

        // Грант на джерело — на місці, зайвого (на неіснуючий клон) немає.
        Assert.Equal(1, await GrantCountAsync(arranged.RoleId).ConfigureAwait(true));
        Assert.True(await HasOwnershipGrantAsync(arranged.RoleId, sourceId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клонування_дає_клон_грант_власності_і_обидва_аудити()
    {
        var arranged = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var creator = await SignedInAsync(app, arranged.CreatorName).ConfigureAwait(true);

        using var source = await CreateAsync(creator, $"PAT{_tag}", arranged.TemplateVersionId).ConfigureAwait(true);
        var sourceId = await CreatedIdAsync(source, app).ConfigureAwait(true);
        var cloneCode = $"PAL{_tag}";

        using var response = await CloneAsync(creator, sourceId, cloneCode).ConfigureAwait(true);
        var cloneId = await CreatedIdAsync(response, app).ConfigureAwait(true);

        Assert.Equal(1, await ProjectCountAsync(cloneCode).ConfigureAwait(true));
        Assert.True(await HasOwnershipGrantAsync(arranged.RoleId, cloneId).ConfigureAwait(true));
        Assert.Equal(1, await CloneAuditCountAsync(sourceId).ConfigureAwait(true));
        Assert.Equal(1, await SecurityEventCountAsync(arranged.RoleId, "CloneProjectOwnership", cloneId).ConfigureAwait(true));
    }

    private static WebApplicationFactory<Program> WithFault(EcrApiFactory baseApp, GrantFault fault)
        => baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped(sp => FaultingUserStore.Create(
                new UserStore(sp.GetRequiredService<EcrDbContext>()), fault))));

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string code, int templateVersionId)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "Ownership atomicity" },
                timeZoneId = "Asia/Atyrau",
                periodKind = "Monthly",
                year = 2026,
                templateVersionId,
                periodPolicyId = 1,
            });

    private static Task<HttpResponseMessage> CloneAsync(HttpClient client, int sourceId, string code)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/projects/{sourceId}/clone", UriKind.Relative), new { code });

    private static async Task<int> CreatedIdAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.GetProperty("projectId").GetInt32();
    }

    private async Task<int> ProjectCountAsync(string code)
    {
        await using var db = CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM doc.Project WHERE Code = {code}")
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> GrantCountAsync(int roleId)
    {
        await using var db = CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sec.ResourceGrant WHERE RoleId = {roleId}")
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<bool> HasOwnershipGrantAsync(int roleId, int projectId)
    {
        await using var db = CreateContext();
        return await db.ResourceGrants
            .AnyAsync(g => g.RoleId == roleId && g.ResourceKind == ResourceKind.Project
                           && g.ResourceId == projectId && g.Level == GrantLevel.Manage && !g.IsDeny)
            .ConfigureAwait(false);
    }

    private async Task<int> SecurityEventCountAsync(int roleId)
    {
        await using var db = CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = 'ResourceGrantsReplaced' AND TargetRoleId = {roleId}")
            .SingleAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> SecurityEventCountAsync(int roleId, string reason, int projectId)
    {
        await using var db = CreateContext();
        var details = await db.Database
            .SqlQuery<string>($"SELECT DetailsJson AS [Value] FROM aud.SecurityEvent WHERE EventType = 'ResourceGrantsReplaced' AND TargetRoleId = {roleId}")
            .ToListAsync()
            .ConfigureAwait(false);

        return details.Count(d =>
        {
            var root = JsonDocument.Parse(d).RootElement;
            return root.TryGetProperty("reason", out var r) && r.GetString() == reason
                && root.TryGetProperty("projectId", out var p) && p.GetInt32() == projectId;
        });
    }

    private async Task<int> CloneAuditCountAsync(int sourceId)
    {
        await using var db = CreateContext();
        var pattern = $"%\"sourceProjectId\":{sourceId},%";
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE EntityType = 'Project' AND Operation = 'Clone' AND OldJson LIKE {pattern}")
            .SingleAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Роль із <c>Project.Manage</c> і лише одним носієм — творцем; плюс
    /// версія шаблону для створення.
    /// </summary>
    private async Task<(int RoleId, string CreatorName, int TemplateVersionId)> ArrangeAsync()
    {
        await using var db = CreateContext();
        var now = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var role = new Role(
            EcrCode.Create($"PAR_{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Ownership" }));
        db.Roles.Add(role);

        var template = new Template(
            EcrCode.Create($"PAT{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Ownership" }), createdByUserId: 1, now);
        db.Templates.Add(template);

        var creator = new User($"pa_{_tag}", "pa", AuthProvider.Local);
        creator.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(creator);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, now);
        db.TemplateVersions.Add(version);
        db.RolePermissions.Add(new RolePermission(role.Id, "Project.Manage"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, creator.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (role.Id, creator.UserName, version.Id);
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
}

/// <summary>Перемикач збою для <see cref="FaultingUserStore"/>.</summary>
public sealed class GrantFault
{
    private int _armed;
    private int _thrown;

    /// <summary>Скільки разів збій спрацював.</summary>
    public int Thrown => Volatile.Read(ref _thrown);

    /// <summary>Наступна заміна грантів кине (рівно один раз).</summary>
    public void Arm() => Volatile.Write(ref _armed, 1);

    /// <summary>Чи кидати зараз; знімає взвід.</summary>
    public bool Fire()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return false;
        }

        Interlocked.Increment(ref _thrown);
        return true;
    }
}

/// <summary>
/// Справжнє сховище, у якого взведений <c>ReplaceGrantsAsync</c> відпрацьовує
/// і кидає.
/// </summary>
/// <remarks>
/// ⚠ <see cref="DispatchProxy"/>, як і <c>GatedUserStore</c>: порт має кілька
/// десятків членів, і ручний декоратор застарів би від першого нового методу.
/// </remarks>
public class FaultingUserStore : DispatchProxy
{
    private IUserStore _inner = null!;
    private GrantFault _fault = null!;

    /// <summary>Будує проксі над <paramref name="inner"/>.</summary>
    /// <param name="inner">Справжнє сховище.</param>
    /// <param name="fault">Перемикач збою.</param>
    public static IUserStore Create(IUserStore inner, GrantFault fault)
    {
        var proxy = Create<IUserStore, FaultingUserStore>();
        var self = (FaultingUserStore)(object)proxy;
        self._inner = inner;
        self._fault = fault;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name == nameof(IUserStore.ReplaceGrantsAsync))
        {
            return ReplaceThenFailAsync(
                (int)args![0]!, (IReadOnlyList<ResourceGrantDto>)args[1]!, (CancellationToken)args[2]!);
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

    private async Task ReplaceThenFailAsync(int roleId, IReadOnlyList<ResourceGrantDto> grants, CancellationToken ct)
    {
        await _inner.ReplaceGrantsAsync(roleId, grants, ct).ConfigureAwait(false);

        if (_fault.Fire())
        {
            throw new InvalidOperationException("Тестовий збій видачі гранта власності.");
        }
    }
}
