// tests/Ecr.Api.Tests/Security/RevokedCookieSelfRotationMatrixTests.cs
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
/// Відкликана cookie не перевидає собі нову ЖОДНОЮ власною ротацією штампа —
/// хоч би як її відкликали. Матриця «відкликання на іншому вузлі × власна
/// ротація на цьому вузлі».
/// </summary>
/// <remarks>
/// ⛔ Чому матриця, а не ще один сценарій. L1-01 виправлявся двічі. Перше
/// виправлення (AN-26) перевіряло «хто прокрутив штамп» і «чи він досі в БД»,
/// а тести до нього брали кожну вісь окремо: чужа ротація БЕЗ власної,
/// власна ротація БЕЗ чужої. Діра була саме в їхній КОМПОЗИЦІЇ — чужа ротація
/// A→B, а слідом власна B→C тією самою старою cookie; знайшло її рев'ю, а не
/// гейт (AN-26b). Тут осі перемножено: кожен шлях відкликання з кожним шляхом
/// власної ротації, через справжні ендпоінти, а не правку штампа в БД.
///
/// ⚠ «Інший вузол» — <see cref="SecondNodeApiFactory"/> на тій самій базі.
/// На вузлі A кеш штампа 5 с: саме в цьому вікні стара cookie ще проходить
/// перевірку, і лише тут обхід можливий. Відкликання на самому вузлі A кеш
/// скидає (<c>SecurityStampCacheInvalidator</c>) і діри не відкриває.
///
/// ⚠ Блокування адміністратором — контрольний рядок: його відсікає ще й
/// читання штампа (заблокований запис чинного штампа не має), тож без
/// перевірки AN-26b він лишається зеленим. Решта рядків без неї червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class RevokedCookieSelfRotationMatrixTests(SqlServerFixture sql)
{
    private const string Password = "Karachaganak-2026-Spring!";
    private const string NewPassword = "Tengiz-Revocation-2026-Autumn!";
    private const string CookieName = "ecr.auth";

    private static readonly Uri Me = new("/api/v1/me", UriKind.Relative);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Як власника вкраденої cookie відкликали на іншому вузлі.</summary>
    public enum Revocation
    {
        /// <summary>Власник вийшов (вихід закриває всі сесії, S21).</summary>
        OwnerLogout,

        /// <summary>Власник змінив пароль.</summary>
        OwnerChangedPassword,

        /// <summary>Адміністратор скинув пароль.</summary>
        AdminResetPassword,

        /// <summary>Адміністратор заблокував запис (контроль: друга перепона).</summary>
        AdminLock,
    }

    /// <summary>Чим стара cookie сама крутить штамп власника.</summary>
    public enum SelfRotation
    {
        /// <summary><c>PUT /users/{self}/roles</c> — <c>UserStore.ReplaceRolesAsync</c>.</summary>
        OwnRoles,

        /// <summary><c>PUT /roles/{own}/grants</c> — <c>UserStore.RotateStampsForRoleAsync</c>.</summary>
        OwnRoleGrants,
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(Revocation.OwnerLogout, SelfRotation.OwnRoles)]
    [InlineData(Revocation.OwnerLogout, SelfRotation.OwnRoleGrants)]
    [InlineData(Revocation.OwnerChangedPassword, SelfRotation.OwnRoles)]
    [InlineData(Revocation.OwnerChangedPassword, SelfRotation.OwnRoleGrants)]
    [InlineData(Revocation.AdminResetPassword, SelfRotation.OwnRoles)]
    [InlineData(Revocation.AdminResetPassword, SelfRotation.OwnRoleGrants)]
    [InlineData(Revocation.AdminLock, SelfRotation.OwnRoles)]
    [InlineData(Revocation.AdminLock, SelfRotation.OwnRoleGrants)]
    public async Task Відкликана_на_іншому_вузлі_cookie_не_перевидає_себе_власною_ротацією(
        Revocation revocation, SelfRotation rotation)
    {
        var ids = await ArrangeAsync().ConfigureAwait(true);

        // ⚠ Порядок підняття важливий: фабрики задають кеш штампа змінною
        // оточення процесу, а хост читає її під час збирання. Вузол A (5 с)
        // збирається першим, вузол B (0 с) — після нього.
        using var nodeA = new EcrApiFactory(sql, stampCacheSeconds: 5);
        using var stolen = await SignedInAsync(nodeA.CreateClient(), $"own_{_tag}", Password, nodeA.ErrorsText).ConfigureAwait(true);

        using var nodeB = new SecondNodeApiFactory(sql);
        using var revoker = revocation is Revocation.OwnerLogout or Revocation.OwnerChangedPassword
            ? await SignedInAsync(nodeB.CreateClient(), $"own_{_tag}", Password, nodeB.ErrorsText).ConfigureAwait(true)
            : await SignedInAsync(nodeB.CreateClient(), $"adm_{_tag}", Password, nodeB.ErrorsText).ConfigureAwait(true);

        // Вузол B уже піднятий (старт хосту довший за вікно кешу) — лише тепер
        // прогріваємо кеш штампа A на вузлі A.
        var grants = new Uri($"/api/v1/roles/{ids.ManageRoles}/grants", UriKind.Relative);
        using var read = await stolen.GetAsync(grants).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var revoked = await RevokeAsync(revoker, revocation, ids.Owner).ConfigureAwait(true);
        Assert.True(revoked.IsSuccessStatusCode, $"{revocation}: {revoked.StatusCode} {nodeB.ErrorsText}");

        // Стара cookie проходить із кешу вузла A і сама крутить штамп власника.
        using var put = rotation == SelfRotation.OwnRoles
            ? await stolen.PutAsJsonAsync(
                new Uri($"/api/v1/users/{ids.Owner}/roles", UriKind.Relative),
                new { roleCodes = new[] { ids.ManageUsersCode, ids.ManageRolesCode, ids.ExtraCode } }).ConfigureAwait(true)
            : await PutGrantsAsync(stolen, grants, read.Headers.ETag?.Tag).ConfigureAwait(true);

        // ⛔ Без цього рядок порожній: якби запит відхилили з іншої причини,
        // ротації не було б, і «нової cookie немає» нічого б не доводило.
        Assert.True(put.IsSuccessStatusCode, $"{revocation}/{rotation}: {put.StatusCode} {nodeA.ErrorsText}");

        Assert.DoesNotContain(
            put.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)
                 && !c.StartsWith(CookieName + "=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync(Me)).StatusCode);
    }

    private static Task<HttpResponseMessage> RevokeAsync(HttpClient revoker, Revocation revocation, int ownerId)
        => revocation switch
        {
            Revocation.OwnerLogout => revoker.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), content: null),
            Revocation.OwnerChangedPassword => revoker.PostAsJsonAsync(
                new Uri("/api/v1/auth/change-password", UriKind.Relative),
                new { currentPassword = Password, newPassword = NewPassword }),
            Revocation.AdminResetPassword => revoker.PostAsJsonAsync(
                new Uri($"/api/v1/users/{ownerId}/reset-password", UriKind.Relative),
                new { newPassword = NewPassword }),
            Revocation.AdminLock => revoker.PostAsJsonAsync(
                new Uri($"/api/v1/users/{ownerId}/lock", UriKind.Relative),
                new { reason = "L1-01 matrix" }),
            _ => throw new ArgumentOutOfRangeException(nameof(revocation), revocation, null),
        };

    private static async Task<HttpResponseMessage> PutGrantsAsync(HttpClient client, Uri grants, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, grants)
        {
            Content = JsonContent.Create(new { grants = Array.Empty<object>() }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private async Task<(int Owner, int ManageRoles, string ManageUsersCode, string ManageRolesCode, string ExtraCode)> ArrangeAsync()
    {
        await using var db = CreateContext();

        Role NewRole(string code)
        {
            var role = new Role(EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
            db.Roles.Add(role);
            return role;
        }

        var manageUsersCode = $"RMU_{_tag}";
        var manageRolesCode = $"RMR_{_tag}";
        var extraCode = $"RMX_{_tag}";
        var manageUsers = NewRole(manageUsersCode);
        var manageRoles = NewRole(manageRolesCode);
        NewRole(extraCode);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(manageUsers.Id, "Security.ManageUsers"));
        db.RolePermissions.Add(new RolePermission(manageRoles.Id, "Security.ManageRoles"));

        var hash = new PasswordHasher().Hash(Password);
        User Local(string prefix)
        {
            var user = new User($"{prefix}_{_tag}", prefix, AuthProvider.Local);
            user.SetPassword(hash);
            db.Users.Add(user);
            return user;
        }

        // Власник вкраденої cookie — сам адміністратор: лише так у нього є
        // власна ротація штампа (свої ролі, гранти своєї ролі).
        var owner = Local("own");
        var admin = Local("adm");
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var user in new[] { owner, admin })
        {
            db.RoleAssignments.Add(new RoleAssignment(manageUsers.Id, user.Id, principalSid: null));
            db.RoleAssignments.Add(new RoleAssignment(manageRoles.Id, user.Id, principalSid: null));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (owner.Id, manageRoles.Id, manageUsersCode, manageRolesCode, extraCode);
    }

    private static async Task<HttpClient> SignedInAsync(HttpClient client, string userName, string password, string errors)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password }).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{userName}: {response.StatusCode} {errors}");
        return client;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
