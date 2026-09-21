// tests/Ecr.Api.Tests/Security/UserAdministrationApiTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Скидання пароля, блокування й розблокування через HTTP: статуси, обрив
/// чинних сесій і відсутність пароля у відповіді та логах (BE-12).
/// </summary>
[Collection("SqlServer")]
public sealed class UserAdministrationApiTests(SqlServerFixture sql)
{
    private const string Password = "Kashagan-2026-Winter!";
    private const string Temporary = "Tengiz-Temporary-7731";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-12")]
    public async Task Блокування_обриває_сесію_й_вхід_а_розблокування_повертає_доступ()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        using var target = await SignedInAsync(app, $"tgt_{_tag}").ConfigureAwait(true);
        using var plain = await SignedInAsync(app, $"pln_{_tag}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(plain, $"{ids.Target}/lock", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(admin, "2147483000/lock", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(admin, $"{ids.Target}/lock", new { reason = " " })).StatusCode);

        var self = await PostAsync(admin, $"{ids.Admin}/lock", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Contains("ECR-SEC-0409", await self.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var locked = await PostAsync(admin, $"{ids.Target}/lock", new { reason = "звільнений" });
        Assert.Equal(HttpStatusCode.NoContent, locked.StatusCode);

        // Чинна сесія гасне на наступному ж запиті, повторний вхід — 423.
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync(new Uri("/api/v1/me", UriKind.Relative))).StatusCode);
        using (var again = app.CreateClient())
        {
            var relogin = await LoginAsync(again, $"tgt_{_tag}", Password);
            Assert.Equal(HttpStatusCode.Locked, relogin.StatusCode);
            Assert.Contains("ECR-AUTH-0423", await relogin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(admin, $"{ids.Target}/unlock", new { reason = "повернувся" })).StatusCode);
        using var back = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(back, $"tgt_{_tag}", Password)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-12")]
    public async Task Скидання_дає_разовий_пароль_обриває_сесію_і_не_віддає_пароля_ніде()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        using var target = await SignedInAsync(app, $"tgt_{_tag}").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(admin, $"{ids.Domain}/reset-password", new { newPassword = Temporary })).StatusCode);
        var tooShort = await PostAsync(admin, $"{ids.Target}/reset-password", new { newPassword = "short" });
        Assert.Contains("ECR-PWD-0422", await tooShort.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var reset = await PostAsync(admin, $"{ids.Target}/reset-password", new { newPassword = Temporary });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Empty(await reset.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync(new Uri("/api/v1/me", UriKind.Relative))).StatusCode);

        using var fresh = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(fresh, $"tgt_{_tag}", Password)).StatusCode);
        var signIn = await LoginAsync(fresh, $"tgt_{_tag}", Temporary);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Contains("\"mustChangePassword\":true", await signIn.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ⛔ Пароля немає ні в логах сервера, ні в журналі безпеки (ФВ-6.11).
        Assert.DoesNotContain(app.ServerLog, line => line.Contains(Temporary, StringComparison.Ordinal));
        await using var db = CreateContext();
        var details = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS [Value] FROM aud.SecurityEvent WHERE TargetUserId = {ids.Target}")
            .ToListAsync().ConfigureAwait(true);
        Assert.NotEmpty(details);
        Assert.DoesNotContain(details, d => d.Contains(Temporary, StringComparison.Ordinal));
    }

    private async Task<(int Admin, int Target, int Domain)> ArrangeAsync()
    {
        await using var db = CreateContext();

        var role = new Role(EcrCode.Create($"UAD_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(role.Id, "Security.ManageUsers"));

        var hash = new PasswordHasher().Hash(Password);
        User Local(string prefix)
        {
            var user = new User($"{prefix}_{_tag}", prefix, AuthProvider.Local);
            user.SetPassword(hash);
            db.Users.Add(user);
            return user;
        }

        var admin = Local("adm");
        var target = Local("tgt");
        Local("pln");
        var domain = User.CreateDomain($"dom_{_tag}", "dom", $"S-1-5-21-{Random.Shared.Next(1, int.MaxValue)}", DateTime.UtcNow);
        db.Users.Add(domain);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, admin.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (admin.Id, target.Id, domain.Id);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var response = await LoginAsync(client, userName, Password).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{userName}: {response.StatusCode} {app.ErrorsText}");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
        => client.PostAsJsonAsync(new Uri($"/api/v1/users/{path}", UriKind.Relative), body);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password)
        => client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password });

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
