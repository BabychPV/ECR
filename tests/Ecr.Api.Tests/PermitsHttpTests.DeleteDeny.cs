// tests/Ecr.Api.Tests/PermitsHttpTests.DeleteDeny.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// AU-13 P2-B (S18): <c>DELETE /permits/{id}</c> перевіряв доступ лише до реєстру шапки - користувач із Deny на таблицю
/// дозволу видаляв чернетку разом із рядками схованої від нього таблиці (204, <c>IsDeleted = 1</c>).
/// </summary>
/// <remarks>
/// Мутація: прибрати виклик <c>RequireTablesAsync</c> у <c>DeletePermitHandler.HandleAsync</c> - червоніє (204 замість 404,
/// рядок таблиці видалено).
/// </remarks>
public sealed partial class PermitsHttpTests
{
    private const string DenyPassword = "Api-Permit-Delete-Deny-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_чернетки_з_Deny_на_таблицю_дозволу_404_як_неіснуючий_і_нічого_не_видаляє()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var type = await PublishTypeAsync(editor, app);
        var registry = $"PRM_{type}_Rows";
        var created = await editor.PostAsJsonAsync(new Uri("/api/v1/permits", UriKind.Relative), Create(type, "HT-DENY", "2026-01-01", "2027-01-01"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{created.StatusCode}: {app.ErrorsText}");
        var permitId = (await Json(created)).GetProperty("id").GetInt64();
        var batch = await editor.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry}/entries/batch?dryRun=false", UriKind.Relative),
            new
            {
                items = new[]
                {
                    new
                    {
                        clientRowId = "r1", op = "upsert", id = (long?)null, code = (string?)null, baseVersion = (string?)null,
                        values = new Dictionary<string, string?> { ["PERMIT"] = permitId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Name"] = "Hidden row" },
                    },
                },
            });
        Assert.True((await Json(batch)).GetProperty("applied").GetBoolean(), app.ErrorsText);

        int tableId;
        await using (var db = sql.CreateContext())
        {
            tableId = await db.RegistryDefs.Where(d => d.Code == registry).Select(d => d.Id).SingleAsync();
        }

        using var denied = await SignedInWithDenyAsync(app, tableId);
        var missing = await denied.DeleteAsync(new Uri("/api/v1/permits/999999999", UriKind.Relative));
        var hidden = await denied.DeleteAsync(new Uri($"/api/v1/permits/{permitId}", UriKind.Relative));
        var hiddenBody = await hidden.Content.ReadAsStringAsync();
        Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"{hidden.StatusCode}: {hiddenBody}");
        Assert.Equal(Uniform(await missing.Content.ReadAsStringAsync()), Uniform(hiddenBody));

        // Нічого не видалено: ні шапку, ні рядок схованої таблиці.
        await using (var db = sql.CreateContext())
        {
            Assert.False(await db.RegistryEntries.Where(e => e.Id == permitId).Select(e => e.IsDeleted).SingleAsync());
            Assert.Equal(1, await db.RegistryEntries.CountAsync(e => e.RegistryDefId == tableId && !e.IsDeleted));
        }

        // Контроль: редактор без Deny видаляє, як і раніше, разом із рядком.
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync(new Uri($"/api/v1/permits/{permitId}", UriKind.Relative))).StatusCode);
        await using (var db = sql.CreateContext())
        {
            Assert.Equal(0, await db.RegistryEntries.CountAsync(e => e.RegistryDefId == tableId && !e.IsDeleted));
        }
    }

    private static string Uniform(string body)
        => Regex.Replace(Regex.Replace(body, "\"(traceId|correlationId|requestId)\"\\s*:\\s*\"[^\"]*\",?", string.Empty), "[0-9]+", "N");

    private async Task<HttpClient> SignedInWithDenyAsync(EcrApiFactory app, int deniedRegistryId)
    {
        var name = $"pdel_{Guid.NewGuid():N}"[..20];
        await using (var db = sql.CreateContext())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(DenyPassword));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Text("Permit delete deny"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in AllRights)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, deniedRegistryId, GrantLevel.Read, isDeny: true));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = DenyPassword });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
