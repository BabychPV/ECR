// tests/Ecr.Api.Tests/DocumentCardModifiedAtTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Приймальна №8, D-12: у картці документа (<c>GET /documents/{id}</c>) <c>modifiedAt</c> і
/// <c>modifiedByDisplayName</c> були <c>null</c> ДЛЯ ВСІХ, навіть для читача без звуження й після десятків правок:
/// <c>DocumentStore.FindAsync</c> (шлях картки) їх не читав, на відміну від переліку (<c>ListAsync</c>).
/// R-7 (звужений читач бачить <c>null</c>) — окремо й лишається.
/// </summary>
[Collection("SqlServer")]
public sealed class DocumentCardModifiedAtTests(SqlServerFixture sql)
{
    private const string Password = "Api-Card-ModAt-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-12")]
    public async Task Картка_документа_віддає_modifiedAt_і_автора_правки_читачу_без_звуження_і_вони_ростуть()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = s.UserName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        var cardUri = new Uri($"/api/v1/documents/{s.Doc.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative);

        var before = await ReadCardAsync(client, cardUri, app).ConfigureAwait(true);
        var modifiedBefore = before.GetProperty("modifiedAt").GetDateTime();

        // Правка комірок: створення динамічного рядка (Mixed) - той самий шлях, що й у сітці.
        var patch = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = $"DYN{Guid.NewGuid():N}"[..12],
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = s.ColumnCode, value = (object)12.5m } },
                    },
                },
            }).ConfigureAwait(true);
        Assert.True(
            patch.StatusCode == HttpStatusCode.OK,
            $"PATCH: {patch.StatusCode}\n{await patch.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var after = await ReadCardAsync(client, cardUri, app).ConfigureAwait(true);

        Assert.True(
            after.GetProperty("modifiedAt").GetDateTime() > modifiedBefore,
            $"modifiedAt не виріс: було {modifiedBefore:O}, стало {after.GetProperty("modifiedAt").GetDateTime():O}");
        Assert.Equal(s.UserName, after.GetProperty("modifiedByDisplayName").GetString());
    }

    /// <summary>
    /// R-7, характеризаційний (не red-first: R-7 уже в коді): звужений читач (Deny на аркуш) бачить у картці
    /// <c>modifiedAt</c>/<c>modifiedByDisplayName</c> = <c>null</c>, бо «хто й коли правив» стосується й схованого аркуша;
    /// незвужений читач того самого документа бачить обидва значення.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-12")]
    public async Task Картка_для_звуженого_читача_без_modifiedAt_а_для_незвуженого_з_ним()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(true);
        var extra = await MultiTableDocument.AddTablesAsync(builder, doc, [1]).ConfigureAwait(true);

        await using var db = builder.CreateContext();

        var wide = new User($"wd_{Guid.NewGuid():N}"[..20], "Wide reader", AuthProvider.Local);
        var narrow = new User($"nr_{Guid.NewGuid():N}"[..20], "Narrow reader", AuthProvider.Local);
        wide.SetPassword(new PasswordHasher().Hash(Password));
        narrow.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.AddRange(wide, narrow);

        var viewer = new Role(
            EcrCode.Create($"MODV_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Card viewer" }));
        var denier = new Role(
            EcrCode.Create($"MODD_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Card sheet denier" }));
        db.Roles.AddRange(viewer, denier);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(viewer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
        db.RoleAssignments.Add(new RoleAssignment(viewer.Id, wide.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(viewer.Id, narrow.Id, null));

        db.RoleAssignments.Add(new RoleAssignment(denier.Id, narrow.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(denier.Id, ResourceKind.Sheet, extra[0].SheetDefId, GrantLevel.Read, isDeny: true));
        await db.SaveChangesAsync().ConfigureAwait(true);

        // Правка документа: «дотик» із автором (той самий, що робить `DocumentStore.TouchAsync`).
        await db.Documents
            .Where(d => d.Id == doc.DocumentId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.ModifiedAt, DateTime.UtcNow)
                .SetProperty(d => d.ModifiedByUserId, wide.Id))
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        foreach (var (user, expectHidden) in new[] { (wide.UserName, false), (narrow.UserName, true) })
        {
            using var client = app.CreateClient();
            var login = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = user, password = Password }).ConfigureAwait(true);
            Assert.True(login.IsSuccessStatusCode, $"Вхід {user}: {login.StatusCode}: {app.ErrorsText}");

            using var response = await client.GetAsync(
                new Uri($"/api/v1/documents/{doc.DocumentId}?periodKey={doc.PeriodKey.Value}", UriKind.Relative)).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{user}: {response.StatusCode}: {body}\n{app.ErrorsText}");

            var card = JsonDocument.Parse(body).RootElement;
            if (expectHidden)
            {
                Assert.Equal(JsonValueKind.Null, card.GetProperty("modifiedAt").ValueKind);
                Assert.Equal(JsonValueKind.Null, card.GetProperty("modifiedByDisplayName").ValueKind);
            }
            else
            {
                Assert.Equal(JsonValueKind.String, card.GetProperty("modifiedAt").ValueKind);
                Assert.Equal("Wide reader", card.GetProperty("modifiedByDisplayName").GetString());
            }
        }
    }

    private static async Task<JsonElement> ReadCardAsync(HttpClient client, Uri uri, EcrApiFactory app)
    {
        using var response = await client.GetAsync(uri).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET картки: {response.StatusCode}: {body}\n{app.ErrorsText}");

        var card = JsonDocument.Parse(body).RootElement.Clone();
        Assert.True(
            card.TryGetProperty("modifiedAt", out var at) && at.ValueKind == JsonValueKind.String,
            $"modifiedAt у картці порожній: {body}");
        return card;
    }

    /// <summary>Документ у відкритому поточному періоді й автор з <c>Write</c> на проєкт (без звуження нижче проєкту).</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var doc = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var userName = $"mod_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"MODAT_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Card modifiedAt writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == doc.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);

        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == doc.ColumnDefIds[1])
            .Select(c => c.Code)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(doc, userName, periodKey, columnCode);
    }

    private sealed record Scenario(TestDocument Doc, string UserName, int PeriodKey, string ColumnCode);
}
