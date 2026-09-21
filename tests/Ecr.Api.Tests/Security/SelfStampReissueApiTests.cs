// tests/Ecr.Api.Tests/Security/SelfStampReissueApiTests.cs
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
/// Дія, що прокрутила штамп самого виконавця, не «викидає» його: у тій самій
/// відповіді він отримує нову cookie. Решта носіїв ролі гасне, як і раніше.
/// </summary>
/// <remarks>Фабрика — з <c>StampCacheSeconds=0</c> (типове для <see cref="EcrApiFactory"/>).</remarks>
[Collection("SqlServer")]
public sealed class SelfStampReissueApiTests(SqlServerFixture sql)
{
    private const string Password = "Karachaganak-2026-Spring!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Гранти_власної_ролі_не_обривають_сесію_виконавця_але_обривають_інших_носіїв()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var admin = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        using var member = await SignedInAsync(app, $"mem_{_tag}").ConfigureAwait(true);

        var put = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/roles/{ids.ManageRoles}/grants", UriKind.Relative),
            new { grants = Array.Empty<object>() }).ConfigureAwait(true);
        Assert.True(put.StatusCode == HttpStatusCode.NoContent, $"{put.StatusCode}: {app.ErrorsText}");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/me", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync(new Uri("/api/v1/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_cookie_несе_вже_звужені_права_403_а_не_401()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var admin = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        var grants = new Uri($"/api/v1/roles/{ids.ManageRoles}/grants", UriKind.Relative);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(grants)).StatusCode);

        // Знімає з себе роль із Security.ManageRoles, лишаючи Security.ManageUsers.
        var put = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/users/{ids.Admin}/roles", UriKind.Relative),
            new { roleCodes = new[] { ids.ManageUsersCode } }).ConfigureAwait(true);
        Assert.True(put.IsSuccessStatusCode, $"{put.StatusCode}: {app.ErrorsText}");

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(grants)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/api/v1/me", UriKind.Relative))).StatusCode);
    }

    private async Task<(int Admin, int ManageRoles, string ManageUsersCode)> ArrangeAsync()
    {
        await using var db = CreateContext();

        Role NewRole(string code)
        {
            var role = new Role(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
            db.Roles.Add(role);
            return role;
        }

        var roles = NewRole($"SRR_{_tag}");
        var usersCode = $"SRU_{_tag}";
        var users = NewRole(usersCode);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(roles.Id, "Security.ManageRoles"));
        db.RolePermissions.Add(new RolePermission(users.Id, "Security.ManageUsers"));

        var hash = new PasswordHasher().Hash(Password);
        User Local(string prefix)
        {
            var user = new User($"{prefix}_{_tag}", prefix, AuthProvider.Local);
            user.SetPassword(hash);
            db.Users.Add(user);
            return user;
        }

        var admin = Local("adm");
        var member = Local("mem");
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(roles.Id, admin.Id, principalSid: null));
        db.RoleAssignments.Add(new RoleAssignment(users.Id, admin.Id, principalSid: null));
        db.RoleAssignments.Add(new RoleAssignment(roles.Id, member.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (admin.Id, roles.Id, usersCode);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{userName}: {response.StatusCode} {app.ErrorsText}");
        return client;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
