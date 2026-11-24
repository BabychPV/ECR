using System.Net;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>B158 (лінія d): групові призначення, переклади інтерфейсу, перейменування ролі.</summary>
/// <remarks>✎ P-1124-3: d6 (версії опису звіту) прибрано разом із конструктором звітів.</remarks>
public sealed partial class AdminDuplicateRequestHttpTests
{
    // ───────────────────────────── d7: ролі групам каталогу ─────────────────────────────

    /// <summary>
    /// d7: <c>POST /security/group-assignments</c> (та сама роль, та сама група) двічі одночасно — один <c>201</c>, другий
    /// та сама відмова <c>ECR-SEC-0409 groupAssignmentExists</c> з <c>{code, sid}</c>, що й послідовний повтор (раніше —
    /// 500 на <c>UQ_RoleAssignment_Sid</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d7_Роль_групі_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageUsers"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (roleId, roleCode) = await NewRoleAsync().ConfigureAwait(true);
        const string url = "/api/v1/security/group-assignments";
        object Body(string sid) => new { roleId, principal = sid };

        var sid = NewSid();
        var raced = NewSid();
        try
        {
            // Послідовно.
            var created = await SendAsync(first, HttpMethod.Post, url, Body(sid)).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Post, url, Body(sid)).ConfigureAwait(true);

            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            AssertRefusal(repeated, repeated.Status, "ECR-SEC-0409", "err.ECR-SEC-0409.groupAssignmentExists", ("code", roleCode), ("sid", sid));

