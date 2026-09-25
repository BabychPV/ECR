// tests/Ecr.Api.Tests/Security/RoleInApprovalRouteDeleteTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// V-09 (UX-прохід, третій раунд): роль, що стоїть ЛИШЕ кроком маршруту
/// погодження, видалялася (<c>DELETE /roles/{id}</c> → 204) — зовнішнього
/// ключа на роль у <c>wf.ApprovalStep</c> немає, а лічильник використання
/// бачив тільки призначення й гранти. Маршрут лишався з кроком на неіснуючу
/// роль. Наскрізно: справжній SQL, вхід, HTTP.
/// </summary>
[Collection("SqlServer")]
public sealed class RoleInApprovalRouteDeleteTests(SqlServerFixture sql)
{
    private const string Password = "Api-Role-Route-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_лише_в_маршруті_погодження_дає_409_з_лічильником_кроків()
    {
        var roleId = await ArrangeAsync(inRoute: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var response = await client
            .DeleteAsync(new Uri($"/api/v1/roles/{roleId}", UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-SEC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("0", problem.GetProperty("assignments").GetString());
        Assert.Equal("0", problem.GetProperty("grants").GetString());
        Assert.Equal("1", problem.GetProperty("approvalSteps").GetString());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Roles.AnyAsync(r => r.Id == roleId).ConfigureAwait(true), "Роль мала лишитися.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_без_жодного_використання_видаляється()
    {
        var roleId = await ArrangeAsync(inRoute: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var response = await client
            .DeleteAsync(new Uri($"/api/v1/roles/{roleId}", UriKind.Relative)).ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {app.ErrorsText}");
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"rrd_{_tag}", password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Адміністратор ролей і роль-жертва; <paramref name="inRoute"/> — крок маршруту на неї.</summary>
    private async Task<int> ArrangeAsync(bool inRoute)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // ⚠ Маршрут — на СВІЙ проєкт: глобальний маршрут один на базу, і
        // повторний прогін уперся б в унікальність, а не в перевірку.
        var projectId = inRoute ? (await builder.BuildAsync().ConfigureAwait(false)).ProjectId : (int?)null;

        await using var db = builder.CreateContext();

        var admin = new User($"rrd_{_tag}", $"rrd_{_tag}", AuthProvider.Local);
        admin.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(admin);

        var manager = NewRole($"RRD_M_{_tag}");
        var victim = NewRole($"RRD_V_{_tag}");
        db.Roles.Add(manager);
        db.Roles.Add(victim);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(manager.Id, "Security.ManageRoles"));
        db.RoleAssignments.Add(new RoleAssignment(manager.Id, admin.Id, null));

        if (inRoute)
        {
            var route = new ApprovalRoute(
                EcrCode.Create($"RRD_R_{_tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Route" }),
                projectId);
            route.AddStep(victim.Id);
            db.ApprovalRoutes.Add(route);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return victim.Id;
    }

    private static Role NewRole(string code)
        => new(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
}
