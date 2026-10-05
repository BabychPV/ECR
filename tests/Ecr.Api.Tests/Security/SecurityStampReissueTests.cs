// tests/Ecr.Api.Tests/Security/SecurityStampReissueTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text;
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
/// L1-01 (аудит 2026-10-03): cookie перевидається лише на ВЛАСНУ ротацію штампа.
/// Чужа ротація (блокування, скидання пароля, «вийти з усіх») — це відкликання.
/// </summary>
/// <remarks>
/// Фабрика — з <c>StampCacheSeconds=5</c>: саме в цьому вікні кешу викрадена
/// cookie встигала отримати нову.
/// </remarks>
[Collection("SqlServer")]
public sealed class SecurityStampReissueTests(SqlServerFixture sql)
{
    private const string Password = "Karachaganak-2026-Spring!";
    private const string CookieName = "ecr.auth";

    private static readonly Uri Me = new("/api/v1/me", UriKind.Relative);
    private static readonly Uri Theme = new("/api/v1/me/preferences/theme", UriKind.Relative);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чужа_ротація_штампа_не_перевидає_cookie()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 5);
        using var victim = await SignedInAsync(app, $"usr_{_tag}").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync(Me)).StatusCode); // кеш штампа прогрітий

        // «Вийти з усіх» / зміна пароля на іншому вузлі: штамп у БД новий, кеш цього вузла — старий.
        await using (var db = CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == ids.User).ConfigureAwait(true);
            user.RefreshSecurityStamp();
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var put = await victim.PutAsync(Theme, Json("\"dark\"")).ConfigureAwait(true);

        Assert.DoesNotContain(
            put.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)
                 && !c.StartsWith(CookieName + "=;", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Блокування_адміністратором_обриває_сесію_з_наступного_запиту_попри_кеш()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 5);
        using var admin = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        using var victim = await SignedInAsync(app, $"usr_{_tag}").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync(Me)).StatusCode);

        var locked = await admin.PostAsJsonAsync(
            new Uri($"/api/v1/users/{ids.User}/lock", UriKind.Relative), new { reason = "L1-01" }).ConfigureAwait(true);
        Assert.True(locked.StatusCode == HttpStatusCode.NoContent, $"{locked.StatusCode}: {app.ErrorsText}");

        using var put = await victim.PutAsync(Theme, Json("\"dark\"")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync(Me)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заблокований_адміністратором_запис_не_має_чинного_штампа()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 0);
        using var victim = await SignedInAsync(app, $"usr_{_tag}").ConfigureAwait(true);

        // Блокування БЕЗ ротації штампа: відкликання не має залежати від того, чи дійшла ротація.
        await using (var db = CreateContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE sec.[User] SET LockedUntil = {User.AdministrativeLockUntil} WHERE Id = {ids.User}").ConfigureAwait(true);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync(Me)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Власна_зміна_ролей_перевидає_cookie_і_з_кешем()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 5);
        using var admin = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Me)).StatusCode);

        using var put = await admin.PutAsJsonAsync(
            new Uri($"/api/v1/users/{ids.Admin}/roles", UriKind.Relative),
            new { roleCodes = new[] { ids.ManageUsersCode, ids.ExtraCode } }).ConfigureAwait(true);
        Assert.True(put.IsSuccessStatusCode, $"{put.StatusCode}: {app.ErrorsText}");
        Assert.Contains(
            put.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith(CookieName + "=", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Me)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкликана_cookie_не_перевидає_себе_власною_зміною_ролей()
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 5);
        using var stolen = await SignedInAsync(app, $"adm_{_tag}").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await stolen.GetAsync(Me)).StatusCode); // кеш тримає штамп A

        // Власник скинув пароль / «вийти з усіх» на іншому вузлі: A→B, кеш цього вузла — ще A.
        await using (var db = CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == ids.Admin).ConfigureAwait(true);
            user.RefreshSecurityStamp();
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        // Стара cookie проходить із кешу і сама крутить B→C зміною власних ролей.
        using var put = await stolen.PutAsJsonAsync(
            new Uri($"/api/v1/users/{ids.Admin}/roles", UriKind.Relative),
            new { roleCodes = new[] { ids.ManageUsersCode, ids.ExtraCode } }).ConfigureAwait(true);
        Assert.True(put.IsSuccessStatusCode, $"{put.StatusCode}: {app.ErrorsText}");

        Assert.DoesNotContain(
            put.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)
                 && !c.StartsWith(CookieName + "=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync(Me)).StatusCode);
    }

    private async Task<(int Admin, int User, string ManageUsersCode, string ExtraCode)> ArrangeAsync()
    {
        await using var db = CreateContext();

        Role NewRole(string code)
        {
            var role = new Role(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
            db.Roles.Add(role);
            return role;
        }

        var manageUsersCode = $"SSU_{_tag}";
        var extraCode = $"SSX_{_tag}";
        var manageUsers = NewRole(manageUsersCode);
        NewRole(extraCode);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(manageUsers.Id, "Security.ManageUsers"));

        var hash = new PasswordHasher().Hash(Password);
        User Local(string prefix)
        {
            var user = new User($"{prefix}_{_tag}", prefix, AuthProvider.Local);
            user.SetPassword(hash);
            db.Users.Add(user);
            return user;
        }

        var admin = Local("adm");
        var victim = Local("usr");
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(manageUsers.Id, admin.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (admin.Id, victim.Id, manageUsersCode, extraCode);
    }

    private static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

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
