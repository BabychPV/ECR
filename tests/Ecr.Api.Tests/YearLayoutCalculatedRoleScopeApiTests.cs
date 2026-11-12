// tests/Ecr.Api.Tests/YearLayoutCalculatedRoleScopeApiTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// LAND-YEAR L1 (D-474 варіант A) × D-214: обчислювана місячна колонка <c>CM11</c> версії з розкладкою
/// <c>YearWithMonthColumns</c> належить періоду 202611. Річний перегляд (якір 202601) віддає її значення ВЛАСНИКА лише
/// читачу, чия роль за періодами покриває листопад; читач з областю «лише січень» її не бачить — ні значення власника,
/// ні старої копії перегляду. Справжній шлях: <c>GET /documents/{id}/tables/{instance}</c>, реальна служба доступу.
/// </summary>
/// <remarks>
/// Диференційна пара (жодних змін фільтрів доступу): та сама база, два користувачі, що відрізняються лише областю
/// ролі. До L1 обидва бачили стару копію перегляду (999): обчислювана колонка не мала «чужого власника», тож маска
/// D-214 її не стосувалася.
/// </remarks>
[Collection("SqlServer")]
public sealed class YearLayoutCalculatedRoleScopeApiTests(SqlServerFixture sql)
{
    private const string Password = "Year-Calc-Scope-L1!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Jan = 202601;
    private const int Nov = 202611;

    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Рік_CM11_власника_бачить_лише_роль_з_листопадом_роль_лише_на_січень_не_бачить()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var janOnly = await AddUserAsync(s, Jan, Jan).ConfigureAwait(true);
        var janToNov = await AddUserAsync(s, Jan, Nov).ConfigureAwait(true);

        Environment.SetEnvironmentVariable(FlagVariable, "false");
        EcrApiFactory app;
        try
        {
            app = new EcrApiFactory(sql);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }

        using var _ = app;

        // Deny: область «лише січень» — CM11 (власник 202611) замаскована; власний CM1 (власник = якір) видно.
        var denied = await ReadRowAsync(app, s, janOnly).ConfigureAwait(true);
        Assert.False(
            denied.TryGetProperty(s.Codes["CM11"], out var leaked),
            $"CM11 чужого місяця поза областю ролі мусить бути замаскована, а віддано: {leaked}");
        Assert.True(denied.TryGetProperty(s.Codes["CM1"], out var own), "власна CM1 якоря мусить бути видимою");
        Assert.Contains("101", own.GetRawText(), StringComparison.Ordinal);

        // Allow: область «січень–листопад» — значення ВЛАСНИКА (111), не стара копія перегляду (999).
        var allowed = await ReadRowAsync(app, s, janToNov).ConfigureAwait(true);
        Assert.True(allowed.TryGetProperty(s.Codes["CM11"], out var visible), "CM11 у межах області ролі мусить бути видимою");
        Assert.Contains("111", visible.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("999", visible.GetRawText(), StringComparison.Ordinal);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Doc, string RowKey, Dictionary<string, string> Codes);

    private static async Task<JsonElement> ReadRowAsync(EcrApiFactory app, Scenario s, string userName)
    {
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}\n{app.ErrorsText}");

        return JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKey).GetProperty("cells").Clone();
    }

    /// <summary>
    /// Документ 202601 (якір року) версії YearWithMonthColumns: «місяці в колонках» з формулами-місяцями CM1, CM11;
    /// календар 1..12 (Open); у власнику 202611 — CM11 = 111, у перегляді — стара копія CM11 = 999; CM1 = 101 у якорі.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Jan, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        var codes = new Dictionary<string, string>();
        var ids = new Dictionary<string, int>();
        await using (var db = builder.CreateContext())
        {
            foreach (var m in new[] { 1, 11 })
            {
                var column = new ColumnDef(
                    doc.TableDefId, EcrCode.Create($"CM{m}_{doc.TableDefId}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = $"CM{m}" }), 100 + m, CellDataType.Formula);
                column.SetMonth(true, (byte)m);
                db.ColumnDefs.Add(column);
                await db.SaveChangesAsync().ConfigureAwait(false);
                ids[$"CM{m}"] = column.Id;
                codes[$"CM{m}"] = column.Code;
            }

            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId).ConfigureAwait(false);
            db.Entry(version).Property(v => v.Layout).CurrentValue = TemplateLayout.YearWithMonthColumns;

            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (2026 * 100) + m;
                var period = key == Jan
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Jan).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var november = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov)).ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            db.CellValues.Add(Calculated(new CellAddress(new PeriodKey(Nov), november.RowIds[0], ids["CM11"]), doc.TableDefId, 111m));
            db.CellValues.Add(Calculated(new CellAddress(new PeriodKey(Jan), doc.RowIds[0], ids["CM11"]), doc.TableDefId, 999m));
            db.CellValues.Add(Calculated(new CellAddress(new PeriodKey(Jan), doc.RowIds[0], ids["CM1"]), doc.TableDefId, 101m));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        string rowKey;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        return new Scenario(builder, doc, rowKey, codes);
    }

    private static CellValue Calculated(CellAddress address, int tableDefId, decimal value)
        => new(address, tableDefId, new CellValueData { ValueNumeric = value, IsCalculated = true });

    /// <summary>Користувач з роллю Document.View на проєкт і областю періодів <paramref name="from"/>..<paramref name="to"/>.</summary>
    private static async Task<string> AddUserAsync(Scenario s, int from, int to)
    {
        var userName = $"l1y_{Guid.NewGuid():N}"[..20];
        await using var db = s.Builder.CreateContext();
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"L1Y{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L1 year calc scope" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        var assignment = new RoleAssignment(role.Id, user.Id, null);
        assignment.SetScope(RoleAssignmentScope.Create(
            [s.Doc.ProjectId], periodFrom: new PeriodKey(from), periodTo: new PeriodKey(to)));
        db.RoleAssignments.Add(assignment);
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Submit));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userName;
    }
}
