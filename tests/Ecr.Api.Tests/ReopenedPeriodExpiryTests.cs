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
/// `F-08` (UX-PASS, четвертий раунд): перевідкритий період після
/// <c>ReopenedUntil</c> закритий для запису ОДРАЗУ, а не після найближчого
/// годинного прогону <c>PeriodStateJob</c>.
/// </summary>
/// <remarks>
/// ⛔ На живому стенді запис через 18 с після <c>until</c> проходив із 200:
/// перевірка запису читала збережений стан <c>Grace</c>, а змінює його лише
/// годинна задача. Тут той самий шлях — <c>PATCH …/cells</c> через HTTP, —
/// і та сама послідовність: запис до межі проходить, після межі — ні.
///
/// ⚠ Застосунок піднімається ДО перевідкриття: разовий прогін
/// <c>PeriodStateJob</c> на старті (<c>RecurringScheduleService</c>) бачить
/// період ще закритим і нічого не змінює. Інакше тест міг би пройти завдяки
/// задачі, а не перевірці запису.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReopenedPeriodExpiryTests(SqlServerFixture sql)
{
    private const string Password = "Api-Reopen-Expiry-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F-08")]
    public async Task Після_ReopenedUntil_запис_відхиляється_без_очікування_задачі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync().ConfigureAwait(true);
        var (userName, columnCode, rowKey) = await ArrangeClosedPeriodAsync(builder, document).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        var until = DateTime.UtcNow.AddSeconds(4);
        await ReopenAsync(builder, document, until).ConfigureAwait(true);

        // До межі — період перевідкрито, запис проходить.
        var before = await WriteAsync(client, document, rowKey, columnCode, 1m).ConfigureAwait(true);
        Assert.True(
            before.StatusCode == HttpStatusCode.OK,
            $"до until: {before.StatusCode}: {await before.Content.ReadAsStringAsync().ConfigureAwait(true)}; {app.ErrorsText}");

        var wait = until - DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(true);
        }

        // ⛔ Мутація: повернути в `AccessDecisionService.BuildContextAsync`
        // збережений `period.State` — тут знову 200.
        var after = await WriteAsync(client, document, rowKey, columnCode, 2m).ConfigureAwait(true);
        var body = await after.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(after.StatusCode == HttpStatusCode.Forbidden, $"після until: {after.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-ACCS-0403", problem.GetProperty("errorCode").GetString());
        Assert.Equal("PeriodClosed", problem.GetProperty("reason").GetString());
    }

    /// <summary>Активний проєкт, період із порахованими межами — закритий; користувач із грантом Write.</summary>
    private static async Task<(string UserName, string ColumnCode, string RowKey)> ArrangeClosedPeriodAsync(
        TestDocumentBuilder builder, TestDocument document)
    {
        await using var db = builder.CreateContext();

        var project = await db.Projects.SingleAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(DateTime.UtcNow);

        var policy = await db.PeriodPolicies.SingleAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value)
            .ConfigureAwait(false);

        // Межі — справжні (січень 2026 закрився навесні), тож розрахунок на
        // «зараз» — `Closed`; збережений стан теж `Closed`.
        period.RecomputeBoundaries(policy, SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());
        period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);

        var userName = $"reopen_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"REOPEN_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Reopen expiry" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnCode = await db.ColumnDefs
            .Where(c => c.Id == document.ColumnDefIds[1])
            .Select(c => c.Code)
            .SingleAsync()
            .ConfigureAwait(false);
        var rowKey = await db.TableRows
            .Where(r => r.Id == document.RowIds[0] && r.PeriodKeyValue == document.PeriodKey.Value)
            .Select(r => r.RowKeyValue)
            .SingleAsync()
            .ConfigureAwait(false);

        return (userName, columnCode, rowKey);
    }

    private static async Task ReopenAsync(TestDocumentBuilder builder, TestDocument document, DateTime until)
    {
        await using var db = builder.CreateContext();

        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value)
            .ConfigureAwait(false);
        period.Reopen(until, "F-08", DateTime.UtcNow);

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> WriteAsync(
        HttpClient client, TestDocument document, string rowKey, string columnCode, decimal value)
    {
        var slice = await client.GetAsync(new Uri(
            $"/api/v1/documents/{document.DocumentId}/tables/{document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        var row = (await slice.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false))
            .GetProperty("rows").EnumerateArray()
            .FirstOrDefault(r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));

        var version = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("rowVersion", out var v)
            ? v.GetString()
            : null;

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = document.TableInstanceId,
                periodKey = document.PeriodKey.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = version,
                        cells = new object[] { new { columnCode, value } },
                    },
                },
            }).ConfigureAwait(false);
    }
}
