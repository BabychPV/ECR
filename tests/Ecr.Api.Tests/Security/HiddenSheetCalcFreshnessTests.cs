// tests/Ecr.Api.Tests/Security/HiddenSheetCalcFreshnessTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Н-2 (оракул «застарілі числа»): <c>GET /documents/{id}/calculation-results</c> віддає <c>isStale</c> /
/// <c>inputsChangedAt</c> по ВСЬОМУ документу. Вузький читач, що бачить число з видимої колонки, не повинен
/// дізнаватися з цих полів, що після прогону змінено вхід схованого аркуша.
/// </summary>
/// <remarks>
/// ⚠ Матриця <c>HiddenSheetRouteMatrixTests</c> (маршрут <c>calc-fresh</c>) цього не ловить: її вихід прив'язаний і до
/// схованої колонки, тож вузькому читачу рядків не віддається взагалі (Н-1) і свіжість не досягається. Тут вихід
/// прив'язаний ЛИШЕ до видимої колонки — єдиний випадок, де свіжість опиняється у відповіді.
/// Контроль: роль без обмежень ті самі зміни бачить (без нього рівність «до/після» нічого не доводить).
/// </remarks>
[Collection("SqlServer")]
public sealed class HiddenSheetCalcFreshnessTests(SqlServerFixture sql)
{
    private const string Password = "Hidden-Sheet-Fresh-N2!";
    private static readonly DateTime RunAt = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Правка_схованого_аркуша_після_прогону_не_робить_числа_застарілими_для_вузького_читача()
    {
        var s = await ArrangeAsync(deny: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var before = await ResultsAsync(client, s).ConfigureAwait(true);
        await WriteChangeAsync(s.DocumentId, s.PeriodKey, s.RowKeyB, s.ColumnBId, RunAt.AddMinutes(30)).ConfigureAwait(true);
        var after = await ResultsAsync(client, s).ConfigureAwait(true);

        Assert.True(before.Status == HttpStatusCode.OK, $"{before.Status}: {before.Body}\n{app.ErrorsText}");
        Assert.True(after.Status == HttpStatusCode.OK, $"{after.Status}: {after.Body}\n{app.ErrorsText}");
        Assert.Contains(s.OutVisible, OutputCodes(after.Body));
        Assert.False(AnyStale(after.Body), $"вузький читач бачить isStale/inputsChangedAt від схованого аркуша\n{after.Body}");
        Assert.Equal(Freshness(before.Body), Freshness(after.Body));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_без_обмежень_бачить_числа_застарілими_після_правки_будь_якого_аркуша_контроль()
    {
        var s = await ArrangeAsync(deny: false).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var before = await ResultsAsync(client, s).ConfigureAwait(true);
        await WriteChangeAsync(s.DocumentId, s.PeriodKey, s.RowKeyB, s.ColumnBId, RunAt.AddMinutes(30)).ConfigureAwait(true);
        var after = await ResultsAsync(client, s).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, before.Status);
        Assert.False(AnyStale(before.Body), before.Body);
        Assert.True(AnyStale(after.Body), after.Body);
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private sealed record Reply(HttpStatusCode Status, string Body);

    private sealed record Scenario(
        long DocumentId, int PeriodKey, string UserName, string OutVisible, string RowKeyB, int ColumnBId);

    private static bool AnyStale(string body)
        => JsonDocument.Parse(body).RootElement.EnumerateArray().Any(r =>
            r.GetProperty("isStale").GetBoolean()
            || r.GetProperty("inputsChangedAt").ValueKind != JsonValueKind.Null);

    private static string Freshness(string body)
        => string.Join(
            "|",
            JsonDocument.Parse(body).RootElement.EnumerateArray().Select(r =>
                $"{r.GetProperty("isStale").GetBoolean()}/{r.GetProperty("inputsChangedAt")}/{r.GetProperty("changedRegistries")}"));

    private static List<string> OutputCodes(string body)
        => [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(r => r.GetProperty("outputCode").GetString()!)];

    private static async Task<Reply> ResultsAsync(HttpClient client, Scenario s)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results?periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative))
            .ConfigureAwait(false);

        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// Документ із видимим A і схованим (Deny) B; методологія з виходом, прив'язаним ЛИШЕ до колонки A; поточний
    /// прогін із числом для A. Правка B після прогону — крок тесту.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(bool deny)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var userName = $"frs{Guid.NewGuid():N}"[..20];
        var outVisible = $"OUTV_{tag}";
        var rowKeyB = $"RWB{tag}";

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"HIDFR{tag}"), Name($"HiddenFrName{tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"FRTB{tag}"), Name($"FrTable{tag}"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnB = new ColumnDef(tableB.Id, EcrCode.Create($"FRCB{tag}"), Name("Col B"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);
        db.RowDefs.Add(new RowDef(tableB.Id, RowKey.Create(rowKeyB), 1, Name("Row B"), RowKind.Item));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Fresh role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Document.View", "Calculation.View" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Write));
        if (deny)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));

        var methodology = new Methodology(EcrCode.Create($"FR{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, RunAt);
        db.MethodologyVersions.Add(version);
        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, outVisible, "{}"));

        var run = new CalculationRun(b.ProjectId, b.PeriodKey.Value, triggeredByUserId: null, RunAt);
        run.Complete("Succeeded", RunAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id},
                    {b.PeriodKey.Value}, {b.DocumentId}, N'row-1', {outVisible}, CAST(1 AS decimal(34,16)), {unitId})
            """).ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.PeriodKey.Value, userName, outVisible, rowKeyB, columnB.Id);
    }

    /// <summary>Рядок журналу — прямим ADO, бо <c>aud.*</c> поза моделлю EF.</summary>
    private async Task WriteChangeAsync(long documentId, int periodKey, string rowKey, int columnDefId, DateTime changedAt)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES
                (@changedAt, @periodKey, @documentId, 1, @rowKey, @columnDefId,
                 N'1', N'2', 1, N'UserEdit', 0);
            """;
        command.Parameters.AddWithValue("@changedAt", changedAt);
        command.Parameters.AddWithValue("@periodKey", periodKey);
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        command.Parameters.AddWithValue("@columnDefId", columnDefId);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
