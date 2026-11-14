// tests/Ecr.Api.Tests/YearLayoutCalculatedOverlayApiTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
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
/// ✎ LAND-YEAR L2b: колонка <c>Calculated</c> місяця 11 версії <c>YearWithMonthColumns</c> (<c>CC11</c>) у річному перегляді
/// (якір 209901) показує число АКТУАЛЬНОГО ПРОГОНУ ПЕРІОДУ-ВЛАСНИКА 209911 з екземпляра-власника — не число прогону
/// перегляду. Справжній шлях: <c>GET /documents/{id}/tables/{instance}</c>, реальні сховища й служба доступу.
/// </summary>
/// <remarks>
/// Allow/Deny пара (жодних змін фільтрів доступу): два користувачі, що відрізняються лише областю ролі за періодами.
/// «Січень–листопад» бачить 111 (власник), «лише січень» — нічого: маска власника поза областю (D-214) діє і на
/// накладений результат. Прогін перегляду має для того самого рядка й виходу 999 — до L2b саме воно стояло в CC11.
/// Власна колонка якоря <c>CC1</c> (власник 209901, той самий вихід) — 999 з прогону перегляду, як і до L2b.
/// </remarks>
[Collection("SqlServer")]
public sealed class YearLayoutCalculatedOverlayApiTests(SqlServerFixture sql)
{
    private const string Password = "Year-Calc-Overlay-L2b!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const string Output = "OUT_L2B";
    private const int Jan = 209901;
    private const int Nov = 209911;

    private static readonly DateTime Now = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Рік_CC11_показує_число_прогону_листопада_і_лише_ролі_з_листопадом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        try
        {
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

            using var factory = app;

            // Allow: область «січень–листопад» — число ВЛАСНИКА 209911 (111), не прогону перегляду (999).
            var allowed = await ReadRowAsync(app, s, janToNov).ConfigureAwait(true);
            Assert.True(allowed.TryGetProperty(s.Codes["CC11"], out var visible), "CC11 у межах області ролі мусить мати число");
            Assert.Contains("111", visible.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("999", visible.GetRawText(), StringComparison.Ordinal);

            // Власна колонка якоря (той самий вихід) — з прогону перегляду, як і до L2b.
            Assert.True(allowed.TryGetProperty(s.Codes["CC1"], out var own), "CC1 якоря мусить мати число");
            Assert.Contains("999", own.GetRawText(), StringComparison.Ordinal);

            // Deny: область «лише січень» — CC11 (власник 209911) замаскована; CC1 видно.
            var denied = await ReadRowAsync(app, s, janOnly).ConfigureAwait(true);
            Assert.False(
                denied.TryGetProperty(s.Codes["CC11"], out var leaked),
                $"CC11 чужого місяця поза областю ролі мусить бути замаскована, а віддано: {leaked}");
            Assert.True(denied.TryGetProperty(s.Codes["CC1"], out _), "власна CC1 якоря мусить бути видимою");
        }
        finally
        {
            await using var db = s.Builder.CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE calc.CalculationRun SET Status = 'Superseded' WHERE ProjectId = {s.Doc.ProjectId} AND Status <> 'Superseded'")
                .ConfigureAwait(true);
        }
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
    /// Документ 209901 (якір) версії YearWithMonthColumns: «місяці в колонках», колонки <c>Calculated</c> CC1 (місяць 1) і
    /// CC11 (місяць 11), обидві прив'язані до одного виходу методології; календар 1..12 (Open). Прогін 209901: рядок → 999
    /// на екземплярі перегляду; прогін 209911: той самий рядок і вихід → 111 на екземплярі листопада.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Jan, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        var codes = new Dictionary<string, string>();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int versionId;
        await using (var db = builder.CreateContext())
        {
            var methodology = new Methodology(
                EcrCode.Create($"L2B{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "L2b" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync().ConfigureAwait(false);
            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);

            foreach (var m in new[] { 1, 11 })
            {
                var column = new ColumnDef(
                    doc.TableDefId, EcrCode.Create($"CC{m}_{doc.TableDefId}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = $"CC{m}" }), 100 + m, CellDataType.Calculated);
                column.SetMonth(true, (byte)m);
                db.ColumnDefs.Add(column);
                await db.SaveChangesAsync().ConfigureAwait(false);
                codes[$"CC{m}"] = column.Code;
                db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, column.Id, methodology.Id, Output, "{}"));
            }

            var template = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId).ConfigureAwait(false);
            db.Entry(template).Property(v => v.Layout).CurrentValue = TemplateLayout.YearWithMonthColumns;

            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (2099 * 100) + m;
                var period = key == Jan
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Jan).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2099, m, 1), new DateOnly(2099, m, DateTime.DaysInMonth(2099, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            versionId = version.Id;
        }

        var november = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov)).ConfigureAwait(false);

        string rowKey;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);

            var janRun = new CalculationRun(doc.ProjectId, Jan, triggeredByUserId: null, Now);
            janRun.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
            janRun.MakeCurrent();
            var novRun = new CalculationRun(doc.ProjectId, Nov, triggeredByUserId: null, Now);
            novRun.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
            novRun.MakeCurrent();
            db.CalculationRuns.AddRange(janRun, novRun);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);

            // Прогін перегляду: 999 на екземплярі перегляду; прогін листопада: 111 на екземплярі листопада.

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {janRun.Id}, {versionId}, {Jan}, {doc.DocumentId},
                        {rowKey}, {Output}, CAST(999 AS decimal(34,16)), {unit}, {doc.TableInstanceId}),
                       (NEXT VALUE FOR calc.CalculationResultSeq, {novRun.Id}, {versionId}, {Nov}, {doc.DocumentId},
                        {rowKey}, {Output}, CAST(111 AS decimal(34,16)), {unit}, {november.TableInstanceId})
                """).ConfigureAwait(false);
        }

        return new Scenario(builder, doc, rowKey, codes);
    }

    /// <summary>Користувач з роллю Document.View на проєкт і областю періодів <paramref name="from"/>..<paramref name="to"/>.</summary>
    private static async Task<string> AddUserAsync(Scenario s, int from, int to)
    {
        var userName = $"l2b_{Guid.NewGuid():N}"[..20];
        await using var db = s.Builder.CreateContext();
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"L2B{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L2b year calc overlay" }));
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
