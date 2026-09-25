// tests/Ecr.Api.Tests/Security/PatchCellsEmptyBatchAccessTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// V-02 (UX-прохід, третій раунд): <c>PATCH …/cells</c> без жодної комірки
/// оминав перевірку прав — користувач із забороною (Deny) на проєкт діставав
/// <c>200</c>, версії ВСІХ рядків таблиці, а документ «торкався»
/// (<c>ModifiedAt/By</c>). Наскрізно: справжній SQL, справжній вхід, HTTP.
/// </summary>
[Collection("SqlServer")]
public sealed class PatchCellsEmptyBatchAccessTests(SqlServerFixture sql)
{
    private const string Password = "Api-Patch-Empty-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Deny_на_проєкт_порожній_PATCH_дає_404_і_не_торкається_документа()
    {
        var s = await ArrangeAsync(deny: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // Та сама відповідь, що й на читання.
        var get = await client
            .GetAsync(new Uri($"/api/v1/documents/{s.Document.DocumentId}", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        foreach (var rows in EmptyBatches(s.RowKey))
        {
            var response = await PatchAsync(client, s, rows).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
            Assert.Contains("ECR-DOC-0404", body, StringComparison.Ordinal);
            Assert.DoesNotContain(s.OtherRowKey, body, StringComparison.Ordinal);
        }

        await AssertUntouchedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Deny_на_проєкт_застаріла_версія_не_віддає_409_з_чужими_даними()
    {
        var s = await ArrangeAsync(deny: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchAsync(client, s, new object[]
        {
            new
            {
                rowKey = s.RowKey,
                baseVersion = "00000000DEADBEEF",
                cells = new object[] { new { columnCode = s.ColumnCode, value = (object)1m } },
            },
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        // ⛔ Раніше звірка версій ішла ДО прав: 409 із чинною версією рядка.
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        await AssertUntouchedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Писар_порожній_PATCH_це_no_op_без_ключів_рядків()
    {
        var s = await ArrangeAsync(deny: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        foreach (var rows in EmptyBatches(s.RowKey))
        {
            var response = await PatchAsync(client, s, rows).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
            Assert.Contains("\"appliedCells\":0", body, StringComparison.Ordinal);
            Assert.Contains("\"rowVersions\":{}", body, StringComparison.Ordinal);
        }

        await AssertUntouchedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Deny_на_проєкт_POST_rows_дає_404_а_не_відмову_про_таблицю()
    {
        var s = await ArrangeAsync(deny: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // ⛔ Наявний ключ: раніше відмова `ECR-ROW-0409` (режим таблиці / ключ
        // зайнятий) приходила ДО перевірки прав і розповідала про таблицю
        // документа, якого цей користувач не бачить.
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Document.TableInstanceId, rowKey = s.RowKey }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("ECR-DOC-0404", body, StringComparison.Ordinal);
        await AssertUntouchedAsync(s).ConfigureAwait(true);
    }

    /// <summary>Батчі, у яких нема що записати: без рядків і з рядком без комірок.</summary>
    private static IEnumerable<object[]> EmptyBatches(string rowKey)
    {
        yield return [];
        yield return [new { rowKey, baseVersion = "00", cells = Array.Empty<object>() }];
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, object[] rows)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.Document.PeriodKey.Value,
                origin = "UserEdit",
                rows,
            });

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task AssertUntouchedAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var document = await db.Documents.AsNoTracking()
            .SingleAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(false);

        Assert.Equal(s.ModifiedAt, document.ModifiedAt);
        Assert.Equal(s.ModifiedByUserId, document.ModifiedByUserId);
    }

    private async Task<Scenario> ArrangeAsync(bool deny)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync().ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var userName = $"pce_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var writer = NewRole("PCE_W");
        db.Roles.Add(writer);
        var denier = NewRole("PCE_D");
        db.Roles.Add(denier);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(writer.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(writer.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(writer.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        // ⚠ Заборона — ПОВЕРХ гранта на запис: сильніший випадок, ніж «немає
        // гранта зовсім», і саме такий стояв на стенді (`FSEC_VIEWER`).
        if (deny)
        {
            db.RoleAssignments.Add(new RoleAssignment(denier.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(
                denier.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Read, isDeny: true));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKeys = await db.TableRows.AsNoTracking()
            .Where(r => r.TableInstanceId == document.TableInstanceId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue)
            .ToListAsync().ConfigureAwait(false);

        var columnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == document.ColumnDefIds[0])
            .Select(c => c.Code)
            .SingleAsync().ConfigureAwait(false);

        var stored = await db.Documents.AsNoTracking()
            .SingleAsync(d => d.Id == document.DocumentId).ConfigureAwait(false);

        return new Scenario(
            document, userName, rowKeys[0], rowKeys[^1], columnCode,
            stored.ModifiedAt, stored.ModifiedByUserId);
    }

    private static Role NewRole(string prefix)
        => new(
            EcrCode.Create($"{prefix}_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = prefix }));

    private sealed record Scenario(
        TestDocument Document,
        string UserName,
        string RowKey,
        string OtherRowKey,
        string ColumnCode,
        DateTime ModifiedAt,
        int ModifiedByUserId);
}
