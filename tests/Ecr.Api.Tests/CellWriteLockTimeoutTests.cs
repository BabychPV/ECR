// tests/Ecr.Api.Tests/CellWriteLockTimeoutTests.cs
using System.Diagnostics;
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
/// PATCH комірки, поки довга операція (перенос версії іншого проєкту) тримає блокування
/// <c>doc.CellValue</c>, — швидка <c>409 ECR-DOC-4091 lockTimeout</c>, а не <c>500</c> через ~30 с
/// (TIER2 N-3, <c>LockWaitGuard</c>).
/// </summary>
/// <remarks>
/// ⚠ Блокувальник — окреме з'єднання з відкритою транзакцією і <c>TABLOCKX</c> на
/// <c>doc.CellValue</c>: це те, чим ескалація блокувань робить перенос великого проєкту. Синхронізація
/// детермінована: команда блокування повертається лише коли блокування вже взяте; жодних sleep.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellWriteLockTimeoutTests(SqlServerFixture sql)
{
    private const string Password = "Api-Cell-Lock-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правка_комірки_під_чужим_блокуванням_таблиці_дає_швидку_409_а_після_звільнення_проходить()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var factory = new EcrApiFactory(sql);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = scenario.UserName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {factory.ErrorsText}");

        var opened = await client.GetAsync(new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative)).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {factory.ErrorsText}");
        var numberColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);

        async Task<(HttpResponseMessage Response, string Body)> PatchAsync()
        {
            var rowKey = $"LCK{Guid.NewGuid():N}"[..12];
            var response = await client.PatchAsJsonAsync(
                new Uri($"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative),
                new
                {
                    tableInstanceId = scenario.Document.TableInstanceId,
                    periodKey = scenario.PeriodKey,
                    origin = "UserEdit",
                    rows = new[]
                    {
                        new
                        {
                            rowKey,
                            baseVersion = (string?)null,
                            cells = new object[] { new { columnCode = numberColumn, value = (object)"12.5" } },
                        },
                    },
                }).ConfigureAwait(true);
            return (response, await response.Content.ReadAsStringAsync().ConfigureAwait(true));
        }

        HttpResponseMessage blockedResponse;
        string blockedBody;
        TimeSpan elapsed;

        await using (var holder = scenario.Builder.CreateContext())
        {
            await using var tx = await holder.Database.BeginTransactionAsync().ConfigureAwait(true);
            try
            {
                // Блокування взяте, коли команда повернулась: далі PATCH гарантовано впирається в нього.
                await holder.Database
                    .ExecuteSqlRawAsync("DECLARE @n int = (SELECT COUNT(*) FROM doc.CellValue WITH (TABLOCKX, HOLDLOCK));")
                    .ConfigureAwait(true);

                var clock = Stopwatch.StartNew();
                (blockedResponse, blockedBody) = await PatchAsync().ConfigureAwait(true);
                elapsed = clock.Elapsed;
            }
            finally
            {
                await tx.RollbackAsync().ConfigureAwait(true);
            }
        }

        // ⛔ До виправлення: 500 ECR-SYS-0500 через ~30 с (Execution Timeout у MERGE).
        Assert.True(
            blockedResponse.StatusCode == HttpStatusCode.Conflict,
            $"Очікували 409, отримали {blockedResponse.StatusCode}\n{blockedBody}\n{factory.ErrorsText}");
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"Відмова прийшла за {elapsed}: LOCK_TIMEOUT не діє.");

        var problem = JsonDocument.Parse(blockedBody).RootElement;
        Assert.Equal("ECR-DOC-4091", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-4091.lockTimeout", problem.GetProperty("messageKey").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.DoesNotContain('{', detail);
        Assert.False(detail.Any(ch => ch is >= 'Ѐ' and <= 'ӿ'), $"Каталог не спрацював: «{detail}»");

        // Після звільнення блокування той самий запит проходить.
        var (retry, retryBody) = await PatchAsync().ConfigureAwait(true);
        Assert.True(
            retry.IsSuccessStatusCode,
            $"Після звільнення блокування: {retry.StatusCode}\n{retryBody}\n{factory.ErrorsText}");
    }

    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        var document = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var userName = $"lck_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"LCK_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Cell-lock writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(
            new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);

        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);

        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, userName, periodKey);
    }

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Document, string UserName, int PeriodKey);
}