            // Одночасно.
            var replies = await RaceAsync(
                "group-assignments",
                meet => SendAsync(first, HttpMethod.Post, url, Body(raced), meet),
                meet => SendAsync(second, HttpMethod.Post, url, Body(raced), meet)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
            AssertRefusal(
                replies.Single(r => r.Status != HttpStatusCode.Created),
                repeated.Status, "ECR-SEC-0409", "err.ECR-SEC-0409.groupAssignmentExists", ("code", roleCode), ("sid", raced));

            await using var db = Db();
            Assert.Equal(1, await db.RoleAssignments.CountAsync(a => a.PrincipalSid == raced && a.RoleId == roleId).ConfigureAwait(true));
        }
        finally
        {
            // Групові призначення вигаданих SID нікому доступу не дають, але спільна база — прибираємо.
            await using var db = Db();
            await db.RoleAssignments.Where(a => a.PrincipalSid == sid || a.PrincipalSid == raced)
                .ExecuteDeleteAsync().ConfigureAwait(true);
        }
    }

    // ───────────────────────────── d8: переклади інтерфейсу ─────────────────────────────

    /// <summary>
    /// d8: <c>PUT /ui-strings/{lang}/{key}</c> — НОВИЙ переклад наявного ключа — двічі одночасно: обидва <c>200</c>, як і
    /// послідовний повтор (upsert), жодного 500 на <c>PK_UiString</c>; рядок перекладу один. Гонку не відтворено —
    /// виправлення (UPDLOCK/HOLDLOCK) не робилось; тест — сторож поведінки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d8_Новий_переклад_двічі_одночасно_обидва_200_як_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["System.ManageLocalization"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var key = $"b158d.probe.{Guid.NewGuid():N}";
        var raced = $"b158d.probe.{Guid.NewGuid():N}";
        await using (var db = Db())
        {
            // Ключ існує англійською (ключ без жодного рядка `PUT` відкидає) — перекладу іншою мовою ще немає.
            foreach (var probe in new[] { key, raced })
            {
                await db.Database.ExecuteSqlAsync(
                    $"INSERT sys_ecr.UiString ([Key], LanguageCode, Value, Scope, ModifiedAt) VALUES ({probe}, N'en', N'B158d probe', {(byte)UiStringScope.Public}, SYSUTCDATETIME())")
                    .ConfigureAwait(true);
            }
        }

        try
        {
            string lang;
            await using (var db = Db())
            {
                lang = await db.Database
                    .SqlQuery<string>($"SELECT TOP (1) Code AS Value FROM sys_ecr.Language WHERE Code <> N'en' ORDER BY Code")
                    .SingleAsync().ConfigureAwait(true);
            }

            var body = new { value = "B158d probe", scope = "Public" };
            string Url(string k) => $"/api/v1/ui-strings/{lang}/{k}";

            // Послідовно: повтор того самого PUT — той самий 200.
            var saved = await SendAsync(first, HttpMethod.Put, Url(key), body).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Put, Url(key), body).ConfigureAwait(true);

            Assert.True(saved.Status == HttpStatusCode.OK, $"{saved}\n{App.ErrorsText}");
            Assert.True(repeated.Status == HttpStatusCode.OK, $"{repeated}\n{App.ErrorsText}");

            // Одночасно. ⚠ Не відтворено (B158d, 17.11): 500 на `PK_UiString` не дав жоден прогін — ні простий (3/3), ні з
            // тримачем рядка ревізії каталогу (2/2), ні з тримачем рядка мови під XLOCK (1/1): в обох «розтягнутих» другий
            // `UPDATE` чекає на щойно вставлений (незакомічений) рядок першого й оновлює його. Вікно — між `UPDATE`
            // (0 рядків) і `INSERT` однієї пачки SQL; ззовні його не розтягнути. Тест лишається сторожем поведінки:
            // жодного 5xx, обидва 200, рядок один.
            var replies = await RaceAsync(
                "ui-strings",
                meet => SendAsync(first, HttpMethod.Put, Url(raced), body, meet),
                meet => SendAsync(second, HttpMethod.Put, Url(raced), body, meet)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.All(replies, r => Assert.True(r.Status == repeated.Status, $"{r}\n{App.ErrorsText}"));

            await using var check = Db();
            Assert.Equal(
                1,
                await check.Database
                    .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sys_ecr.UiString WHERE [Key] = {raced} AND LanguageCode = {lang}")
                    .SingleAsync().ConfigureAwait(true));
        }
        finally
        {
            await using var db = Db();
            await db.Database.ExecuteSqlAsync($"DELETE sys_ecr.UiString WHERE [Key] IN ({key}, {raced})").ConfigureAwait(true);
        }
    }

    // ───────────────────────────── d9: перейменування ролі ─────────────────────────────

    /// <summary>
    /// d9: дві РІЗНІ ролі перейменовують в один код одночасно — одне <c>204</c>, друге та сама відмова <c>ECR-SEC-0409
    /// roleCodeTaken</c> з <c>{code}</c>, що й послідовне перейменування в зайнятий код (раніше — 500 на <c>UQ_Role</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d9_Дві_ролі_в_один_код_одночасно_одне_204_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageRoles"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static string Url(int id) => $"/api/v1/roles/{id}/code";
        static object Body(string code) => new { code };

        // Послідовно.
        var (a, _) = await NewRoleAsync().ConfigureAwait(true);
        var (b, _) = await NewRoleAsync().ConfigureAwait(true);
        var target = NewCode("RN");
        var renamed = await SendAsync(first, HttpMethod.Put, Url(a), Body(target)).ConfigureAwait(true);
        var taken = await SendAsync(first, HttpMethod.Put, Url(b), Body(target)).ConfigureAwait(true);

        Assert.True(renamed.Status == HttpStatusCode.NoContent, $"{renamed}\n{App.ErrorsText}");
        AssertRefusal(taken, taken.Status, "ECR-SEC-0409", "err.ECR-SEC-0409.roleCodeTaken", ("code", target));

        // Одночасно.
        var (c, _) = await NewRoleAsync().ConfigureAwait(true);
        var (d, _) = await NewRoleAsync().ConfigureAwait(true);
        var raced = NewCode("RN");
        var replies = await RaceAsync(
            "role-rename",
            meet => SendAsync(first, HttpMethod.Put, Url(c), Body(raced), meet),
            meet => SendAsync(second, HttpMethod.Put, Url(d), Body(raced), meet)).ConfigureAwait(true);

        AssertNoServerError(replies);
        Assert.Single(replies, r => r.Status == HttpStatusCode.NoContent);
        AssertRefusal(
            replies.Single(r => r.Status != HttpStatusCode.NoContent),
            taken.Status, "ECR-SEC-0409", "err.ECR-SEC-0409.roleCodeTaken", ("code", raced));

        await using var db = Db();
        Assert.Equal(1, await db.Roles.CountAsync(r => r.Code == raced).ConfigureAwait(true));
    }

    /// <summary>Роль без прав (небезпечних прав для підтвердження немає), лише в тестовій базі.</summary>
    private async Task<(int Id, string Code)> NewRoleAsync()
    {
        await using var db = Db();
        var role = new Role(EcrCode.Create(NewCode("RB")), Name("B158d role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (role.Id, role.Code);
    }

    /// <summary>Добре сформований доменний SID, якого немає в жодному каталозі.</summary>
    private static string NewSid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        uint Part(int offset) => BitConverter.ToUInt32(bytes, offset) % 900_000_000u + 100_000_000u;
        return $"S-1-5-21-{Part(0)}-{Part(4)}-{Part(8)}-{(BitConverter.ToUInt32(bytes, 12) % 90_000u) + 10_000u}";
    }
}
